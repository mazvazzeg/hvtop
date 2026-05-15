namespace hvtop.Native;

internal sealed class PdhQuery : IDisposable
{
    private readonly nint query;

    public PdhQuery()
    {
        var status = Native.PdhOpenQuery(null, 0, out query);
        Native.ThrowIfError(status, "PdhOpenQuery");
    }

    public Counter Add(string path)
    {
        var status = Native.PdhAddEnglishCounter(query, path, 0, out var counter);
        Native.ThrowIfError(status, "PdhAddEnglishCounter " + path);
        return new Counter(counter, path);
    }

    public void Collect()
    {
        var status = Native.PdhCollectQueryData(query);
        Native.ThrowIfError(status, "PdhCollectQueryData");
    }

    public void Dispose()
    {
        if (query != 0)
            Native.PdhCloseQuery(query);
    }
}

internal sealed record Counter(nint Handle, string Path)
{
    public double Read()
    {
        var status = Native.PdhGetFormattedCounterValue(Handle, Native.PDH_FMT_DOUBLE, out _, out var value);
        if (status != 0 || value.CStatus != 0)
            return double.NaN;
        return value.DoubleValue;
    }
}

internal static partial class Native
{
    public const uint PDH_FMT_DOUBLE = 0x00000200;
    public const uint IF_OPER_STATUS_UP = 1;
    public const uint IF_TYPE_ETHERNET_CSMACD = 6;
    public const uint IF_TYPE_SOFTWARE_LOOPBACK = 24;
    public const uint IF_TYPE_IEEE80211 = 71;
    public const byte IF_HARDWARE_INTERFACE = 0x01;
    private const ushort ALL_PROCESSOR_GROUPS = 0xFFFF;
    private const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int PdhOpenQuery(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int PdhAddEnglishCounter(nint query, string fullCounterPath, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    public static partial int PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    public static partial int PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out PdhFmtCounterValue value);

    [LibraryImport("pdh.dll")]
    public static partial int PdhCloseQuery(nint query);

    [LibraryImport("Iphlpapi.dll")]
    public static partial uint GetIfTable2(out nint table);

    [LibraryImport("Iphlpapi.dll")]
    public static partial uint GetIfEntry2(ref MibIfRow2 row);

    [LibraryImport("Iphlpapi.dll")]
    public static partial void FreeMibTable(nint memory);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhExpandWildCardPath(
        string? dataSource,
        string wildcardPath,
        char[]? expandedPathList,
        ref uint bufferSize,
        uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailable, out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetActiveProcessorCount(ushort groupNumber);

    public static int GetActiveLogicalProcessorCount()
    {
        try
        {
            var count = GetActiveProcessorCount(ALL_PROCESSOR_GROUPS);
            if (count > 0 && count <= int.MaxValue)
                return (int)count;
        }
        catch
        {
        }

        return Environment.ProcessorCount;
    }

    public static ulong GetPhysicalMemoryBytes()
    {
        var buffer = new MemoryStatusEx();
        buffer.Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        return GlobalMemoryStatusEx(ref buffer) ? buffer.TotalPhys : 0;
    }

    public static bool TryGetDiskFreeSpace(string path, out ulong freeBytesAvailable, out ulong totalNumberOfBytes)
    {
        freeBytesAvailable = 0;
        totalNumberOfBytes = 0;
        return GetDiskFreeSpaceEx(path, out freeBytesAvailable, out totalNumberOfBytes, out _);
    }

    public static string[] ExpandWildcardPath(string wildcardPath)
    {
        uint size = 0;
        var status = PdhExpandWildCardPath(null, wildcardPath, null, ref size, 0);
        if (status != PDH_MORE_DATA || size == 0) return [];

        var buffer = new char[size];
        status = PdhExpandWildCardPath(null, wildcardPath, buffer, ref size, 0);
        if (status != 0) return [];

        var paths = new List<string>();
        var start = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != '\0') continue;
            if (i == start) break;
            paths.Add(new string(buffer, start, i - start));
            start = i + 1;
        }
        return paths.ToArray();
    }

    public static void ThrowIfError(int status, string operation)
    {
        if (status != 0)
            throw new InvalidOperationException($"{operation} failed with PDH status 0x{status:X8}");
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct PdhFmtCounterValue
{
    public uint CStatus;
    public double DoubleValue;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MemoryStatusEx
{
    public uint Length;
    public uint MemoryLoad;
    public ulong TotalPhys;
    public ulong AvailPhys;
    public ulong TotalPageFile;
    public ulong AvailPageFile;
    public ulong TotalVirtual;
    public ulong AvailVirtual;
    public ulong AvailExtendedVirtual;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct MibIfRow2
{
    public ulong InterfaceLuid;
    public uint InterfaceIndex;
    public Guid InterfaceGuid;
    public fixed char Alias[257];
    public fixed char Description[257];
    public uint PhysicalAddressLength;
    public fixed byte PhysicalAddress[32];
    public fixed byte PermanentPhysicalAddress[32];
    public uint Mtu;
    public uint Type;
    public uint TunnelType;
    public uint MediaType;
    public uint PhysicalMediumType;
    public uint AccessType;
    public uint DirectionType;
    public byte InterfaceAndOperStatusFlags;
    public fixed byte Padding[3];
    public uint OperStatus;
    public uint AdminStatus;
    public uint MediaConnectState;
    public Guid NetworkGuid;
    public uint ConnectionType;
    public ulong TransmitLinkSpeed;
    public ulong ReceiveLinkSpeed;
    public ulong InOctets;
    public ulong InUcastPkts;
    public ulong InNUcastPkts;
    public ulong InDiscards;
    public ulong InErrors;
    public ulong InUnknownProtos;
    public ulong InUcastOctets;
    public ulong InMulticastOctets;
    public ulong InBroadcastOctets;
    public ulong OutOctets;
    public ulong OutUcastPkts;
    public ulong OutNUcastPkts;
    public ulong OutDiscards;
    public ulong OutErrors;
    public ulong OutUcastOctets;
    public ulong OutMulticastOctets;
    public ulong OutBroadcastOctets;
    public ulong OutQLen;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MibIfTable2
{
    public uint NumEntries;
    public MibIfRow2 Table;
}

