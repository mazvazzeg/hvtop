namespace hvtop.Native;

internal sealed record Snapshot(
    DateTime At,
    ClusterRow[] Clusters,
    HostRow[] Hosts,
    VmRow[] Vms,
    DiskRow[] Disks,
    PhysicalDiskRow[] PhysicalDisks,
    NetworkSwitchRow[] NetworkSwitches,
    NetworkRow[] Networks,
    EventRow[] Events,
    VmTopologyRow[] VmTopology,
    bool Loading,
    bool InventoryRefreshing,
    bool TopologyRefreshing,
    DiscoveryProgress Discovery,
    string RdcStatus)
{
    public static Snapshot Empty { get; } = new(DateTime.Now, [], [], [], [], [], [], [], [], [], true, false, false, DiscoveryProgress.Empty, "idle");
}

internal sealed record DiscoveryProgress(
    bool HostsReady,
    bool VmsReady,
    bool StorageReady,
    bool NetworkReady,
    bool Complete,
    int VmCount,
    int StorageCount,
    int NetworkInterfaceCount,
    int NetworkSwitchCount)
{
    public static DiscoveryProgress Empty { get; } = new(false, false, false, false, false, 0, 0, 0, 0);
}

internal sealed record Metric(double Current, double Max, Unit Unit)
{
    public static Metric Percent(double value) => new(value, value, Unit.Percent);
    public static Metric Bytes(double value) => new(value, value, Unit.Bytes);
    public static Metric Mbps(double value) => new(value, value, Unit.Mbps);
    public static Metric Iops(double value) => new(value, value, Unit.Iops);
    public static Metric Milliseconds(double value) => new(value, value, Unit.Milliseconds);
    public static Metric QueueDepth(double value) => new(value, value, Unit.QueueDepth);
    public static Metric Plain(double value) => new(value, value, Unit.Plain);
}

internal enum Unit { Plain, Percent, Bytes, Mbps, Iops, Milliseconds, QueueDepth }

internal sealed record ClusterRow(string Name, int NodeCount, int UpNodeCount, string OwnerNode, string Quorum, string FunctionalLevel, string Status);

internal sealed record ClusterNodeRow(string Name, string State, string Status);

internal sealed record HostMemoryBreakdown(Metric InUse, Metric Processes, Metric Kernel, Metric Modified, Metric StandbyCache, Metric Free)
{
    public static HostMemoryBreakdown Empty { get; } = new(
        Metric.Bytes(double.NaN),
        Metric.Bytes(double.NaN),
        Metric.Bytes(double.NaN),
        Metric.Bytes(double.NaN),
        Metric.Bytes(double.NaN),
        Metric.Bytes(double.NaN));
}

internal sealed record HostRow(string Name, string Version, TimeSpan? Uptime, Metric Cpu, string CpuCapacity, Metric Mem, string MemCapacity, HostMemoryBreakdown Ram, Metric Io, Metric Net, string Status)
{
    public IReadOnlyDictionary<string, double> Metrics => new Dictionary<string, double>
    {
        [nameof(Cpu)] = Cpu.Current,
        [nameof(Mem)] = Mem.Current,
        [nameof(Ram) + nameof(Ram.InUse)] = Ram.InUse.Current,
        [nameof(Ram) + nameof(Ram.Processes)] = Ram.Processes.Current,
        [nameof(Ram) + nameof(Ram.Kernel)] = Ram.Kernel.Current,
        [nameof(Ram) + nameof(Ram.Modified)] = Ram.Modified.Current,
        [nameof(Ram) + nameof(Ram.StandbyCache)] = Ram.StandbyCache.Current,
        [nameof(Ram) + nameof(Ram.Free)] = Ram.Free.Current,
        [nameof(Io)] = Io.Current,
        [nameof(Net)] = Net.Current
    };
}

internal sealed record VmRow(string Name, string HostName, string Version, TimeSpan Uptime, bool IsRunning, string Replication, string ReplicationStatus, Metric Cpu, string CpuCapacity, Metric Mem, string MemCapacity, Metric Io, Metric Net, Metric Iops, Metric Latency, string Status)
{
    public IReadOnlyDictionary<string, double> Metrics => new Dictionary<string, double>
    {
        [nameof(Cpu)] = Cpu.Current,
        [nameof(Mem)] = Mem.Current,
        [nameof(Io)] = Io.Current,
        [nameof(Net)] = Net.Current,
        [nameof(Iops)] = Iops.Current,
        [nameof(Latency)] = Latency.Current
    };
}

internal sealed record DiskRow(string HostName, string Name, string Size, string UsedSpace, string FreeSpace, Metric Free, Metric Io, Metric ReadIo, Metric WriteIo, Metric Iops, Metric ReadIops, Metric WriteIops, Metric QueueDepth, Metric Latency, string Status)
{
    public IReadOnlyDictionary<string, double> Metrics => new Dictionary<string, double>
    {
        [nameof(Free)] = Free.Current,
        [nameof(Io)] = Io.Current,
        [nameof(ReadIo)] = ReadIo.Current,
        [nameof(WriteIo)] = WriteIo.Current,
        [nameof(Iops)] = Iops.Current,
        [nameof(ReadIops)] = ReadIops.Current,
        [nameof(WriteIops)] = WriteIops.Current,
        [nameof(QueueDepth)] = QueueDepth.Current,
        [nameof(Latency)] = Latency.Current
    };
}

internal sealed record PhysicalDiskRow(string HostName, string PhysicalDiskId, string Name, string Type, string Size, string FriendlyName, string Manufacturer, string Model, string FirmwareVersion, string SerialNumber, string Mapping, string SoftwareRaid, string VolumeName, Metric Io, Metric ReadIo, Metric WriteIo, Metric Iops, Metric ReadIops, Metric WriteIops, Metric QueueDepth, Metric Latency, string Status)
{
    public IReadOnlyDictionary<string, double> Metrics => new Dictionary<string, double>
    {
        [nameof(Io)] = Io.Current,
        [nameof(ReadIo)] = ReadIo.Current,
        [nameof(WriteIo)] = WriteIo.Current,
        [nameof(Iops)] = Iops.Current,
        [nameof(ReadIops)] = ReadIops.Current,
        [nameof(WriteIops)] = WriteIops.Current,
        [nameof(QueueDepth)] = QueueDepth.Current,
        [nameof(Latency)] = Latency.Current
    };
}

internal sealed record NetworkRow(string HostName, string Name, string Description, string Link, bool IsUp, long LinkSpeedBitsPerSecond, Metric Throughput, Metric Rx, Metric Tx, Metric RdmaThroughput, Metric RdmaRx, Metric RdmaTx, Metric Drops, string Status, string PdhInstance = "", double RawReceivedBytesPerSecond = 0, double RawSentBytesPerSecond = 0, double PdhReceivedBytesPerSecond = 0, double PdhSentBytesPerSecond = 0, string RdmaInstance = "", double RdmaReceivedBytesPerSecond = 0, double RdmaSentBytesPerSecond = 0)
{
    public IReadOnlyDictionary<string, double> Metrics => new Dictionary<string, double>
    {
        [nameof(Throughput)] = Throughput.Current,
        [nameof(Rx)] = Rx.Current,
        [nameof(Tx)] = Tx.Current,
        [nameof(RdmaThroughput)] = RdmaThroughput.Current,
        [nameof(RdmaRx)] = RdmaRx.Current,
        [nameof(RdmaTx)] = RdmaTx.Current,
        [nameof(Drops)] = Drops.Current
    };
}

internal sealed record NetworkUplinkInfo(string Name, string Description, string Link, bool IsUp, long LinkSpeedBitsPerSecond);

internal sealed record NetworkSwitchRow(string HostName, string Name, string SwitchType, string TeamMode, NetworkUplinkInfo[] Uplinks, string Link, Metric Throughput, Metric Rx, Metric Tx, Metric RdmaThroughput, Metric RdmaRx, Metric RdmaTx, Metric Drops, string Status)
{
    public IReadOnlyDictionary<string, double> Metrics => new Dictionary<string, double>
    {
        [nameof(Throughput)] = Throughput.Current,
        [nameof(Rx)] = Rx.Current,
        [nameof(Tx)] = Tx.Current,
        [nameof(RdmaThroughput)] = RdmaThroughput.Current,
        [nameof(RdmaRx)] = RdmaRx.Current,
        [nameof(RdmaTx)] = RdmaTx.Current,
        [nameof(Drops)] = Drops.Current
    };
}

internal sealed record EventRow(DateTime At, string Severity, string Message);

internal sealed record VDiskRow(
    string Name,
    string Path,
    string StorageName,
    double ReadMbps,
    double ReadIops,
    double WriteMbps,
    double WriteIops,
    double TotalMbpsMax = double.NaN,
    double TotalIopsMax = double.NaN,
    double ReadMbpsMax = double.NaN,
    double ReadIopsMax = double.NaN,
    double WriteMbpsMax = double.NaN,
    double WriteIopsMax = double.NaN)
{
    public double TotalMbps => ReadMbps + WriteMbps;
    public double TotalIops => ReadIops + WriteIops;
    public IReadOnlyDictionary<string, double> Metrics => new Dictionary<string, double>
    {
        [nameof(TotalMbps)] = TotalMbps,
        [nameof(TotalIops)] = TotalIops,
        [nameof(ReadMbps)] = ReadMbps,
        [nameof(ReadIops)] = ReadIops,
        [nameof(WriteMbps)] = WriteMbps,
        [nameof(WriteIops)] = WriteIops
    };
}

internal sealed record VmNetworkPathRow(
    string Name,
    string SwitchName,
    string PhysicalAdapterName,
    double RxMbps = 0,
    double TxMbps = 0,
    double ThroughputMbpsMax = double.NaN,
    double RxMbpsMax = double.NaN,
    double TxMbpsMax = double.NaN)
{
    public double ThroughputMbps => RxMbps + TxMbps;
    public IReadOnlyDictionary<string, double> Metrics => new Dictionary<string, double>
    {
        [nameof(ThroughputMbps)] = ThroughputMbps,
        [nameof(RxMbps)] = RxMbps,
        [nameof(TxMbps)] = TxMbps
    };
}

internal sealed record VmCheckpointRow(
    string Name,
    string ParentName,
    string Path,
    string ParentPath,
    DateTime Created,
    double SizeMb = 0,
    double SizeMbMax = double.NaN,
    double ChangeMb = 0,
    double ChangeMbMax = double.NaN)
{
    public IReadOnlyDictionary<string, double> Metrics => new Dictionary<string, double>
    {
        [nameof(SizeMb)] = SizeMb,
        [nameof(ChangeMb)] = ChangeMb
    };
}

internal sealed record VDiskDetailRow(string HostName, string VmName, VDiskRow Disk);

internal sealed record VmNetworkDetailRow(string HostName, string VmName, VmNetworkPathRow Adapter, NetworkSwitchRow? Switch);

internal sealed record NetworkSwitchTopologyRow(
    string Name,
    string SwitchType,
    NetworkUplinkInfo[] Uplinks,
    string TeamMode = "");

internal sealed record VmTopologyRow(
    string VmName,
    VDiskRow[] Disks,
    VmNetworkPathRow[] Networks,
    VmCheckpointRow[] Checkpoints,
    string HostName = "");

internal static class Status
{
    public static string From(double cpu, double mem, double latency, double queueDepth)
    {
        if (cpu < 5 && mem < 15 && latency < 3) return "IDLE";
        if (cpu >= 85 || mem >= 90 || latency >= 25 || queueDepth >= 16) return "HOT";
        if (cpu >= 70 || mem >= 75 || latency >= 12 || queueDepth >= 8) return "BUSY";
        return "OK";
    }

    public static string FromNetwork(double throughputBytesPerSecond, long linkSpeedBitsPerSecond, bool isUp)
    {
        if (!isUp)
            return "OFF";

        if (throughputBytesPerSecond <= 0.01)
            return "IDLE";

        if (linkSpeedBitsPerSecond > 0)
        {
            var utilization = throughputBytesPerSecond * 8d / linkSpeedBitsPerSecond;
            if (utilization >= 0.80) return "HOT";
            if (utilization >= 0.50) return "BUSY";
        }
        else if (throughputBytesPerSecond > 300 * 1024d * 1024d)
        {
            return "BUSY";
        }

        return "OK";
    }
}

