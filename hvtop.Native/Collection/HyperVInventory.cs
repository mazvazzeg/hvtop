namespace hvtop.Native;

internal sealed class HyperVInventory
{
    private readonly object gate = new();
    private HyperVInventoryVm[] cache = [];
    private string? lastEventMessage;
    private readonly Queue<InventoryEvent> pendingEvents = new();
    public bool IsRefreshing { get; private set; }

    public void RequestRefresh()
    {
        lock (gate)
        {
            if (IsRefreshing) return;
            IsRefreshing = true;
            _ = Task.Run(RefreshAsync);
        }
    }

    public HyperVInventoryResult TryRead()
    {
        lock (gate)
        {
            var events = pendingEvents.ToArray();
            pendingEvents.Clear();
            return new HyperVInventoryResult(cache, "PowerShell", events);
        }
    }

    private void RefreshAsync()
    {
        try
        {
            var fallback = TryReadPowerShell();
            lock (gate)
            {
                if (fallback.Available)
                {
                    cache = fallback.Vms;
                    EnqueueEvent("WARN", "Hyper-V native WMI inventory disabled in single-file build, using PowerShell fallback.");
                }
                else
                {
                    EnqueueEvent("INFO", "Hyper-V inventory not detected; standard Windows host mode active.");
                }

                foreach (var evt in fallback.Events)
                    EnqueueEvent(evt.Severity, evt.Message);
            }
        }
        finally
        {
            lock (gate)
                IsRefreshing = false;
        }
    }

    private static HyperVInventoryData TryReadPowerShell()
    {
        const string script = "$ErrorActionPreference='Stop'; " +
            "$available=$false; $rows=@(); " +
            "try { " +
            "Import-Module Hyper-V -ErrorAction Stop | Out-Null; $available=$true; " +
            "$rows = @(Get-VM -ErrorAction Stop | Select-Object Name,@{N='Version';E={[string]$_.Version}},@{N='IsRunning';E={[bool]($_.State -eq 'Running')}},@{N='ProcessorCount';E={try {[int]$_.ProcessorCount} catch {0}}},@{N='MemoryStartup';E={try {[double]$_.MemoryStartup} catch {0}}},MemoryAssigned,MemoryDemand,MemoryStatus,DynamicMemoryEnabled,@{N='ReplicationState';E={try {[string]$_.ReplicationState} catch {''}}},@{N='ReplicationHealth';E={try {[string]$_.ReplicationHealth} catch {''}}}); " +
            "} catch { " +
            "$rows = @(Get-CimInstance -Namespace root/virtualization/v2 -ClassName Msvm_ComputerSystem -ErrorAction Stop " +
            "| Where-Object { $_.Caption -eq 'Virtual Machine' } " +
            "| Select-Object @{N='Name';E={$_.ElementName}},@{N='Version';E={''}},@{N='IsRunning';E={[bool]($_.EnabledState -eq 2)}},@{N='ProcessorCount';E={0}},@{N='MemoryStartup';E={0}},@{N='UptimeSeconds';E={if ($_.EnabledState -eq 2 -and $_.OnTimeInMilliseconds) { [double]$_.OnTimeInMilliseconds / 1000 } else { 0 }}},@{N='MemoryAssigned';E={0}},@{N='MemoryDemand';E={0}},@{N='MemoryStatus';E={''}},@{N='DynamicMemoryEnabled';E={$false}},@{N='ReplicationState';E={''}},@{N='ReplicationHealth';E={''}}); " +
            "if ($rows -and $rows.Count -gt 0) { $available=$true }; " +
            "} ; " +
            "[pscustomobject]@{Available=$available; Vms=@($rows)} | ConvertTo-Json -Compress -Depth 4";

        if (!PowerShellRunner.TryRun(script, 30000, out var output, out var error, out var exitCode, out var timedOut))
        {
            var reason = timedOut ? "timed out" : $"failed exit={exitCode}";
            return new HyperVInventoryData([], false, [new InventoryEvent("WARN", $"HVDIAG inventory PowerShell {reason}: {TrimForEvent(error, 140)}")]);
        }

        var parsed = ParseInventoryJson(output);
        var uptime = parsed.Vms.Length > 0 ? TryReadPowerShellUptime() : new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        var sampledAt = DateTime.UtcNow;
        if (uptime.Count > 0)
            parsed = parsed with
            {
                Vms = parsed.Vms
                    .Select(vm => uptime.TryGetValue(vm.Name, out var value) ? vm with { Uptime = value, UptimeSampledAt = sampledAt } : vm)
                    .ToArray()
            };
        var zeroVmEvents = parsed.Vms.Length == 0
            ? new[] { new InventoryEvent("WARN", $"HVDIAG raw='{TrimForEvent(output, 120)}'") }
            : [];
        return parsed with
        {
            Events =
            [
                .. parsed.Events,
                new InventoryEvent("INFO", $"HVDIAG inventory available={parsed.Available} rows={parsed.Vms.Length} json={output.Length}"),
                .. parsed.Vms.Take(3).Select(vm => new InventoryEvent("INFO", $"HVDIAG vm='{TrimForEvent(vm.Name, 28)}' run={vm.IsRunning} up={UptimeFormatter.FormatShort(vm.Uptime)} ver='{TrimForEvent(vm.Version, 10)}' repl='{TrimForEvent(vm.ReplicationDisplay, 22)}'")),
                .. zeroVmEvents
            ]
        };
    }

    private static Dictionary<string, TimeSpan> TryReadPowerShellUptime()
    {
        const string script = "Import-Module Hyper-V -ErrorAction Stop | Out-Null; " +
            "@(Get-VM -ErrorAction Stop | Select-Object Name,@{N='UptimeSeconds';E={try { [double]$_.Uptime.TotalSeconds } catch { 0 }}}) | ConvertTo-Json -Compress -Depth 3";

        if (!PowerShellRunner.TryRun(script, 5000, out var output) || string.IsNullOrWhiteSpace(output))
            return new(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var document = JsonDocument.Parse(output);
            var rows = document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => document.RootElement.EnumerateArray().ToArray(),
                JsonValueKind.Object => [document.RootElement],
                _ => []
            };

            return rows
                .Select(row => new
                {
                    Name = ReadJsonString(row, "Name"),
                    Uptime = TimeSpan.FromSeconds(Math.Max(0, ReadJsonDouble(row, "UptimeSeconds")))
                })
                .Where(row => !string.IsNullOrWhiteSpace(row.Name))
                .ToDictionary(row => row.Name, row => row.Uptime, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static HyperVInventoryData ParseInventoryJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return HyperVInventoryData.Empty;

        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement[] rows;
            var available = true;
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("Vms", out var vmsElement))
            {
                available = ReadJsonBool(document.RootElement, "Available");
                rows = vmsElement.ValueKind switch
                {
                    JsonValueKind.Array => vmsElement.EnumerateArray().ToArray(),
                    JsonValueKind.Object => [vmsElement],
                    _ => []
                };
            }
            else
            {
                rows = document.RootElement.ValueKind switch
                {
                    JsonValueKind.Array => document.RootElement.EnumerateArray().ToArray(),
                    JsonValueKind.Object => [document.RootElement],
                    _ => []
                };
            }

            var vms = rows
                .Select(element =>
                {
                    var name = ReadJsonString(element, "Name");
                    var version = ReadJsonString(element, "Version");
                    var isRunning = ReadJsonBool(element, "IsRunning");
                    var processorCount = Math.Max(0, (int)Math.Round(ReadJsonDouble(element, "ProcessorCount")));
                    var uptime = ReadJsonTimeSpan(element, "Uptime");
                    if (uptime == TimeSpan.Zero)
                    {
                        var uptimeSeconds = ReadJsonDouble(element, "UptimeSeconds");
                        if (uptimeSeconds > 0)
                            uptime = TimeSpan.FromSeconds(uptimeSeconds);
                    }
                    var memoryStartupBytes = ReadJsonDouble(element, "MemoryStartup");
                    var memoryAssignedBytes = ReadJsonDouble(element, "MemoryAssigned");
                    var memoryDemandBytes = ReadJsonDouble(element, "MemoryDemand");
                    var memoryStatus = ReadJsonString(element, "MemoryStatus");
                    var dynamicMemoryEnabled = ReadJsonBool(element, "DynamicMemoryEnabled");
                    var replicationState = ReadJsonString(element, "ReplicationState");
                    var replicationHealth = ReadJsonString(element, "ReplicationHealth");
                    return new HyperVInventoryVm(name, version, isRunning, uptime, DateTime.UtcNow, processorCount, memoryStartupBytes, memoryAssignedBytes, memoryDemandBytes, memoryStatus, dynamicMemoryEnabled, replicationState, replicationHealth);
                })
                .Where(vm => !string.IsNullOrWhiteSpace(vm.Name))
                .DistinctBy(vm => vm.Name, StringComparer.OrdinalIgnoreCase)
                .OrderBy(vm => vm.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new HyperVInventoryData(vms, available, []);
        }
        catch (Exception ex)
        {
            return new HyperVInventoryData([], false, [new InventoryEvent("WARN", $"HVDIAG parse failed: {TrimForEvent(ex.Message, 120)}"), new InventoryEvent("WARN", $"HVDIAG raw='{TrimForEvent(json, 120)}'")]);
        }
    }

    internal static string ReadJsonString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) ? value.ToString().Trim() : string.Empty;

    internal static bool ReadJsonBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return false;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetString()?.Equals("True", StringComparison.OrdinalIgnoreCase) == true,
            JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
            _ => false
        };
    }

    internal static double ReadJsonDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDouble(out var number) ? number : 0,
            JsonValueKind.String => double.TryParse(value.GetString(), out var number) ? number : 0,
            _ => 0
        };
    }

    private static TimeSpan ReadJsonTimeSpan(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return TimeSpan.Zero;

        try
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var ticks = ReadJsonDouble(value, "Ticks");
                if (ticks > 0)
                    return TimeSpan.FromTicks((long)ticks);

                var days = ReadJsonDouble(value, "Days");
                var hours = ReadJsonDouble(value, "Hours");
                var minutes = ReadJsonDouble(value, "Minutes");
                var seconds = ReadJsonDouble(value, "Seconds");
                return TimeSpan.FromDays(days) + TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
            }

            if (value.ValueKind == JsonValueKind.String && TimeSpan.TryParse(value.GetString(), out var parsed))
                return parsed;
        }
        catch
        {
        }

        return TimeSpan.Zero;
    }

    internal static string[] ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var ch in line)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        values.Add(current.ToString());
        return values.ToArray();
    }

    private string? DedupEvent(string message)
    {
        if (string.Equals(lastEventMessage, message, StringComparison.Ordinal))
            return null;
        lastEventMessage = message;
        return message;
    }

    private void EnqueueEvent(string severity, string message)
    {
        var deduped = DedupEvent(message);
        if (!string.IsNullOrWhiteSpace(deduped))
            pendingEvents.Enqueue(new InventoryEvent(severity, deduped));
    }

    private static string TrimForEvent(string value, int max)
    {
        value = (value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return value.Length <= max ? value : value[..max];
    }

}

internal sealed record HyperVInventoryVm(
    string Name,
    string Version,
    bool IsRunning,
    TimeSpan Uptime,
    DateTime UptimeSampledAt,
    int ProcessorCount,
    double MemoryStartupBytes,
    double MemoryAssignedBytes,
    double MemoryDemandBytes,
    string MemoryStatus,
    bool DynamicMemoryEnabled,
    string ReplicationState,
    string ReplicationHealth)
{
    public string ReplicationDisplay => ReplicationFormatter.Display(ReplicationState, ReplicationHealth);
    public string ReplicationStatus => ReplicationFormatter.Status(ReplicationState, ReplicationHealth);
}
internal sealed record HyperVInventoryData(HyperVInventoryVm[] Vms, bool Available, InventoryEvent[] Events)
{
    public static HyperVInventoryData Empty { get; } = new([], false, []);
}
internal sealed record InventoryEvent(string Severity, string Message);
internal sealed record HyperVInventoryResult(HyperVInventoryVm[] Vms, string Source, InventoryEvent[] Events);

