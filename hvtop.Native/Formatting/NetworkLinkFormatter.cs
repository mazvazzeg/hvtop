namespace hvtop.Native;

internal static class NetworkLinkFormatter
{
    public static string Format(long bitsPerSecond, bool isUp)
    {
        if (!isUp || bitsPerSecond <= 0)
            return "DOWN";

        if (bitsPerSecond >= 100_000_000_000L) return "100G";
        if (bitsPerSecond >= 40_000_000_000L) return "40G";
        if (bitsPerSecond >= 25_000_000_000L) return "25G";
        if (bitsPerSecond >= 10_000_000_000L) return "10G";
        if (bitsPerSecond >= 1_000_000_000L) return "GbE";
        return "FE";
    }

    public static long ParseBitsPerSecond(string link)
    {
        if (string.IsNullOrWhiteSpace(link) || link.Equals("DOWN", StringComparison.OrdinalIgnoreCase))
            return 0;

        return link.Trim().ToUpperInvariant() switch
        {
            "FE" => 100_000_000L,
            "GBE" => 1_000_000_000L,
            "10G" => 10_000_000_000L,
            "25G" => 25_000_000_000L,
            "40G" => 40_000_000_000L,
            "100G" => 100_000_000_000L,
            _ => 0
        };
    }
}

