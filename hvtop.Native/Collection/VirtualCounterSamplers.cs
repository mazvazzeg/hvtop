namespace hvtop.Native;

internal static class VirtualDiskCounterSampler
{
    public static Dictionary<string, VirtualDiskStats> Read()
    {
        var values = PdhWildcardReader.ReadMany(
            [
                ("read-bytes", @"\Hyper-V Virtual Storage Device(*)\Read Bytes/sec"),
                ("write-bytes", @"\Hyper-V Virtual Storage Device(*)\Write Bytes/sec"),
                ("read-ops", @"\Hyper-V Virtual Storage Device(*)\Read Operations/Sec"),
                ("write-ops", @"\Hyper-V Virtual Storage Device(*)\Write Operations/Sec")
            ],
            NormalizeDiskCounterInstance);
        var readBytes = values["read-bytes"].Values;
        var writeBytes = values["write-bytes"].Values;
        var readOps = values["read-ops"].Values;
        var writeOps = values["write-ops"].Values;
        var keys = new HashSet<string>(readBytes.Keys.Concat(writeBytes.Keys).Concat(readOps.Keys).Concat(writeOps.Keys), StringComparer.OrdinalIgnoreCase);
        return keys.ToDictionary(
            key => key,
            key => new VirtualDiskStats(
                ReadValue(readBytes, key) / 1024 / 1024,
                ReadValue(readOps, key),
                ReadValue(writeBytes, key) / 1024 / 1024,
                ReadValue(writeOps, key)),
            StringComparer.OrdinalIgnoreCase);
    }

    private static double ReadValue(Dictionary<string, double> values, string key)
        => values.TryGetValue(key, out var value) && !double.IsNaN(value) ? value : 0;

    private static string NormalizeDiskCounterInstance(string instance)
        => HyperVNaming.NormalizeStorageCounterIdentity(instance);
}

internal sealed record VirtualDiskStats(double ReadMbps, double ReadIops, double WriteMbps, double WriteIops);

internal static class VirtualNetworkCounterSampler
{
    public static Dictionary<string, VirtualNetworkStats> Read()
    {
        var values = PdhWildcardReader.ReadMany(
            [
                ("rx", @"\Hyper-V Virtual Network Adapter(*)\Bytes Received/sec"),
                ("tx", @"\Hyper-V Virtual Network Adapter(*)\Bytes Sent/sec")
            ],
            NormalizeNetworkCounterInstance);
        var rx = values["rx"].Values;
        var tx = values["tx"].Values;
        var keys = new HashSet<string>(rx.Keys.Concat(tx.Keys), StringComparer.OrdinalIgnoreCase);
        return keys.ToDictionary(
            key => key,
            key => new VirtualNetworkStats(
                ReadValue(rx, key) / 1024 / 1024,
                ReadValue(tx, key) / 1024 / 1024),
            StringComparer.OrdinalIgnoreCase);
    }

    private static double ReadValue(Dictionary<string, double> values, string key)
        => values.TryGetValue(key, out var value) && !double.IsNaN(value) ? value : 0;

    private static string NormalizeNetworkCounterInstance(string instance)
        => HyperVNaming.NormalizeStorageCounterIdentity(instance);
}

internal sealed record VirtualNetworkStats(double RxMbps, double TxMbps);

