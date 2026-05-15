namespace hvtop.Native;

internal sealed class NetworkSampler
{
    private readonly ConcurrentDictionary<string, InterfaceCounterSnapshot> previous = new();

    public AdapterRate[] Sample()
    {
        var now = DateTime.UtcNow;
        var pdhRates = NetworkPdhSampler.Read();
        var rdmaRates = RdmaPdhSampler.Read();
        return ReadInterfaceRows()
            .Where(row => row.Type != Native.IF_TYPE_SOFTWARE_LOOPBACK)
            .Select(row => TryRead(row, now, pdhRates, rdmaRates, out var rate) ? rate : null)
            .Where(rate => rate is not null)
            .Cast<AdapterRate>()
            .ToArray();
    }

    private bool TryRead(MibIfRow2 row, DateTime now, NetworkPdhRate[] pdhRates, RdmaPdhRate[] rdmaRates, out AdapterRate rate)
    {
        if (row.InterfaceGuid == Guid.Empty)
        {
            rate = default!;
            return false;
        }

        var key = row.InterfaceGuid.ToString("D");
        var current = new InterfaceCounterSnapshot(now, row.InOctets, row.OutOctets, row.InDiscards + row.OutDiscards);
        var prior = previous.GetOrAdd(key, current);
        var seconds = Math.Max(0.001, (now - prior.At).TotalSeconds);
        var isUp = row.OperStatus == Native.IF_OPER_STATUS_UP;
        var rx = isUp && current.InOctets >= prior.InOctets ? (current.InOctets - prior.InOctets) / seconds : 0;
        var tx = isUp && current.OutOctets >= prior.OutOctets ? (current.OutOctets - prior.OutOctets) / seconds : 0;
        var rawRx = rx;
        var rawTx = tx;
        var drops = isUp && current.Discards >= prior.Discards ? (current.Discards - prior.Discards) / seconds : 0;

        previous[key] = current;
        string alias;
        string description;
        unsafe
        {
            alias = ReadRowString(row.Alias, $"if-{row.InterfaceIndex}");
            description = ReadRowString(row.Description, alias);
        }

        var pdhInstance = string.Empty;
        var pdhRx = 0d;
        var pdhTx = 0d;
        if (NetworkPdhSampler.TryMatch(pdhRates, alias, description, out var pdhRate))
        {
            pdhInstance = pdhRate.Instance;
            pdhRx = pdhRate.ReceivedBytesPerSecond;
            pdhTx = pdhRate.SentBytesPerSecond;
            rx = Math.Max(rx, pdhRate.ReceivedBytesPerSecond);
            tx = Math.Max(tx, pdhRate.SentBytesPerSecond);
        }

        var rdmaInstance = string.Empty;
        var rdmaRx = 0d;
        var rdmaTx = 0d;
        if (RdmaPdhSampler.TryMatch(rdmaRates, alias, description, out var rdmaRate))
        {
            rdmaInstance = rdmaRate.Instance;
            rdmaRx = rdmaRate.ReceivedBytesPerSecond;
            rdmaTx = rdmaRate.SentBytesPerSecond;
        }

        rate = new AdapterRate(
            alias,
            description,
            key,
            unchecked((long)Math.Max(row.ReceiveLinkSpeed, row.TransmitLinkSpeed)),
            isUp,
            (row.InterfaceAndOperStatusFlags & Native.IF_HARDWARE_INTERFACE) != 0,
            IsVisibleAdapter(row, alias, description),
            rx,
            tx,
            rawRx,
            rawTx,
            pdhInstance,
            pdhRx,
            pdhTx,
            rdmaInstance,
            rdmaRx,
            rdmaTx,
            drops);
        return true;
    }

    private static bool IsVisibleAdapter(MibIfRow2 row, string alias, string description)
    {
        if (row.Type == Native.IF_TYPE_SOFTWARE_LOOPBACK)
            return false;

        var text = $"{alias} {description}";
        if (ContainsAny(text,
                "Scheduler-0000",
                "Filter-0000",
                "WFP",
                "QoS Packet Scheduler",
                "LightWeight Filter",
                "Kernel Debugger",
                "IP-HTTPS",
                "ISATAP",
                "Teredo",
                "6to4",
                "Loopback"))
            return false;

        if (alias.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase)
            || description.Contains("Hyper-V Virtual Ethernet", StringComparison.OrdinalIgnoreCase))
            return false;

        var hasAddress = row.PhysicalAddressLength > 0;
        var hasCounters = row.InOctets > 0 || row.OutOctets > 0;
        var hasLinkSpeed = row.ReceiveLinkSpeed > 0 || row.TransmitLinkSpeed > 0;
        var isEthernetLike = row.Type is Native.IF_TYPE_ETHERNET_CSMACD or Native.IF_TYPE_IEEE80211;
        var isHardware = (row.InterfaceAndOperStatusFlags & Native.IF_HARDWARE_INTERFACE) != 0;

        return isEthernetLike && (isHardware || hasAddress || hasLinkSpeed || hasCounters);
    }

    private static bool ContainsAny(string value, params string[] needles)
        => needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static MibIfRow2[] ReadInterfaceRows()
    {
        if (Native.GetIfTable2(out var tablePtr) != 0 || tablePtr == nint.Zero)
            return [];

        try
        {
            var table = Marshal.PtrToStructure<MibIfTable2>(tablePtr);
            var offset = Marshal.OffsetOf<MibIfTable2>(nameof(MibIfTable2.Table)).ToInt32();
            var rowSize = Marshal.SizeOf<MibIfRow2>();
            var rows = new MibIfRow2[table.NumEntries];
            for (var i = 0; i < table.NumEntries; i++)
            {
                var rowPtr = nint.Add(tablePtr, offset + (i * rowSize));
                rows[i] = Marshal.PtrToStructure<MibIfRow2>(rowPtr);
            }

            return rows;
        }
        finally
        {
            Native.FreeMibTable(tablePtr);
        }
    }

    private static unsafe string ReadRowString(char* buffer, string fallback)
    {
        var value = new string(buffer).TrimEnd('\0').Trim();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private sealed record InterfaceCounterSnapshot(DateTime At, ulong InOctets, ulong OutOctets, ulong Discards);
}

internal static class NetworkPdhSampler
{
    public static NetworkPdhRate[] LastRates { get; private set; } = [];

    public static NetworkPdhRate[] Read()
    {
        var values = PdhWildcardReader.ReadMany(
            [
                ("rx", @"\Network Interface(*)\Bytes Received/sec"),
                ("tx", @"\Network Interface(*)\Bytes Sent/sec")
            ],
            NormalizeInstance);
        var rx = values["rx"].Values;
        var tx = values["tx"].Values;
        var rates = rx.Keys
            .Concat(tx.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new NetworkPdhRate(
                name,
                rx.TryGetValue(name, out var received) ? received : 0,
                tx.TryGetValue(name, out var sent) ? sent : 0))
            .ToArray();
        LastRates = rates;
        return rates;
    }

    public static bool TryMatch(NetworkPdhRate[] rates, string alias, string description, out NetworkPdhRate rate)
    {
        var candidates = new[] { alias, description }
            .Select(NormalizeForMatch)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var candidate in candidates)
        {
            var exact = rates.FirstOrDefault(r => NormalizeForMatch(r.Instance).Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                rate = exact;
                return true;
            }
        }

        var fuzzy = rates
            .Select(r => new { Rate = r, Key = NormalizeForMatch(r.Instance) })
            .Where(item => candidates.Any(candidate => item.Key.Contains(candidate, StringComparison.OrdinalIgnoreCase) || candidate.Contains(item.Key, StringComparison.OrdinalIgnoreCase)))
            .Select(item => item.Rate)
            .Distinct()
            .ToArray();

        if (fuzzy.Length == 1)
        {
            rate = fuzzy[0];
            return true;
        }

        rate = default!;
        return false;
    }

    private static string NormalizeInstance(string instance)
        => instance.Trim();

    private static string NormalizeForMatch(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        var index = 0;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
                buffer[index++] = char.ToLowerInvariant(ch);
        }

        return new string(buffer[..index]);
    }
}

internal sealed record NetworkPdhRate(string Instance, double ReceivedBytesPerSecond, double SentBytesPerSecond);

internal static class RdmaPdhSampler
{
    public static RdmaPdhRate[] LastRates { get; private set; } = [];

    public static RdmaPdhRate[] Read()
    {
        var rx = ReadFirstAvailable(
            @"\RDMA Activity(*)\RDMA Inbound Bytes/sec",
            @"\RDMA Activity(*)\Inbound bytes/sec");
        var tx = ReadFirstAvailable(
            @"\RDMA Activity(*)\RDMA Outbound Bytes/sec",
            @"\RDMA Activity(*)\Outbound bytes/sec");
        var rates = rx.Keys
            .Concat(tx.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new RdmaPdhRate(
                name,
                rx.TryGetValue(name, out var received) ? received : 0,
                tx.TryGetValue(name, out var sent) ? sent : 0))
            .ToArray();
        LastRates = rates;
        return rates;
    }

    private static Dictionary<string, double> ReadFirstAvailable(params string[] paths)
    {
        foreach (var path in paths)
        {
            var values = PdhWildcardReader.Read(path, NormalizeInstance);
            if (values.Count > 0)
                return values;
        }

        return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    }

    public static bool TryMatch(RdmaPdhRate[] rates, string alias, string description, out RdmaPdhRate rate)
    {
        var candidates = new[] { alias, description }
            .Select(NormalizeForMatch)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var candidate in candidates)
        {
            var exact = rates.FirstOrDefault(r => NormalizeForMatch(r.Instance).Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                rate = exact;
                return true;
            }
        }

        var fuzzy = rates
            .Select(r => new { Rate = r, Key = NormalizeForMatch(r.Instance) })
            .Where(item => candidates.Any(candidate => item.Key.Contains(candidate, StringComparison.OrdinalIgnoreCase) || candidate.Contains(item.Key, StringComparison.OrdinalIgnoreCase)))
            .Select(item => item.Rate)
            .Distinct()
            .ToArray();

        if (fuzzy.Length == 1)
        {
            rate = fuzzy[0];
            return true;
        }

        rate = default!;
        return false;
    }

    private static string NormalizeInstance(string instance)
        => instance.Trim();

    private static string NormalizeForMatch(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        var index = 0;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
                buffer[index++] = char.ToLowerInvariant(ch);
        }

        return new string(buffer[..index]);
    }
}

internal sealed record RdmaPdhRate(string Instance, double ReceivedBytesPerSecond, double SentBytesPerSecond);

internal static class HyperVNetworkPdhSampler
{
    public static Dictionary<string, HyperVNetworkPdhRate> ReadSwitchRates()
    {
        return ReadFamily(
                "Hyper-V Virtual Switch",
                @"\Hyper-V Virtual Switch(*)\Bytes/sec",
                @"\Hyper-V Virtual Switch(*)\Bytes Received/sec",
                @"\Hyper-V Virtual Switch(*)\Bytes Sent/sec")
            .Rates
            .GroupBy(rate => rate.Instance, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(rate => Math.Max(rate.TotalBytesPerSecond, rate.ReceivedBytesPerSecond + rate.SentBytesPerSecond)).First(),
                StringComparer.OrdinalIgnoreCase);
    }

    public static HyperVNetworkPdhFamily[] Read()
        =>
        [
            ReadFamily(
                "Hyper-V Virtual Switch",
                @"\Hyper-V Virtual Switch(*)\Bytes/sec",
                @"\Hyper-V Virtual Switch(*)\Bytes Received/sec",
                @"\Hyper-V Virtual Switch(*)\Bytes Sent/sec"),
            ReadFamily(
                "Hyper-V Virtual Switch Port",
                @"\Hyper-V Virtual Switch Port(*)\Bytes/sec",
                @"\Hyper-V Virtual Switch Port(*)\Bytes Received/sec",
                @"\Hyper-V Virtual Switch Port(*)\Bytes Sent/sec"),
            ReadFamily(
                "Hyper-V Virtual Network Adapter",
                @"\Hyper-V Virtual Network Adapter(*)\Bytes/sec",
                @"\Hyper-V Virtual Network Adapter(*)\Bytes Received/sec",
                @"\Hyper-V Virtual Network Adapter(*)\Bytes Sent/sec")
        ];

    private static HyperVNetworkPdhFamily ReadFamily(string name, string totalPath, string rxPath, string txPath)
    {
        var values = PdhWildcardReader.ReadMany(
            [
                ("total", totalPath),
                ("rx", rxPath),
                ("tx", txPath)
            ],
            NormalizeInstance);
        var total = values["total"].Values;
        var rx = values["rx"].Values;
        var tx = values["tx"].Values;
        var rates = total.Keys
            .Concat(rx.Keys)
            .Concat(tx.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(instance => new HyperVNetworkPdhRate(
                instance,
                total.TryGetValue(instance, out var totalValue) ? totalValue : 0,
                rx.TryGetValue(instance, out var rxValue) ? rxValue : 0,
                tx.TryGetValue(instance, out var txValue) ? txValue : 0))
            .ToArray();
        return new HyperVNetworkPdhFamily(name, rates);
    }

    private static string NormalizeInstance(string instance)
        => instance.Trim();
}

internal sealed record HyperVNetworkPdhFamily(string Name, HyperVNetworkPdhRate[] Rates);

internal sealed record HyperVNetworkPdhRate(string Instance, double TotalBytesPerSecond, double ReceivedBytesPerSecond, double SentBytesPerSecond);

internal sealed record AdapterRate(string Name, string Description, string InterfaceId, long LinkSpeedBitsPerSecond, bool IsUp, bool IsHardwareInterface, bool IsVisibleAdapter, double ReceivedBytesPerSecond, double SentBytesPerSecond, double RawReceivedBytesPerSecond, double RawSentBytesPerSecond, string PdhInstance, double PdhReceivedBytesPerSecond, double PdhSentBytesPerSecond, string RdmaInstance, double RdmaReceivedBytesPerSecond, double RdmaSentBytesPerSecond, double DropsPerSecond)
{
    public double TotalBytesPerSecond => ReceivedBytesPerSecond + SentBytesPerSecond;
    public double RdmaTotalBytesPerSecond => RdmaReceivedBytesPerSecond + RdmaSentBytesPerSecond;
}

