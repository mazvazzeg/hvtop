namespace hvtop.Native;

internal static class SmbSampler
{
    private static readonly object Gate = new();
    private static DateTime lastInventoryAt = DateTime.MinValue;
    private static SmbInventory inventory = SmbInventory.Empty;

    public static SmbSample Read(string hostName, string serverVersion, TimeSpan? uptime)
    {
        var currentInventory = EnsureInventory();
        var shareCounters = ReadShareCounters();
        var clientTraffic = SmbTcpConnectionSampler.Read();

        var shares = currentInventory.Shares
            .OrderBy(share => share.Name, StringComparer.OrdinalIgnoreCase)
            .Select(share =>
            {
                var counters = shareCounters.For(share.Name);
                var userCount = currentInventory.Sessions
                    .Where(session => session.OpenFileCount > 0)
                    .Select(session => session.ClientUser)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                var openCount = currentInventory.OpenFiles.Count(file => ShareMatchesPath(share, file.Path));
                var status = SmbStatus(counters.IoMbps, openCount, userCount);
                return new SmbShareRow(
                    hostName,
                    share.Name,
                    userCount,
                    openCount,
                    Metric.Iops(counters.Iops),
                    Metric.Mbps(counters.IoMbps),
                    Metric.Mbps(counters.RxMbps),
                    Metric.Mbps(counters.TxMbps),
                    status);
            })
            .ToArray();

        var usersPerAddress = currentInventory.Sessions
            .GroupBy(session => SmbTcpConnectionSampler.NormalizeAddress(session.ClientAddress), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(session => session.ClientUser).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                StringComparer.OrdinalIgnoreCase);

        var clients = currentInventory.Sessions
            .GroupBy(
                session => $"{SmbTcpConnectionSampler.NormalizeAddress(session.ClientAddress)}\0{session.ClientUser}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                var clientAddress = SmbTcpConnectionSampler.NormalizeAddress(first.ClientAddress);
                var openCount = group.Sum(session => session.OpenFileCount);
                var traffic = usersPerAddress.GetValueOrDefault(clientAddress) == 1
                    ? clientTraffic.For(clientAddress)
                    : SmbTcpTraffic.Unavailable;
                var io = SumFinite([traffic.RxMbps, traffic.TxMbps]);
                return new SmbClientRow(
                    hostName,
                    first.ClientUser,
                    openCount,
                    Metric.Iops(double.NaN),
                    Metric.Mbps(io),
                    Metric.Mbps(traffic.RxMbps),
                    Metric.Mbps(traffic.TxMbps),
                    SmbStatus(io, openCount, 1));
            })
            .OrderByDescending(row => double.IsFinite(row.Io.Current) ? row.Io.Current : -1)
            .ThenByDescending(row => row.OpenFileCount)
            .ThenBy(row => row.ClientUser, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();

        var openFiles = currentInventory.OpenFiles
            .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var users = group.Select(file => file.ClientUser).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                return new SmbOpenFileRow(
                    hostName,
                    group.Key,
                    users,
                    Metric.Iops(double.NaN),
                    Metric.Mbps(double.NaN),
                    Metric.Mbps(double.NaN),
                    Metric.Mbps(double.NaN),
                    users > 0 ? "OK" : "IDLE");
            })
            .OrderByDescending(row => row.UserCount)
            .ThenBy(row => row.Path, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();

        var totalRx = SumFinite(shares.Select(share => share.Rx.Current));
        var totalTx = SumFinite(shares.Select(share => share.Tx.Current));
        var totalIo = SumFinite(shares.Select(share => share.Io.Current));
        if (shares.Length == 0 || shareCounters.IsEmpty)
        {
            totalRx = double.NaN;
            totalTx = double.NaN;
            totalIo = double.NaN;
        }

        var host = new SmbHostRow(
            hostName,
            serverVersion,
            uptime,
            EffectiveSmbVersion(currentInventory, serverVersion),
            currentInventory.Shares.Length,
            currentInventory.Sessions.Select(session => session.ClientUser).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            currentInventory.OpenFiles.Length,
            currentInventory.Features,
            Metric.Mbps(totalIo),
            Metric.Mbps(totalRx),
            Metric.Mbps(totalTx),
            currentInventory.Available ? SmbStatus(totalIo, currentInventory.OpenFiles.Length, currentInventory.Sessions.Length) : "N/A");

        return new SmbSample([host], shares, clients, openFiles);
    }

    private static SmbInventory EnsureInventory()
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - lastInventoryAt < TimeSpan.FromSeconds(10))
                return inventory;

            inventory = ReadInventory();
            lastInventoryAt = DateTime.UtcNow;
            return inventory;
        }
    }

    private static SmbInventory ReadInventory()
    {
        const string script =
            "function _hvprop($o,$n){ try { $p=$o.PSObject.Properties[$n]; if($p){ [string]$p.Value } else { '' } } catch { '' } }; " +
            "$cfg=$null; try { $cfg=Get-SmbServerConfiguration -ErrorAction Stop } catch { }; " +
            "$shares=@(); try { $shares=@(Get-SmbShare -ErrorAction Stop | Where-Object { -not $_.Special } | Select-Object Name,Path) } catch { }; " +
            "$sessions=@(); try { $sessions=@(Get-SmbSession -ErrorAction Stop | Select-Object ClientComputerName,ClientUserName,NumOpens,Dialect) } catch { }; " +
            "$files=@(); try { $files=@(Get-SmbOpenFile -ErrorAction Stop | Select-Object Path,ClientComputerName,ClientUserName,ShareRelativePath) } catch { }; " +
            "$features=@(); if($cfg){ foreach($n in @('EnableSMB1Protocol','EnableSMB2Protocol','EnableSMBQUIC','EnableSMBDirect','EnableMultiChannel','EnableLeasing','EnableOplocks','EnableSecuritySignature','EncryptData','RequireSecuritySignature')){ $p=$cfg.PSObject.Properties[$n]; if($p){ $features += ($n + '=' + [string]$p.Value) } } }; " +
            "$rdma=$false; try { $rdma=@(Get-SmbServerNetworkInterface -ErrorAction Stop | Where-Object { $_.RdmaCapable -eq $true }).Count -gt 0 } catch { try { $rdma=@(Get-NetAdapterRdma -ErrorAction Stop | Where-Object { $_.Enabled -eq $true }).Count -gt 0 } catch { } }; " +
            "$features += ('RdmaInterface=' + [string]$rdma); " +
            "$dialects=@($sessions | ForEach-Object { _hvprop $_ 'Dialect' } | Where-Object { $_ }); " +
            "$smbver=if($dialects.Count -gt 0){ ($dialects | Sort-Object -Unique) -join ',' } elseif($cfg){ 'server' } else { 'n/a' }; " +
            "[pscustomobject]@{Available=[bool]($cfg -or $shares.Count -gt 0 -or $sessions.Count -gt 0 -or $files.Count -gt 0); SmbVersion=$smbver; Features=($features -join ', '); Shares=$shares; Sessions=$sessions; OpenFiles=$files} | ConvertTo-Json -Depth 6 -Compress";

        if (!PowerShellRunner.TryRun(script, 8000, out var output) || string.IsNullOrWhiteSpace(output))
            return SmbInventory.Empty;

        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            var shares = ReadShares(root);
            var sessions = ReadSessions(root);
            var files = ReadOpenFiles(root);
            return new SmbInventory(
                ReadBool(root, "Available"),
                ReadString(root, "SmbVersion", "n/a"),
                ReadString(root, "Features", "n/a"),
                shares,
                sessions,
                files);
        }
        catch
        {
            return SmbInventory.Empty;
        }
    }

    private static SmbShareInventory[] ReadShares(JsonElement root)
    {
        if (!TryGetArray(root, "Shares", out var array))
            return [];

        return array.EnumerateArray()
            .Select(item => new SmbShareInventory(ReadString(item, "Name", "n/a"), ReadString(item, "Path", string.Empty)))
            .Where(share => !string.IsNullOrWhiteSpace(share.Name))
            .ToArray();
    }

    private static SmbSessionInventory[] ReadSessions(JsonElement root)
    {
        if (!TryGetArray(root, "Sessions", out var array))
            return [];

        return array.EnumerateArray()
            .Select(item =>
            {
                var client = ReadString(item, "ClientComputerName", "n/a");
                var user = ReadString(item, "ClientUserName", "n/a");
                return new SmbSessionInventory(client, $"{client}\\{user}", ReadInt(item, "NumOpens"), ReadString(item, "Dialect", string.Empty));
            })
            .Where(session => !string.IsNullOrWhiteSpace(session.ClientUser))
            .ToArray();
    }

    private static SmbOpenFileInventory[] ReadOpenFiles(JsonElement root)
    {
        if (!TryGetArray(root, "OpenFiles", out var array))
            return [];

        return array.EnumerateArray()
            .Select(item =>
            {
                var client = ReadString(item, "ClientComputerName", "n/a");
                var user = ReadString(item, "ClientUserName", "n/a");
                return new SmbOpenFileInventory(ReadString(item, "Path", string.Empty), $"{client}\\{user}");
            })
            .Where(file => !string.IsNullOrWhiteSpace(file.Path))
            .ToArray();
    }

    private static bool TryGetArray(JsonElement root, string property, out JsonElement array)
    {
        array = default;
        if (!root.TryGetProperty(property, out var value))
            return false;

        if (value.ValueKind == JsonValueKind.Array)
        {
            array = value;
            return true;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            using var doc = JsonDocument.Parse($"[{value.GetRawText()}]");
            array = doc.RootElement.Clone();
            return true;
        }

        return false;
    }

    private static SmbCounterMap ReadShareCounters()
    {
        try
        {
            var values = PdhWildcardReader.ReadMany(
                [
                    ("data", @"\SMB Server Shares(*)\Data Bytes/sec"),
                    ("read", @"\SMB Server Shares(*)\Read Bytes/sec"),
                    ("write", @"\SMB Server Shares(*)\Write Bytes/sec"),
                    ("requests", @"\SMB Server Shares(*)\Requests/sec")
                ],
                NormalizeShareInstance);

            return new SmbCounterMap(
                values.GetValueOrDefault("data")?.Values ?? [],
                values.GetValueOrDefault("read")?.Values ?? [],
                values.GetValueOrDefault("write")?.Values ?? [],
                values.GetValueOrDefault("requests")?.Values ?? []);
        }
        catch
        {
            return SmbCounterMap.Empty;
        }
    }

    private static string NormalizeShareInstance(string instance)
    {
        var value = instance.Trim();
        var slash = value.LastIndexOf('\\');
        if (slash >= 0 && slash < value.Length - 1)
            value = value[(slash + 1)..];
        return value;
    }

    private static bool ShareMatchesPath(SmbShareInventory share, string path)
        => !string.IsNullOrWhiteSpace(share.Path)
           && path.StartsWith(share.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static string SmbStatus(double ioMbps, int openFiles, int users)
    {
        if (double.IsNaN(ioMbps))
            return openFiles == 0 && users == 0 ? "IDLE" : "OK";
        if (ioMbps >= 300) return "HOT";
        if (ioMbps >= 100 || openFiles >= 1000 || users >= 100) return "BUSY";
        if (ioMbps <= 0.01 && openFiles == 0 && users == 0) return "IDLE";
        return "OK";
    }

    private static string EffectiveSmbVersion(SmbInventory currentInventory, string serverVersion)
    {
        var detected = NormalizeDialectList(currentInventory.SmbVersion);
        if (!string.IsNullOrWhiteSpace(detected)
            && !detected.Equals("server", StringComparison.OrdinalIgnoreCase)
            && !detected.Equals("n/a", StringComparison.OrdinalIgnoreCase))
            return detected;

        var smb2Enabled = FeatureEnabled(currentInventory.Features, "EnableSMB2Protocol");
        if (smb2Enabled == false)
            return FeatureEnabled(currentInventory.Features, "EnableSMB1Protocol") == true ? "1.0" : "n/a";

        var build = ParseWindowsBuild(serverVersion);
        if (build >= 14393
            || ContainsServerVersion(serverVersion, "WIN10")
            || ContainsServerVersion(serverVersion, "WIN11")
            || ContainsServerVersion(serverVersion, "SRV2016")
            || ContainsServerVersion(serverVersion, "SRV2019")
            || ContainsServerVersion(serverVersion, "SRV2022")
            || ContainsServerVersion(serverVersion, "SRV2025"))
            return "3.1.1";

        if (ContainsServerVersion(serverVersion, "SRV2012R2") || build == 9600)
            return "3.02";
        if (ContainsServerVersion(serverVersion, "SRV2012") || build == 9200)
            return "3.0";
        if (ContainsServerVersion(serverVersion, "SRV2008R2") || build is 7600 or 7601)
            return "2.1";
        if (ContainsServerVersion(serverVersion, "SRV2008") || build is 6001 or 6002)
            return "2.0";

        return smb2Enabled == true ? "3.x" : "n/a";
    }

    private static bool ContainsServerVersion(string serverVersion, string value)
        => serverVersion.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static int ParseWindowsBuild(string serverVersion)
    {
        var text = serverVersion ?? string.Empty;
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && char.IsDigit(text[i]))
            {
                if (start < 0)
                    start = i;
                continue;
            }

            if (start >= 0)
            {
                var token = text[start..i];
                if (token.Length is >= 4 and <= 5 && int.TryParse(token, out var build) && build >= 6000)
                    return build;
                start = -1;
            }
        }

        return 0;
    }

    private static string NormalizeDialectList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "n/a";

        var dialects = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeDialect)
            .Where(dialect => !string.IsNullOrWhiteSpace(dialect))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(dialect => dialect, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return dialects.Length == 0 ? value : string.Join(",", dialects);
    }

    private static string NormalizeDialect(string value)
    {
        var normalized = value.Trim();
        return normalized.ToLowerInvariant() switch
        {
            "0x0311" or "0311" or "785" or "smb311" or "3.11" => "3.1.1",
            "0x0302" or "0302" or "770" or "smb302" or "3.02" => "3.0.2",
            "0x0300" or "0300" or "768" or "smb300" => "3.0",
            "0x0210" or "0210" or "528" or "smb210" => "2.1",
            "0x0202" or "0202" or "514" or "smb202" => "2.02",
            "0x0200" or "0200" or "512" or "smb200" => "2.0",
            _ => normalized
        };
    }

    private static bool? FeatureEnabled(string features, string name)
    {
        foreach (var part in features.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = part[..separator].Trim();
            if (!key.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = part[(separator + 1)..].Trim();
            if (bool.TryParse(value, out var result))
                return result;
        }

        return null;
    }

    private static double SumFinite(IEnumerable<double> values)
    {
        var finite = values.Where(value => !double.IsNaN(value) && !double.IsInfinity(value)).ToArray();
        return finite.Length == 0 ? double.NaN : finite.Sum();
    }

    private static string ReadString(JsonElement element, string property, string fallback)
        => element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : fallback;

    private static int ReadInt(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : 0;

    private static bool ReadBool(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
}

internal sealed record SmbSample(SmbHostRow[] Hosts, SmbShareRow[] Shares, SmbClientRow[] Clients, SmbOpenFileRow[] OpenFiles);

internal sealed record SmbInventory(bool Available, string SmbVersion, string Features, SmbShareInventory[] Shares, SmbSessionInventory[] Sessions, SmbOpenFileInventory[] OpenFiles)
{
    public static SmbInventory Empty { get; } = new(false, "n/a", "n/a", [], [], []);
}

internal sealed record SmbShareInventory(string Name, string Path);

internal sealed record SmbSessionInventory(string ClientAddress, string ClientUser, int OpenFileCount, string Dialect);

internal sealed record SmbOpenFileInventory(string Path, string ClientUser);

internal sealed class SmbCounterMap(
    IReadOnlyDictionary<string, double> data,
    IReadOnlyDictionary<string, double> read,
    IReadOnlyDictionary<string, double> write,
    IReadOnlyDictionary<string, double> requests)
{
    public static SmbCounterMap Empty { get; } = new(
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase));

    public bool IsEmpty => data.Count == 0 && read.Count == 0 && write.Count == 0 && requests.Count == 0;

    public SmbCounterValues For(string shareName)
    {
        var readBytes = Read(read, shareName);
        var writeBytes = Read(write, shareName);
        var dataBytes = Read(data, shareName);
        var rx = double.IsNaN(writeBytes) ? double.NaN : writeBytes / 1024d / 1024d;
        var tx = double.IsNaN(readBytes) ? double.NaN : readBytes / 1024d / 1024d;
        var io = !double.IsNaN(readBytes) || !double.IsNaN(writeBytes)
            ? (double.IsNaN(rx) ? 0 : rx) + (double.IsNaN(tx) ? 0 : tx)
            : double.IsNaN(dataBytes) ? double.NaN : dataBytes / 1024d / 1024d;
        return new SmbCounterValues(io, rx, tx, Read(requests, shareName));
    }

    private static double Read(IReadOnlyDictionary<string, double> values, string key)
    {
        if (values.TryGetValue(key, out var value))
            return value;

        foreach (var pair in values)
        {
            if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
                || pair.Key.EndsWith("\\" + key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return double.NaN;
    }
}

internal sealed record SmbCounterValues(double IoMbps, double RxMbps, double TxMbps, double Iops);
