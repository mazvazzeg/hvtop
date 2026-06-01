namespace hvtop.Native;

internal static class PhysicalDiskSampler
{
    public static PhysicalDiskRow[] Read(string hostName)
    {
        var values = PdhWildcardReader.ReadMany(
            [
                ("bytes", @"\PhysicalDisk(*)\Disk Bytes/sec"),
                ("read-bytes", @"\PhysicalDisk(*)\Disk Read Bytes/sec"),
                ("write-bytes", @"\PhysicalDisk(*)\Disk Write Bytes/sec"),
                ("iops", @"\PhysicalDisk(*)\Disk Transfers/sec"),
                ("read-iops", @"\PhysicalDisk(*)\Disk Reads/sec"),
                ("write-iops", @"\PhysicalDisk(*)\Disk Writes/sec"),
                ("queue", @"\PhysicalDisk(*)\Current Disk Queue Length"),
                ("latency", @"\PhysicalDisk(*)\Avg. Disk sec/Transfer")
            ],
            NormalizeInstance);

        var names = values
            .Values
            .SelectMany(group => group.Values.Keys)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, PhysicalDiskNameComparer.Instance)
            .ToArray();

        return names
            .Select(name =>
            {
                var inventory = PhysicalDiskInventory.Find(name);
                var readIo = Read(values, "read-bytes", name) / 1024d / 1024d;
                var writeIo = Read(values, "write-bytes", name) / 1024d / 1024d;
                var totalIo = Read(values, "bytes", name) / 1024d / 1024d;
                var readIops = Read(values, "read-iops", name);
                var writeIops = Read(values, "write-iops", name);
                var totalIops = Read(values, "iops", name);
                var queue = Read(values, "queue", name);
                var latencyMs = Read(values, "latency", name) * 1000d;
                var io = Math.Max(readIo + writeIo, totalIo);
                var iops = Math.Max(readIops + writeIops, totalIops);

                return new PhysicalDiskRow(
                    hostName,
                    inventory.PhysicalDiskId,
                    name,
                    inventory.Type,
                    inventory.Size,
                    inventory.FriendlyName,
                    inventory.Manufacturer,
                    inventory.Model,
                    inventory.FirmwareVersion,
                    inventory.SerialNumber,
                    inventory.Mapping,
                    Metric.Mbps(io),
                    Metric.Mbps(readIo),
                    Metric.Mbps(writeIo),
                    Metric.Iops(iops),
                    Metric.Iops(readIops),
                    Metric.Iops(writeIops),
                    Metric.QueueDepth(queue),
                    Metric.Milliseconds(latencyMs),
                    Status.From(10, 10, latencyMs, queue));
            })
            .ToArray();
    }

    private static double Read(Dictionary<string, PdhWildcardGroupResult> values, string group, string name)
        => values.TryGetValue(group, out var result)
           && result.Values.TryGetValue(name, out var value)
           && !double.IsNaN(value)
            ? value
            : 0;

    private static string NormalizeInstance(string instance)
    {
        var value = instance.Trim();
        return value.Equals("_Total", StringComparison.OrdinalIgnoreCase) ? string.Empty : value;
    }

    private sealed class PhysicalDiskNameComparer : IComparer<string>
    {
        public static PhysicalDiskNameComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            x ??= string.Empty;
            y ??= string.Empty;
            var xNumber = LeadingNumber(x);
            var yNumber = LeadingNumber(y);
            if (xNumber.HasValue && yNumber.HasValue && xNumber.Value != yNumber.Value)
                return xNumber.Value.CompareTo(yNumber.Value);

            return StringComparer.OrdinalIgnoreCase.Compare(x, y);
        }

        private static int? LeadingNumber(string value)
        {
            var digits = new string(value.TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, out var number) ? number : null;
        }
    }
}
