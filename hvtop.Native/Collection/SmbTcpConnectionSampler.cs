namespace hvtop.Native;

internal static class SmbTcpConnectionSampler
{
    private const uint ErrorInsufficientBuffer = 122;
    private const uint TcpStateEstablished = 5;
    private const ushort SmbPort = 445;
    private const int TcpConnectionEstatsData = 1;
    private static readonly object Gate = new();
    private static Dictionary<TcpConnectionKey, TcpByteSample> previous = [];

    public static SmbTcpTrafficMap Read()
    {
        var now = Stopwatch.GetTimestamp();
        var readings = ReadIpv4().Concat(ReadIpv6()).ToArray();

        lock (Gate)
        {
            var next = new Dictionary<TcpConnectionKey, TcpByteSample>();
            var totals = new Dictionary<string, TrafficAccumulator>(StringComparer.OrdinalIgnoreCase);

            foreach (var reading in readings)
            {
                next[reading.Key] = new TcpByteSample(now, reading.BytesIn, reading.BytesOut);
                var accumulator = totals.GetValueOrDefault(reading.RemoteAddress) ?? new TrafficAccumulator();
                accumulator.ConnectionCount++;

                if (previous.TryGetValue(reading.Key, out var baseline)
                    && reading.BytesIn >= baseline.BytesIn
                    && reading.BytesOut >= baseline.BytesOut)
                {
                    var elapsed = Stopwatch.GetElapsedTime(baseline.Timestamp, now).TotalSeconds;
                    if (elapsed > 0)
                    {
                        accumulator.RxMbps += (reading.BytesIn - baseline.BytesIn) / elapsed / 1024d / 1024d;
                        accumulator.TxMbps += (reading.BytesOut - baseline.BytesOut) / elapsed / 1024d / 1024d;
                        accumulator.HasRate = true;
                    }
                }

                totals[reading.RemoteAddress] = accumulator;
            }

            previous = next;
            return new SmbTcpTrafficMap(totals.ToDictionary(
                pair => pair.Key,
                pair => new SmbTcpTraffic(
                    pair.Value.HasRate ? pair.Value.RxMbps : double.NaN,
                    pair.Value.HasRate ? pair.Value.TxMbps : double.NaN,
                    pair.Value.ConnectionCount),
                StringComparer.OrdinalIgnoreCase));
        }
    }

    public static string NormalizeAddress(string value)
    {
        var text = (value ?? string.Empty).Trim().TrimStart('\\').Trim('[', ']');
        var percent = text.IndexOf('%');
        var addressText = percent > 0 ? text[..percent] : text;
        if (!IPAddress.TryParse(addressText, out var address))
            return text;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return address.ToString();
    }

    private static TcpConnectionReading[] ReadIpv4()
    {
        var readings = new List<TcpConnectionReading>();
        foreach (var rowValue in ReadTcp4Rows())
        {
            var row = rowValue;
            if (row.State != TcpStateEstablished || Port(row.LocalPort) != SmbPort)
                continue;

            var enabled = new TcpEstatsDataRw { EnableCollection = 1 };
            _ = SetPerTcpConnectionEStats(ref row, TcpConnectionEstatsData, ref enabled, 0, 1, 0);
            var status = GetPerTcpConnectionEStats(
                ref row,
                TcpConnectionEstatsData,
                nint.Zero, 0, 0,
                nint.Zero, 0, 0,
                out var stats, 0, (uint)Marshal.SizeOf<TcpEstatsDataRod>());
            if (status != 0)
                continue;

            var remote = NormalizeAddress(new IPAddress(BitConverter.GetBytes(row.RemoteAddr)).ToString());
            var local = NormalizeAddress(new IPAddress(BitConverter.GetBytes(row.LocalAddr)).ToString());
            readings.Add(new TcpConnectionReading(
                new TcpConnectionKey(4, local, Port(row.LocalPort), remote, Port(row.RemotePort)),
                remote,
                stats.DataBytesIn,
                stats.DataBytesOut));
        }

        return readings.ToArray();
    }

    private static unsafe TcpConnectionReading[] ReadIpv6()
    {
        var readings = new List<TcpConnectionReading>();
        foreach (var rowValue in ReadTcp6Rows())
        {
            var row = rowValue;
            if (row.State != TcpStateEstablished || Port(row.LocalPort) != SmbPort)
                continue;

            var enabled = new TcpEstatsDataRw { EnableCollection = 1 };
            _ = SetPerTcp6ConnectionEStats(ref row, TcpConnectionEstatsData, ref enabled, 0, 1, 0);
            var status = GetPerTcp6ConnectionEStats(
                ref row,
                TcpConnectionEstatsData,
                nint.Zero, 0, 0,
                nint.Zero, 0, 0,
                out var stats, 0, (uint)Marshal.SizeOf<TcpEstatsDataRod>());
            if (status != 0)
                continue;

            var localBytes = new byte[16];
            var remoteBytes = new byte[16];
            for (var i = 0; i < 16; i++)
            {
                localBytes[i] = row.LocalAddr[i];
                remoteBytes[i] = row.RemoteAddr[i];
            }
            var local = NormalizeAddress(new IPAddress(localBytes, row.LocalScopeId).ToString());
            var remote = NormalizeAddress(new IPAddress(remoteBytes, row.RemoteScopeId).ToString());

            readings.Add(new TcpConnectionReading(
                new TcpConnectionKey(6, local, Port(row.LocalPort), remote, Port(row.RemotePort)),
                remote,
                stats.DataBytesIn,
                stats.DataBytesOut));
        }

        return readings.ToArray();
    }

    private static MibTcpRow[] ReadTcp4Rows()
        => ReadTable<MibTcpRow>(GetTcpTable, Marshal.SizeOf<MibTcpRow>());

    private static MibTcp6Row[] ReadTcp6Rows()
        => ReadTable<MibTcp6Row>(GetTcp6Table, Marshal.SizeOf<MibTcp6Row>());

    private static T[] ReadTable<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors | System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicConstructors)] T>(TcpTableReader reader, int rowSize) where T : struct
    {
        uint size = 0;
        var status = reader(nint.Zero, ref size, false);
        if (status != ErrorInsufficientBuffer || size < sizeof(uint))
            return [];

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = reader(buffer, ref size, false);
            if (status != 0)
                return [];

            var count = Marshal.ReadInt32(buffer);
            if (count <= 0)
                return [];

            var rows = new T[count];
            var current = nint.Add(buffer, sizeof(uint));
            for (var i = 0; i < count; i++)
            {
                rows[i] = Marshal.PtrToStructure<T>(current);
                current = nint.Add(current, rowSize);
            }
            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ushort Port(uint value)
        => unchecked((ushort)IPAddress.NetworkToHostOrder((short)(value & 0xffff)));

    private delegate uint TcpTableReader(nint table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetTcpTable(nint table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetTcp6Table(nint table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order);

    [DllImport("iphlpapi.dll")]
    private static extern uint SetPerTcpConnectionEStats(ref MibTcpRow row, int statsType, ref TcpEstatsDataRw rw, uint rwVersion, uint rwSize, uint offset);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetPerTcpConnectionEStats(ref MibTcpRow row, int statsType, nint rw, uint rwVersion, uint rwSize, nint ros, uint rosVersion, uint rosSize, out TcpEstatsDataRod rod, uint rodVersion, uint rodSize);

    [DllImport("iphlpapi.dll")]
    private static extern uint SetPerTcp6ConnectionEStats(ref MibTcp6Row row, int statsType, ref TcpEstatsDataRw rw, uint rwVersion, uint rwSize, uint offset);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetPerTcp6ConnectionEStats(ref MibTcp6Row row, int statsType, nint rw, uint rwVersion, uint rwSize, nint ros, uint rosVersion, uint rosSize, out TcpEstatsDataRod rod, uint rodVersion, uint rodSize);

    private sealed class TrafficAccumulator
    {
        public double RxMbps;
        public double TxMbps;
        public int ConnectionCount;
        public bool HasRate;
    }
}

internal sealed class SmbTcpTrafficMap(IReadOnlyDictionary<string, SmbTcpTraffic> values)
{
    public SmbTcpTraffic For(string clientAddress)
        => values.GetValueOrDefault(SmbTcpConnectionSampler.NormalizeAddress(clientAddress)) ?? SmbTcpTraffic.Unavailable;
}

internal sealed record SmbTcpTraffic(double RxMbps, double TxMbps, int ConnectionCount)
{
    public static SmbTcpTraffic Unavailable { get; } = new(double.NaN, double.NaN, 0);
    public bool Available => !double.IsNaN(RxMbps) || !double.IsNaN(TxMbps);
}

internal sealed record TcpConnectionReading(TcpConnectionKey Key, string RemoteAddress, ulong BytesIn, ulong BytesOut);
internal readonly record struct TcpConnectionKey(int AddressFamily, string LocalAddress, ushort LocalPort, string RemoteAddress, ushort RemotePort);
internal readonly record struct TcpByteSample(long Timestamp, ulong BytesIn, ulong BytesOut);

[StructLayout(LayoutKind.Sequential)]
internal struct MibTcpRow
{
    public uint State;
    public uint LocalAddr;
    public uint LocalPort;
    public uint RemoteAddr;
    public uint RemotePort;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MibTcp6Row
{
    public uint State;
    public fixed byte LocalAddr[16];
    public uint LocalScopeId;
    public uint LocalPort;
    public fixed byte RemoteAddr[16];
    public uint RemoteScopeId;
    public uint RemotePort;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct TcpEstatsDataRw
{
    public byte EnableCollection;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TcpEstatsDataRod
{
    public ulong DataBytesOut;
    public ulong DataSegsOut;
    public ulong DataBytesIn;
    public ulong DataSegsIn;
    public ulong SegsOut;
    public ulong SegsIn;
    public uint SoftErrors;
    public uint SoftErrorReason;
    public uint SndUna;
    public uint SndNxt;
    public uint SndMax;
    public ulong ThruBytesAcked;
    public uint RcvNxt;
    public ulong ThruBytesReceived;
}
