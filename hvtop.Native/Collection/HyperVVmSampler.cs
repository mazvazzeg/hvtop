namespace hvtop.Native;

internal sealed class HyperVVmSampler
{
    private static readonly string[] CpuCounters =
    [
        @"\Hyper-V Hypervisor Virtual Processor(*)\% Guest Run Time",
        @"\Hyper-V Hypervisor Virtual Processor(*)\% Total Run Time"
    ];

    private static readonly string[] MemoryCounters =
    [
        @"\Hyper-V Dynamic Memory VM(*)\Physical Memory",
        @"\Hyper-V Dynamic Memory VM(*)\Guest Visible Physical Memory"
    ];

    public VmRow[] Collect(RollingHistory history, string hostName, HyperVInventoryVm[] inventoryVms)
    {
        var vmValues = PdhWildcardReader.ReadMany(
            [
                ("cpu-guest", CpuCounters[0]),
                ("cpu-total", CpuCounters[1]),
                ("assigned-mem", @"\Hyper-V Dynamic Memory VM(*)\Physical Memory"),
                ("visible-mem", @"\Hyper-V Dynamic Memory VM(*)\Guest Visible Physical Memory"),
                ("net", @"\Hyper-V Virtual Network Adapter(*)\Bytes/sec"),
                ("read-bytes", @"\Hyper-V Virtual Storage Device(*)\Read Bytes/sec"),
                ("write-bytes", @"\Hyper-V Virtual Storage Device(*)\Write Bytes/sec"),
                ("read-ops", @"\Hyper-V Virtual Storage Device(*)\Read Operations/Sec"),
                ("write-ops", @"\Hyper-V Virtual Storage Device(*)\Write Operations/Sec")
            ],
            NormalizeVmCounterInstance);
        var cpuGroup = vmValues["cpu-guest"].Values.Count > 0 ? vmValues["cpu-guest"] : vmValues["cpu-total"];
        var cpu = cpuGroup.Values;
        var cpuInstanceCounts = cpuGroup.InstanceCounts;
        var assignedMem = vmValues["assigned-mem"].Values;
        var visibleMem = vmValues["visible-mem"].Values;
        var net = vmValues["net"].Values;
        var readBytes = vmValues["read-bytes"].Values;
        var writeBytes = vmValues["write-bytes"].Values;
        var readOps = vmValues["read-ops"].Values;
        var writeOps = vmValues["write-ops"].Values;

        return inventoryVms
            .Where(vm => !string.IsNullOrWhiteSpace(vm.Name))
            .OrderBy(vm => vm.Name, StringComparer.OrdinalIgnoreCase)
            .Select(vm =>
            {
                var name = vm.Name;
                var vcpuCount = CountVirtualProcessors(cpuInstanceCounts, name);
                var cpuValue = vm.IsRunning ? (vcpuCount > 0 ? Get(cpu, name) / vcpuCount : Get(cpu, name)) : 0;
                var assignedMb = Get(assignedMem, name);
                var visibleMb = Get(visibleMem, name);
                var assignedBytes = vm.MemoryAssignedBytes > 0 ? vm.MemoryAssignedBytes : (assignedMb > 0 ? assignedMb * 1024d * 1024d : 0);
                var demandBytes = vm.MemoryDemandBytes > 0 ? vm.MemoryDemandBytes : 0;
                var memPercent = ComputeVmMemoryPercent(vm, assignedBytes, demandBytes, visibleMb);
                var memoryCapacityBytes = assignedBytes > 0
                    ? assignedBytes
                    : (visibleMb > 0 ? visibleMb * 1024d * 1024d : 0);
                var memoryCapacityLabel = BuildVmMemoryCapacityLabel(vm, memoryCapacityBytes);
                var readBytesValue = vm.IsRunning ? SumStorageCounters(readBytes, name) : 0;
                var writeBytesValue = vm.IsRunning ? SumStorageCounters(writeBytes, name) : 0;
                var ioMbps = (readBytesValue + writeBytesValue) / 1024 / 1024;
                var netMbps = vm.IsRunning ? Get(net, name) / 1024 / 1024 : 0;
                var iops = vm.IsRunning ? SumStorageCounters(readOps, name) + SumStorageCounters(writeOps, name) : 0;
                var status = vm.IsRunning ? Status.From(cpuValue, memPercent, 0, 0) : "OFF";
                var uptime = vm.IsRunning
                    ? vm.Uptime + (DateTime.UtcNow - vm.UptimeSampledAt)
                    : TimeSpan.Zero;
                if (uptime < TimeSpan.Zero)
                    uptime = TimeSpan.Zero;
                var row = new VmRow(
                    name,
                    hostName,
                    FormatVersion(vm.Version),
                    uptime,
                    vm.IsRunning,
                    vm.ReplicationDisplay,
                    vm.ReplicationStatus,
                    Metric.Percent(cpuValue),
                    vcpuCount > 0 ? $"{vcpuCount} vCPU" : "n/a vCPU",
                    Metric.Percent(memPercent),
                    memoryCapacityLabel,
                    Metric.Mbps(ioMbps),
                    Metric.Mbps(netMbps),
                    Metric.Iops(iops),
                    Metric.Milliseconds(0),
                    status);
                return history.Apply("vm:" + row.Name, row);
            })
            .ToArray();
    }

    private static double ComputeVmMemoryPercent(HyperVInventoryVm vm, double assignedBytes, double demandBytes, double visibleMb)
    {
        if (!vm.IsRunning)
            return 0;

        if (assignedBytes > 0 && demandBytes > 0)
            return Math.Clamp(demandBytes / assignedBytes * 100, 0, 100);

        if (visibleMb > 0)
        {
            var assignedMb = assignedBytes / 1024d / 1024d;
            if (assignedMb > 0)
                return Math.Clamp(assignedMb / visibleMb * 100, 0, 100);
        }

        return 0;
    }

    private static double SumStorageCounters(Dictionary<string, double> counters, string vmName)
    {
        var normalizedVmName = HyperVNaming.NormalizeVmIdentity(vmName);
        if (string.IsNullOrWhiteSpace(normalizedVmName))
            return 0;

        double total = 0;
        foreach (var pair in counters)
        {
            if (HyperVNaming.ContainsIdentityToken(pair.Key, normalizedVmName) && !double.IsNaN(pair.Value))
                total += pair.Value;
        }

        return total;
    }

    private static string BuildVmMemoryCapacityLabel(HyperVInventoryVm vm, double memoryCapacityBytes)
    {
        if (memoryCapacityBytes <= 0)
            return "n/a";

        var label = CapacityFormatter.FormatConfigCapacity(memoryCapacityBytes);
        return vm.DynamicMemoryEnabled ? $"D {label}" : label;
    }

    private static string FormatVersion(string? version)
        => string.IsNullOrWhiteSpace(version) ? "n/a" : version.Trim();

    private static Dictionary<string, double> ReadFirst(string[] paths, Func<string, string> normalizeInstance)
    {
        foreach (var path in paths)
        {
            var values = PdhWildcardReader.Read(path, normalizeInstance);
            if (values.Count > 0) return values;
        }
        return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    }

    private static double Get(Dictionary<string, double> values, string key)
        => values.TryGetValue(key, out var value) && !double.IsNaN(value) ? value : 0;

    private static int CountVirtualProcessors(Dictionary<string, int> cpuInstanceCounts, string vmName)
        => cpuInstanceCounts.TryGetValue(vmName, out var count) ? Math.Max(1, count) : 0;

    private static string NormalizeCpuInstance(string instance)
    {
        var colon = instance.IndexOf(':');
        return colon > 0 ? instance[..colon].Trim() : instance.Trim();
    }

    private static string NormalizeDynamicMemoryInstance(string instance) => instance.Trim();

    private static string NormalizeNetworkInstance(string instance)
    {
        var marker = instance.IndexOf("_Network Adapter", StringComparison.OrdinalIgnoreCase);
        if (marker > 0) return instance[..marker].Trim();
        if (instance.Contains("__DEVICE_", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        if (instance.StartsWith("vSwitch", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        return instance.Trim();
    }

    private static string NormalizeVmCounterInstance(string instance)
    {
        var network = NormalizeNetworkInstance(instance);
        if (string.IsNullOrWhiteSpace(network))
            return string.Empty;
        if (!network.Equals(instance.Trim(), StringComparison.Ordinal))
            return network;

        if (instance.Contains('\\', StringComparison.Ordinal)
            || instance.Contains('/', StringComparison.Ordinal)
            || instance.Contains(".vhd", StringComparison.OrdinalIgnoreCase))
            return HyperVNaming.NormalizeStorageCounterIdentity(instance);

        var colon = instance.IndexOf(':');
        if (colon > 0)
            return instance[..colon].Trim();

        return instance.Trim();
    }

}

