namespace hvtop.Native;

internal static class ElevationChecker
{
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}

internal static class CapacityFormatter
{
    public static string FormatCapacity(double bytes)
    {
        if (bytes <= 0) return "n/a";
        var teb = bytes / 1024d / 1024d / 1024d / 1024d;
        if (teb >= 1) return $"{teb:0.00} TB";
        var gib = bytes / 1024d / 1024d / 1024d;
        return $"{gib:0.0} GB";
    }

    public static string FormatConfigCapacity(double bytes)
    {
        if (bytes <= 0) return "n/a";
        var teb = bytes / 1024d / 1024d / 1024d / 1024d;
        if (teb >= 1) return $"{teb:0.0} TB";
        var gib = bytes / 1024d / 1024d / 1024d;
        return $"{Math.Round(gib, MidpointRounding.AwayFromZero):0} GB";
    }

    public static string FormatPhysicalDiskCapacity(double bytes)
    {
        if (bytes <= 0) return "n/a";

        return $"{FormatMarketingCapacity(bytes)} ({FormatBinaryCapacity(bytes)})";
    }

    private static string FormatMarketingCapacity(double bytes)
    {
        var tb = bytes / 1_000_000_000_000d;
        if (tb >= 1)
            return $"{FormatMarketingNumber(tb)} TB";

        var gb = bytes / 1_000_000_000d;
        return $"{FormatMarketingNumber(gb)} GB";
    }

    private static string FormatBinaryCapacity(double bytes)
    {
        var tib = bytes / 1024d / 1024d / 1024d / 1024d;
        if (tib >= 1)
            return $"{tib:0.00} TiB";

        var gib = bytes / 1024d / 1024d / 1024d;
        return gib >= 100 ? $"{gib:0} GiB" : $"{gib:0.0} GiB";
    }

    private static string FormatMarketingNumber(double value)
    {
        var roundedWhole = Math.Round(value, MidpointRounding.AwayFromZero);
        var wholeTolerance = value >= 100 ? 0.5 : 0.15;
        if (Math.Abs(value - roundedWhole) < wholeTolerance)
            return $"{roundedWhole:0}";

        return value >= 10 ? $"{value:0.#}" : $"{value:0.##}";
    }

}

internal static class UptimeFormatter
{
    public static string FormatShort(TimeSpan? uptime)
    {
        if (uptime is null)
            return "n/a";

        var value = uptime.Value < TimeSpan.Zero ? TimeSpan.Zero : uptime.Value;
        var minutes = Math.Floor(value.TotalMinutes);
        if (minutes <= 120)
            return Clamp4($"{Math.Max(0, (int)minutes)}m");

        var hours = value.TotalHours;
        if (hours <= 24)
            return Clamp4(FormatCompactUnit(hours, "h"));

        var days = value.TotalDays;
        if (days <= 365)
            return Clamp4(FormatCompactUnit(days, "d"));

        return Clamp4(FormatCompactUnit(days / 365d, "y"));
    }

    public static string FormatExact(TimeSpan? uptime)
    {
        if (uptime is null)
            return "n/a";

        var value = uptime.Value < TimeSpan.Zero ? TimeSpan.Zero : uptime.Value;
        var totalDays = Math.Max(0, (int)Math.Floor(value.TotalDays));
        var years = totalDays / 365;
        var daysAfterYears = totalDays % 365;
        var months = daysAfterYears / 30;
        var days = daysAfterYears % 30;
        var parts = new[]
        {
            FormatUnit(years, "year"),
            FormatUnit(months, "month"),
            FormatUnit(days, "day"),
            FormatUnit(value.Hours, "hour"),
            FormatUnit(value.Minutes, "minute"),
            FormatUnit(value.Seconds, "second")
        };
        return $"{totalDays}:{value.Hours:00}:{value.Minutes:00}:{value.Seconds:00} ({string.Join(", ", parts)})";
    }

    private static string FormatCompactUnit(double value, string suffix)
    {
        if (value < 10)
        {
            var rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
            if (Math.Abs(rounded - Math.Round(rounded)) >= 0.05)
                return $"{rounded:0.0}{suffix}";
        }

        return $"{Math.Round(value, MidpointRounding.AwayFromZero):0}{suffix}";
    }

    private static string Clamp4(string value)
        => value.Length <= 4 ? value : value[..4];

    private static string FormatUnit(int value, string unit)
        => $"{value} {unit}{(value == 1 ? string.Empty : "s")}";
}

internal static class ReplicationFormatter
{
    public static string Display(string state, string health)
    {
        state = Normalize(state);
        health = Normalize(health);
        if (IsNotConfigured(state))
            return "N/A";

        if (!IsNotConfigured(health) && !IsNotConfigured(state))
            return $"{health} ({state})";

        return !IsNotConfigured(health) ? health : state;
    }

    public static string Status(string state, string health)
    {
        state = Normalize(state);
        health = Normalize(health);
        if (IsNotConfigured(state))
            return "N/A";

        var text = $"{health} {state}".Trim();
        if (ContainsAny(text, "Critical", "Error", "Failed", "Failover", "Suspended", "ResynchronizationRequired"))
            return "HOT";

        if (ContainsAny(text, "Warning", "InProgress", "Waiting", "ReadyForInitialReplication", "Resynchronizing"))
            return "BUSY";

        return "OK";
    }

    private static string Normalize(string value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static bool IsNotConfigured(string value)
        => string.IsNullOrWhiteSpace(value)
           || value.Equals("N/A", StringComparison.OrdinalIgnoreCase)
           || value.Equals("None", StringComparison.OrdinalIgnoreCase)
           || value.Equals("Disabled", StringComparison.OrdinalIgnoreCase)
           || value.Equals("NotApplicable", StringComparison.OrdinalIgnoreCase)
           || value.Equals("Not Applicable", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAny(string value, params string[] needles)
        => needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
}

internal sealed class RollingHistory
{
    private readonly TimeSpan window;
    private readonly Dictionary<string, Queue<HistoryPoint>> points = new();

    public RollingHistory(TimeSpan window) => this.window = window;

    public HostRow Apply(string key, HostRow row)
    {
        var values = Add(key, row.Metrics);
        return row with
        {
            Cpu = row.Cpu with { Max = values[nameof(row.Cpu)] },
            Mem = row.Mem with { Max = values[nameof(row.Mem)] },
            Ram = row.Ram with
            {
                InUse = row.Ram.InUse with { Max = values[nameof(row.Ram) + nameof(row.Ram.InUse)] },
                Processes = row.Ram.Processes with { Max = values[nameof(row.Ram) + nameof(row.Ram.Processes)] },
                Kernel = row.Ram.Kernel with { Max = values[nameof(row.Ram) + nameof(row.Ram.Kernel)] },
                Modified = row.Ram.Modified with { Max = values[nameof(row.Ram) + nameof(row.Ram.Modified)] },
                StandbyCache = row.Ram.StandbyCache with { Max = values[nameof(row.Ram) + nameof(row.Ram.StandbyCache)] },
                Free = row.Ram.Free with { Max = values[nameof(row.Ram) + nameof(row.Ram.Free)] }
            },
            Io = row.Io with { Max = values[nameof(row.Io)] },
            Net = row.Net with { Max = values[nameof(row.Net)] }
        };
    }

    public VmRow Apply(string key, VmRow row)
    {
        var values = Add(key, row.Metrics);
        return row with
        {
            Cpu = row.Cpu with { Max = values[nameof(row.Cpu)] },
            Mem = row.Mem with { Max = values[nameof(row.Mem)] },
            Io = row.Io with { Max = values[nameof(row.Io)] },
            Net = row.Net with { Max = values[nameof(row.Net)] },
            Iops = row.Iops with { Max = values[nameof(row.Iops)] },
            Latency = row.Latency with { Max = values[nameof(row.Latency)] }
        };
    }

    public DiskRow Apply(string key, DiskRow row)
    {
        var values = Add(key, row.Metrics);
        var freeMin = points.TryGetValue(key, out var queue)
            ? queue.Select(p => p.Values[nameof(row.Free)]).Where(v => !double.IsNaN(v)).DefaultIfEmpty(row.Free.Current).Min()
            : row.Free.Current;
        return row with
        {
            Free = row.Free with { Max = freeMin },
            Io = row.Io with { Max = values[nameof(row.Io)] },
            ReadIo = row.ReadIo with { Max = values[nameof(row.ReadIo)] },
            WriteIo = row.WriteIo with { Max = values[nameof(row.WriteIo)] },
            Iops = row.Iops with { Max = values[nameof(row.Iops)] },
            ReadIops = row.ReadIops with { Max = values[nameof(row.ReadIops)] },
            WriteIops = row.WriteIops with { Max = values[nameof(row.WriteIops)] },
            QueueDepth = row.QueueDepth with { Max = values[nameof(row.QueueDepth)] },
            Latency = row.Latency with { Max = values[nameof(row.Latency)] }
        };
    }

    public PhysicalDiskRow Apply(string key, PhysicalDiskRow row)
    {
        var values = Add(key, row.Metrics);
        return row with
        {
            Io = row.Io with { Max = values[nameof(row.Io)] },
            ReadIo = row.ReadIo with { Max = values[nameof(row.ReadIo)] },
            WriteIo = row.WriteIo with { Max = values[nameof(row.WriteIo)] },
            Iops = row.Iops with { Max = values[nameof(row.Iops)] },
            ReadIops = row.ReadIops with { Max = values[nameof(row.ReadIops)] },
            WriteIops = row.WriteIops with { Max = values[nameof(row.WriteIops)] },
            QueueDepth = row.QueueDepth with { Max = values[nameof(row.QueueDepth)] },
            Latency = row.Latency with { Max = values[nameof(row.Latency)] }
        };
    }

    public NetworkRow Apply(string key, NetworkRow row)
    {
        var values = Add(key, row.Metrics);
        return row with
        {
            Throughput = row.Throughput with { Max = values[nameof(row.Throughput)] },
            Rx = row.Rx with { Max = values[nameof(row.Rx)] },
            Tx = row.Tx with { Max = values[nameof(row.Tx)] },
            RdmaThroughput = row.RdmaThroughput with { Max = values[nameof(row.RdmaThroughput)] },
            RdmaRx = row.RdmaRx with { Max = values[nameof(row.RdmaRx)] },
            RdmaTx = row.RdmaTx with { Max = values[nameof(row.RdmaTx)] },
            Drops = row.Drops with { Max = values[nameof(row.Drops)] }
        };
    }

    public NetworkSwitchRow Apply(string key, NetworkSwitchRow row)
    {
        var values = Add(key, row.Metrics);
        return row with
        {
            Throughput = row.Throughput with { Max = values[nameof(row.Throughput)] },
            Rx = row.Rx with { Max = values[nameof(row.Rx)] },
            Tx = row.Tx with { Max = values[nameof(row.Tx)] },
            RdmaThroughput = row.RdmaThroughput with { Max = values[nameof(row.RdmaThroughput)] },
            RdmaRx = row.RdmaRx with { Max = values[nameof(row.RdmaRx)] },
            RdmaTx = row.RdmaTx with { Max = values[nameof(row.RdmaTx)] },
            Drops = row.Drops with { Max = values[nameof(row.Drops)] }
        };
    }

    public VmTopologyRow Apply(string key, VmTopologyRow row)
    {
        return row with
        {
            Disks = row.Disks.Select(disk =>
            {
                var diskKey = $"{key}:vdisk:{disk.Path}:{disk.Name}";
                var values = Add(diskKey, disk.Metrics);
                return disk with
                {
                    TotalMbpsMax = values[nameof(disk.TotalMbps)],
                    TotalIopsMax = values[nameof(disk.TotalIops)],
                    ReadMbpsMax = values[nameof(disk.ReadMbps)],
                    ReadIopsMax = values[nameof(disk.ReadIops)],
                    WriteMbpsMax = values[nameof(disk.WriteMbps)],
                    WriteIopsMax = values[nameof(disk.WriteIops)]
                };
            }).ToArray(),
            Networks = row.Networks.Select(adapter =>
            {
                var adapterKey = $"{key}:vnic:{adapter.Name}:{adapter.SwitchName}";
                var values = Add(adapterKey, adapter.Metrics);
                return adapter with
                {
                    ThroughputMbpsMax = values[nameof(adapter.ThroughputMbps)],
                    RxMbpsMax = values[nameof(adapter.RxMbps)],
                    TxMbpsMax = values[nameof(adapter.TxMbps)]
                };
            }).ToArray(),
            Checkpoints = row.Checkpoints.Select(checkpoint =>
            {
                var checkpointKey = $"{key}:checkpoint:{checkpoint.Path}";
                var previousSize = LastValue(checkpointKey, nameof(checkpoint.SizeMb), checkpoint.SizeMb);
                var current = checkpoint with { ChangeMb = checkpoint.SizeMb - previousSize };
                var values = Add(checkpointKey, current.Metrics);
                return current with
                {
                    SizeMbMax = values[nameof(checkpoint.SizeMb)],
                    ChangeMbMax = values[nameof(checkpoint.ChangeMb)]
                };
            }).ToArray()
        };
    }

    private double LastValue(string key, string metricName, double fallback)
    {
        if (!points.TryGetValue(key, out var queue) || queue.Count == 0)
            return fallback;

        var latest = queue.LastOrDefault();
        return latest?.Values.TryGetValue(metricName, out var value) == true ? value : fallback;
    }

    private Dictionary<string, double> Add(string key, IReadOnlyDictionary<string, double> values)
    {
        var now = DateTime.UtcNow;
        if (!points.TryGetValue(key, out var queue))
        {
            queue = new Queue<HistoryPoint>();
            points[key] = queue;
        }

        queue.Enqueue(new HistoryPoint(now, values));
        while (queue.Count > 0 && now - queue.Peek().At > window)
            queue.Dequeue();

        return values.Keys.ToDictionary(k => k, k => queue.Select(p => p.Values[k]).Where(v => !double.IsNaN(v)).DefaultIfEmpty(values[k]).Max());
    }

    private sealed record HistoryPoint(DateTime At, IReadOnlyDictionary<string, double> Values);
}

