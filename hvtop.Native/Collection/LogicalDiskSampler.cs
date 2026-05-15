namespace hvtop.Native;

internal static class LogicalDiskSampler
{
    private static readonly Dictionary<string, double> DiskBytes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, double> DiskReadBytes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, double> DiskWriteBytes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, double> DiskIops = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, double> DiskReadIops = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, double> DiskWriteIops = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, double> Queue = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, double> Latency = new(StringComparer.OrdinalIgnoreCase);

    public static void Refresh()
    {
        var values = PdhWildcardReader.ReadMany(
            [
                ("bytes", @"\LogicalDisk(*)\Disk Bytes/sec"),
                ("read-bytes", @"\LogicalDisk(*)\Disk Read Bytes/sec"),
                ("write-bytes", @"\LogicalDisk(*)\Disk Write Bytes/sec"),
                ("iops", @"\LogicalDisk(*)\Disk Transfers/sec"),
                ("read-iops", @"\LogicalDisk(*)\Disk Reads/sec"),
                ("write-iops", @"\LogicalDisk(*)\Disk Writes/sec"),
                ("queue", @"\LogicalDisk(*)\Current Disk Queue Length"),
                ("latency", @"\LogicalDisk(*)\Avg. Disk sec/Transfer")
            ],
            NormalizeInstance);

        Replace(DiskBytes, values["bytes"].Values);
        Replace(DiskReadBytes, values["read-bytes"].Values);
        Replace(DiskWriteBytes, values["write-bytes"].Values);
        Replace(DiskIops, values["iops"].Values);
        Replace(DiskReadIops, values["read-iops"].Values);
        Replace(DiskWriteIops, values["write-iops"].Values);
        Replace(Queue, values["queue"].Values);
        Replace(Latency, values["latency"].Values.ToDictionary(kvp => kvp.Key, kvp => kvp.Value * 1000, StringComparer.OrdinalIgnoreCase));
    }

    public static double TotalMbps(string drive) => Read(DiskBytes, drive) / 1024 / 1024;
    public static double ReadMbps(string drive) => Read(DiskReadBytes, drive) / 1024 / 1024;
    public static double WriteMbps(string drive) => Read(DiskWriteBytes, drive) / 1024 / 1024;
    public static double TotalIops(string drive) => Read(DiskIops, drive);
    public static double ReadIops(string drive) => Read(DiskReadIops, drive);
    public static double WriteIops(string drive) => Read(DiskWriteIops, drive);
    public static double QueueDepth(string drive) => Read(Queue, drive);
    public static double LatencyMs(string drive) => Read(Latency, drive);

    private static void Replace(Dictionary<string, double> target, Dictionary<string, double> source)
    {
        target.Clear();
        foreach (var pair in source)
            target[pair.Key] = pair.Value;
    }

    private static double Read(Dictionary<string, double> values, string drive)
    {
        var key = NormalizeLookupKey(drive);
        if (values.TryGetValue(key, out var value) && !double.IsNaN(value))
            return value;

        foreach (var pair in values)
        {
            var candidate = NormalizeLookupKey(pair.Key);
            if (candidate.Equals(key, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(candidate + "\\", StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase))
                return !double.IsNaN(pair.Value) ? pair.Value : 0;
        }

        return 0;
    }

    private static string NormalizeInstance(string instance)
    {
        var value = NormalizeLookupKey(instance);
        if (value.Equals("_Total", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        if (value.StartsWith(@"C:\ClusterStorage\", StringComparison.OrdinalIgnoreCase))
            return StorageInventory.ResolveStorageKey(value);
        if (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':')
            return value.Length == 2 ? value[..2].ToUpperInvariant() : value;
        return value;
    }

    private static string NormalizeLookupKey(string value)
        => value.Trim().TrimEnd('\\');
}

