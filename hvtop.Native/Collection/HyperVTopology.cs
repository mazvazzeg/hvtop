namespace hvtop.Native;

internal sealed class HyperVTopology
{
    private readonly object gate = new();
    private VmTopologyRow[] cache = [];
    private NetworkSwitchTopologyRow[] switchCache = [];
    private string? lastEventMessage;
    private string? pendingEventMessage;
    private string pendingEventSeverity = "INFO";
    public bool IsRefreshing { get; private set; }
    private bool isCheckpointRefreshing;
    private DateTime lastCheckpointRefreshUtc = DateTime.MinValue;
    private DiskRow[] latestDisks = [];
    private NetworkRow[] latestNetworks = [];

    public void RequestRefresh()
    {
        lock (gate)
        {
            if (IsRefreshing) return;
            IsRefreshing = true;
            var disks = latestDisks;
            var networks = latestNetworks;
            _ = Task.Run(() => RefreshAsync(disks, networks));
        }
    }

    public HyperVTopologyResult TryRead(DiskRow[] disks, NetworkRow[] networks)
    {
        lock (gate)
        {
            latestDisks = disks;
            latestNetworks = networks;

            var eventMessage = pendingEventMessage;
            var eventSeverity = pendingEventSeverity;
            pendingEventMessage = null;
            return new HyperVTopologyResult(cache, switchCache, "PowerShell", eventMessage, eventSeverity);
        }
    }

    public void RequestCheckpointRefreshIfDue(TimeSpan interval)
    {
        Dictionary<string, VDiskRow[]> diskMap;
        lock (gate)
        {
            var now = DateTime.UtcNow;
            if (IsRefreshing || isCheckpointRefreshing || cache.Length == 0 || now - lastCheckpointRefreshUtc < interval)
                return;

            isCheckpointRefreshing = true;
            lastCheckpointRefreshUtc = now;
            diskMap = cache
                .Where(vm => vm.Disks.Length > 0)
                .ToDictionary(vm => vm.VmName, vm => vm.Disks, StringComparer.OrdinalIgnoreCase);
        }

        _ = Task.Run(() => RefreshCheckpointsAsync(diskMap));
    }

    private void RefreshAsync(DiskRow[] disks, NetworkRow[] networks)
    {
        try
        {
            var data = TryReadPowerShell(disks, networks);
            lock (gate)
            {
                if (data.Topology.Length > 0 || cache.Length == 0)
                    cache = data.Topology;
                if (data.Switches.Length > 0 || switchCache.Length == 0)
                    switchCache = data.Switches;
                lastCheckpointRefreshUtc = DateTime.UtcNow;
                if (data.Topology.Length == 0 && data.Switches.Length == 0)
                {
                    pendingEventMessage = DedupEvent("Hyper-V network topology not detected; using Windows network adapter view.");
                    pendingEventSeverity = "INFO";
                }
                else
                {
                    pendingEventMessage = DedupEvent("Hyper-V native WMI topology disabled in single-file build, using PowerShell fallback.");
                    pendingEventSeverity = "WARN";
                }
            }
        }
        catch
        {
            lock (gate)
            {
                pendingEventMessage = DedupEvent("Hyper-V topology unavailable; using Windows network adapter view.");
                pendingEventSeverity = "WARN";
            }
        }
        finally
        {
            lock (gate)
                IsRefreshing = false;
        }
    }

    private void RefreshCheckpointsAsync(Dictionary<string, VDiskRow[]> diskMap)
    {
        try
        {
            var checkpointMap = TryReadPowerShellCheckpoints(diskMap);
            lock (gate)
            {
                cache = MergeCheckpointTopology(cache, checkpointMap);
            }
        }
        catch
        {
            lock (gate)
            {
                pendingEventMessage = DedupEvent("Hyper-V checkpoint refresh unavailable; keeping previous checkpoint topology.");
                pendingEventSeverity = "WARN";
            }
        }
        finally
        {
            lock (gate)
                isCheckpointRefreshing = false;
        }
    }

    private static VmTopologyRow[] MergeCheckpointTopology(VmTopologyRow[] topology, Dictionary<string, VmCheckpointRow[]> checkpointMap)
    {
        var existing = topology.ToDictionary(row => row.VmName, StringComparer.OrdinalIgnoreCase);
        return topology
            .Select(row => row with { Checkpoints = checkpointMap.TryGetValue(row.VmName, out var checkpoints) ? checkpoints : [] })
            .Concat(checkpointMap.Keys
                .Where(name => !existing.ContainsKey(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => new VmTopologyRow(name, [], [], checkpointMap[name])))
            .OrderBy(row => row.VmName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static HyperVTopologyData TryReadPowerShell(DiskRow[] disks, NetworkRow[] networks)
    {
        var diskMap = TryReadPowerShellDisks(disks);
        var checkpointMap = TryReadPowerShellCheckpoints(diskMap);
        var switches = TryReadPowerShellSwitches();
        var netMap = TryReadPowerShellNetworks(switches).ToDictionary(vm => vm.VmName, StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(diskMap.Keys.Concat(netMap.Keys).Concat(checkpointMap.Keys), StringComparer.OrdinalIgnoreCase);
        return new HyperVTopologyData(
            names
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new VmTopologyRow(
                name,
                diskMap.TryGetValue(name, out var vmDisks) ? vmDisks : [],
                netMap.TryGetValue(name, out var vmNets) ? vmNets.Networks : [],
                checkpointMap.TryGetValue(name, out var vmCheckpoints) ? vmCheckpoints : []))
            .ToArray(),
            switches);
    }

    private static Dictionary<string, VDiskRow[]> TryReadPowerShellDisks(DiskRow[] disks)
    {
        if (!PowerShellRunner.TryRun("Import-Module Hyper-V -ErrorAction Stop; Get-VM | Get-VMHardDiskDrive | Select-Object VMName,Path | ConvertTo-Csv -NoTypeInformation", 5000, out var output))
            return new(StringComparer.OrdinalIgnoreCase);

        var storageCounters = VirtualDiskCounterSampler.Read();
        return output
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(HyperVInventory.ParseCsvLine)
            .Where(parts => parts.Length >= 2 && IsVirtualDiskPath(parts[1]))
            .GroupBy(parts => parts[0].Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(parts =>
                {
                    var path = parts[1]?.Trim() ?? string.Empty;
                    var diskName = Path.GetFileName(path);
                    var stats = HyperVNaming.ResolveDiskStats(storageCounters, path, diskName);
                    stats ??= new VirtualDiskStats(0, 0, 0, 0);
                    return new VDiskRow(
                        string.IsNullOrWhiteSpace(diskName) ? path : diskName,
                        path,
                        ResolveStorageName(path, disks),
                        stats.ReadMbps,
                        stats.ReadIops,
                        stats.WriteMbps,
                        stats.WriteIops);
                }).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, VmCheckpointRow[]> TryReadPowerShellCheckpoints(Dictionary<string, VDiskRow[]> diskMap)
    {
        const string script = "$ErrorActionPreference='Stop'; Import-Module Hyper-V -ErrorAction Stop | Out-Null; " +
            "$rows=@(); " +
            "$checkpoints=@(); " +
            "try { if (Get-Command Get-VMCheckpoint -ErrorAction SilentlyContinue) { $checkpoints=@(Get-VMCheckpoint -VMName * -ErrorAction SilentlyContinue) } } catch { } ; " +
            "if ($checkpoints.Count -eq 0) { try { $checkpoints=@(Get-VMSnapshot -VMName * -ErrorAction SilentlyContinue) } catch { } } ; " +
            "foreach ($cp in $checkpoints) { " +
            "  $vmName=''; try { if ($cp.PSObject.Properties.Match('VMName').Count -gt 0) { $vmName=[string]$cp.VMName } } catch { } ; " +
            "  if ([string]::IsNullOrWhiteSpace($vmName)) { try { if ($cp.PSObject.Properties.Match('VM').Count -gt 0 -and $cp.VM) { $vmName=[string]$cp.VM.Name } } catch { } } ; " +
            "  $parentName=''; try { if ($cp.PSObject.Properties.Match('ParentSnapshotName').Count -gt 0) { $parentName=[string]$cp.ParentSnapshotName } } catch { } ; " +
            "  if (-not [string]::IsNullOrWhiteSpace($vmName) -and -not [string]::IsNullOrWhiteSpace([string]$cp.Name)) { " +
            "    $rows += [pscustomobject]@{ VMName=$vmName; Name=[string]$cp.Name; ParentName=$parentName; CreationTime=$cp.CreationTime; Path=''; ParentPath='' } " +
            "  } " +
            "} ; " +
            "foreach ($vm in @(Get-VM -ErrorAction Stop)) { " +
            "  foreach ($drive in @($vm | Get-VMHardDiskDrive -ErrorAction SilentlyContinue)) { " +
            "    if ($drive.Path -and ([string]$drive.Path).ToLowerInvariant().EndsWith('.avhdx')) { " +
            "      $rows += [pscustomobject]@{ VMName=$vm.Name; Name='Now / Active differencing disk'; ParentName=''; CreationTime=$null; Path=[string]$drive.Path; ParentPath='' } " +
            "    } " +
            "  } " +
            "} ; @($rows | Sort-Object VMName,Path,Name -Unique) | ConvertTo-Json -Compress -Depth 4";

        var fromPowerShell = new Dictionary<string, VmCheckpointRow[]>(StringComparer.OrdinalIgnoreCase);
        if (PowerShellRunner.TryRun(script, 20000, out var output) && !string.IsNullOrWhiteSpace(output))
            fromPowerShell = ParseCheckpointJson(output);

        var activeAvhdx = diskMap
            .SelectMany(kvp => kvp.Value
                .Where(disk => disk.Path.EndsWith(".avhdx", StringComparison.OrdinalIgnoreCase))
                .Select(disk => new { VmName = kvp.Key, Disk = disk }))
            .GroupBy(item => item.VmName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => new VmCheckpointRow("Now / Active differencing disk", string.Empty, item.Disk.Path, string.Empty, DateTime.MinValue))
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        return fromPowerShell.Keys.Concat(activeAvhdx.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                name => name,
                name => fromPowerShell.GetValueOrDefault(name, [])
                    .Concat(activeAvhdx.GetValueOrDefault(name, []))
                    .Where(row => !string.IsNullOrWhiteSpace(row.Name) || !string.IsNullOrWhiteSpace(row.Path))
                    .DistinctBy(row => string.IsNullOrWhiteSpace(row.Path) ? $"checkpoint:{row.Name}:{row.Created:O}" : $"path:{row.Path}", StringComparer.OrdinalIgnoreCase)
                    .OrderBy(row => row.Created == DateTime.MinValue ? DateTime.MaxValue : row.Created)
                    .ThenBy(row => row.Path, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static VmTopologyRow[] TryReadPowerShellNetworks(NetworkSwitchTopologyRow[] switches)
    {
        const string script = "Import-Module Hyper-V -ErrorAction Stop; @(Get-VMNetworkAdapter -VMName * | Select-Object VMName,Name,SwitchName) | ConvertTo-Json -Compress";
        if (!PowerShellRunner.TryRun(script, 5000, out var output))
            return [];

        return ParseVmNetworkJson(output, switches);
    }

    private static NetworkSwitchTopologyRow[] TryReadPowerShellSwitches()
    {
        const string script = "Import-Module Hyper-V -ErrorAction Stop | Out-Null; " +
            "$netAdapters = @(Get-NetAdapter | Where-Object { $_.HardwareInterface -eq $true } | Select-Object Name,InterfaceDescription,Status,LinkSpeed); " +
            "$switches = @(Get-VMSwitch | ForEach-Object { " +
            "$sw = $_; $members=@(); $teamMode=''; " +
            "$descs=@(); " +
            "if ($sw.PSObject.Properties.Match('NetAdapterInterfaceDescriptions').Count -gt 0) { $descs = @($sw.NetAdapterInterfaceDescriptions) } " +
            "elseif ($sw.PSObject.Properties.Match('NetAdapterInterfaceDescription').Count -gt 0 -and $sw.NetAdapterInterfaceDescription) { $descs = @($sw.NetAdapterInterfaceDescription) } " +
            "try { if ($sw.PSObject.Properties.Match('EmbeddedTeamingEnabled').Count -gt 0 -and [bool]$sw.EmbeddedTeamingEnabled) { $teamMode='SET' } } catch { } ; " +
            "try { if (Get-Command Get-VMSwitchTeam -ErrorAction SilentlyContinue) { " +
            "$setTeam = Get-VMSwitchTeam -Name $sw.Name -ErrorAction SilentlyContinue; " +
            "if ($setTeam) { $teamMode='SET'; " +
            "if ($setTeam.PSObject.Properties.Match('NetAdapterInterfaceDescriptions').Count -gt 0) { $members += @($setTeam.NetAdapterInterfaceDescriptions) } " +
            "elseif ($setTeam.PSObject.Properties.Match('NetAdapterInterfaceDescription').Count -gt 0) { $members += @($setTeam.NetAdapterInterfaceDescription) } " +
            "if ($setTeam.PSObject.Properties.Match('NetAdapterNames').Count -gt 0) { $members += @($setTeam.NetAdapterNames) } " +
            "elseif ($setTeam.PSObject.Properties.Match('NetAdapterName').Count -gt 0) { $members += @($setTeam.NetAdapterName) } " +
            "} } } catch { } ; " +
            "if ([string]::IsNullOrWhiteSpace($teamMode)) { try { if (Get-Command Get-NetLbfoTeam -ErrorAction SilentlyContinue) { " +
            "$bound=@(Get-NetAdapter | Where-Object { $descs -contains $_.InterfaceDescription -or $descs -contains $_.Name }); " +
            "foreach ($adapter in $bound) { $team=@(Get-NetLbfoTeam -ErrorAction Stop | Where-Object { $_.Name -eq $adapter.Name -or $_.TeamNics -contains $adapter.Name } | Select-Object -First 1); " +
            "if ($team.Count -gt 0) { $teamMode='LBFO'; $members += @(Get-NetLbfoTeamMember -Team $team[0].Name -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name); break } } " +
            "} } catch { } } " +
            "$keys=@($members + $descs | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique); " +
            "$uplinks=@(); " +
            "foreach ($key in $keys) { " +
            "$matches = @($netAdapters | Where-Object { $_.Name -eq $key -or $_.InterfaceDescription -eq $key -or $_.Name -like ('*' + $key + '*') -or $_.InterfaceDescription -like ('*' + $key + '*') -or $key -like ('*' + $_.Name + '*') -or $key -like ('*' + $_.InterfaceDescription + '*') } | Select-Object Name,InterfaceDescription,Status,LinkSpeed -Unique); " +
            "foreach ($match in $matches) { " +
            "if ($match.Name -like 'vEthernet*') { continue } " +
            "$uplinks += [pscustomobject]@{ Name=[string]$match.Name; Description=[string]$match.InterfaceDescription; Link=[string]$match.LinkSpeed; IsUp=[bool]($match.Status -eq 'Up') } " +
            "} " +
            "} ; " +
            "$uplinks = @($uplinks | Group-Object Name | ForEach-Object { $_.Group | Select-Object -First 1 }); " +
            "[pscustomobject]@{ Name=$sw.Name; SwitchType=[string]$sw.SwitchType; Uplinks=@($uplinks); TeamMode=$teamMode } " +
            "}); @($switches) | ConvertTo-Json -Compress -Depth 4";
        if (!PowerShellRunner.TryRun(script, 7000, out var output))
            return [];

        return ParseSwitchTopologyJson(output);
    }

    private static VmTopologyRow[] ParseVmNetworkJson(string json, NetworkSwitchTopologyRow[] switches)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            var rows = document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => document.RootElement.EnumerateArray().ToArray(),
                JsonValueKind.Object => [document.RootElement],
                _ => []
            };

            var switchMap = switches.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
            return rows
                .Select(element => new
                {
                    VmName = HyperVInventory.ReadJsonString(element, "VMName"),
                    Name = HyperVInventory.ReadJsonString(element, "Name"),
                    SwitchName = HyperVInventory.ReadJsonString(element, "SwitchName")
                })
                .Where(row => !string.IsNullOrWhiteSpace(row.VmName))
                .GroupBy(row => row.VmName, StringComparer.OrdinalIgnoreCase)
                .Select(group => new VmTopologyRow(
                    group.Key,
                    [],
                    group.Select(row =>
                    {
                        var switchName = string.IsNullOrWhiteSpace(row.SwitchName) ? "n/a" : row.SwitchName;
                        var uplinkSummary = switchMap.TryGetValue(switchName, out var switchRow)
                            ? BuildUplinkSummary(switchRow.Uplinks)
                            : "n/a";
                        return new VmNetworkPathRow(
                            string.IsNullOrWhiteSpace(row.Name) ? "Ethernet" : row.Name,
                            switchName,
                            uplinkSummary);
                    })
                    .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                    []))
                .OrderBy(row => row.VmName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static Dictionary<string, VmCheckpointRow[]> ParseCheckpointJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var document = JsonDocument.Parse(json);
            var rows = document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => document.RootElement.EnumerateArray().ToArray(),
                JsonValueKind.Object => [document.RootElement],
                _ => []
            };

            return rows
                .Select(element => new
                {
                    VmName = HyperVInventory.ReadJsonString(element, "VMName"),
                    Checkpoint = new VmCheckpointRow(
                        HyperVInventory.ReadJsonString(element, "Name"),
                        HyperVInventory.ReadJsonString(element, "ParentName"),
                        HyperVInventory.ReadJsonString(element, "Path"),
                        HyperVInventory.ReadJsonString(element, "ParentPath"),
                        ReadJsonDateTime(element, "CreationTime"))
                })
                .Where(row => !string.IsNullOrWhiteSpace(row.VmName) && (!string.IsNullOrWhiteSpace(row.Checkpoint.Name) || !string.IsNullOrWhiteSpace(row.Checkpoint.Path)))
                .GroupBy(row => row.VmName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(row => row.Checkpoint)
                        .DistinctBy(row => string.IsNullOrWhiteSpace(row.Path) ? $"checkpoint:{row.Name}:{row.Created:O}" : $"path:{row.Path}", StringComparer.OrdinalIgnoreCase)
                        .OrderBy(row => row.Created == DateTime.MinValue ? DateTime.MaxValue : row.Created)
                        .ThenBy(row => row.Path, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static DateTime ReadJsonDateTime(JsonElement element, string propertyName)
    {
        var value = HyperVInventory.ReadJsonString(element, propertyName);
        return DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsed)
            || DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed)
            ? parsed
            : DateTime.MinValue;
    }

    private static NetworkSwitchTopologyRow[] ParseSwitchTopologyJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            var rows = document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => document.RootElement.EnumerateArray().ToArray(),
                JsonValueKind.Object => [document.RootElement],
                _ => []
            };

            return rows
                .Select(element => new NetworkSwitchTopologyRow(
                    HyperVInventory.ReadJsonString(element, "Name"),
                    HyperVInventory.ReadJsonString(element, "SwitchType"),
                    ReadJsonUplinks(element, "Uplinks"),
                    HyperVInventory.ReadJsonString(element, "TeamMode")))
                .Where(row => !string.IsNullOrWhiteSpace(row.Name))
                .DistinctBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static NetworkUplinkInfo[] ReadJsonUplinks(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return [];

        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray()
                .Select(item => new NetworkUplinkInfo(
                    HyperVInventory.ReadJsonString(item, "Name"),
                    HyperVInventory.ReadJsonString(item, "Description"),
                    NormalizeTopologyLink(HyperVInventory.ReadJsonString(item, "Link"), HyperVInventory.ReadJsonBool(item, "IsUp")),
                    HyperVInventory.ReadJsonBool(item, "IsUp"),
                    NetworkLinkFormatter.ParseBitsPerSecond(NormalizeTopologyLink(HyperVInventory.ReadJsonString(item, "Link"), HyperVInventory.ReadJsonBool(item, "IsUp")))))
                .Where(item => !string.IsNullOrWhiteSpace(item.Name) || !string.IsNullOrWhiteSpace(item.Description))
                .DistinctBy(item => $"{item.Name}|{item.Description}", StringComparer.OrdinalIgnoreCase)
                .ToArray();

        var single = value.ToString().Trim();
        return string.IsNullOrWhiteSpace(single) ? [] : [new NetworkUplinkInfo(single, single, "DOWN", false, 0)];
    }

    private static string NormalizeTopologyLink(string raw, bool isUp)
    {
        if (!isUp)
            return "DOWN";

        var text = raw?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return "DOWN";
        if (text.Contains("100", StringComparison.OrdinalIgnoreCase)) return "100G";
        if (text.Contains("40", StringComparison.OrdinalIgnoreCase)) return "40G";
        if (text.Contains("25", StringComparison.OrdinalIgnoreCase)) return "25G";
        if (text.Contains("10", StringComparison.OrdinalIgnoreCase)) return "10G";
        if (text.Contains("1 G", StringComparison.OrdinalIgnoreCase) || text.Contains("1000", StringComparison.OrdinalIgnoreCase)) return "GbE";
        if (text.Contains("100 M", StringComparison.OrdinalIgnoreCase) || text.Contains("100Mbps", StringComparison.OrdinalIgnoreCase)) return "FE";
        return text;
    }

    private static string BuildUplinkSummary(NetworkUplinkInfo[] uplinks)
    {
        var names = uplinks
            .Select(u => string.IsNullOrWhiteSpace(u.Name) ? u.Description : u.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 0) return "n/a";
        return string.Join(", ", names);
    }

    private static bool IsVirtualDiskPath(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && (path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".avhdx", StringComparison.OrdinalIgnoreCase));

    private static string ResolveStorageName(string path, DiskRow[] disks)
    {
        var root = StorageInventory.ResolveStorageKey(path);
        if (string.IsNullOrWhiteSpace(root))
            return string.Empty;

        var match = disks
            .Where(d =>
                d.Name.Equals(root, StringComparison.OrdinalIgnoreCase)
                || d.Name.StartsWith(root + " ", StringComparison.OrdinalIgnoreCase)
                || root.StartsWith(d.Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.Name.Length)
            .FirstOrDefault();
        return match?.Name ?? root;
    }

    private string? DedupEvent(string message)
    {
        if (string.Equals(lastEventMessage, message, StringComparison.Ordinal))
            return null;
        lastEventMessage = message;
        return message;
    }
}

internal sealed record HyperVTopologyData(VmTopologyRow[] Topology, NetworkSwitchTopologyRow[] Switches);

internal sealed record HyperVTopologyResult(VmTopologyRow[] Topology, NetworkSwitchTopologyRow[] Switches, string Source, string? EventMessage, string EventSeverity)
{
    public static HyperVTopologyResult Empty { get; } = new([], [], "None", null, "INFO");
}

