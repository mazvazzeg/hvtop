namespace hvtop.Native;

internal static class NetworkTopologyMatcher
{
    public static NetworkRow? MatchAdapter(NetworkRow[] adapters, string primaryCandidate, string? secondaryCandidate = null)
    {
        var candidates = new[] { primaryCandidate, secondaryCandidate }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (candidates.Length == 0)
            return null;

        var exactName = adapters.FirstOrDefault(adapter =>
            candidates.Any(value => adapter.Name.Equals(value, StringComparison.OrdinalIgnoreCase)));
        if (exactName is not null)
            return exactName;

        var exactDescription = adapters.FirstOrDefault(adapter =>
            candidates.Any(value => adapter.Description.Equals(value, StringComparison.OrdinalIgnoreCase)));
        if (exactDescription is not null)
            return exactDescription;

        var fuzzyMatches = adapters
            .Where(adapter => !IsVirtualSwitchAdapter(adapter))
            .Where(adapter => candidates.Any(value =>
                adapter.Name.Contains(value, StringComparison.OrdinalIgnoreCase)
                || value.Contains(adapter.Name, StringComparison.OrdinalIgnoreCase)
                || adapter.Description.Contains(value, StringComparison.OrdinalIgnoreCase)
                || value.Contains(adapter.Description, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (fuzzyMatches.Length == 1)
            return fuzzyMatches[0];

        return null;
    }

    public static NetworkRow MergeWithLive(NetworkRow[] adapters, NetworkUplinkInfo uplink, string hostName = "")
    {
        var live = MatchAdapter(adapters, uplink.Name, uplink.Description);
        if (live is not null)
            return live;

        return new NetworkRow(
            hostName,
            uplink.Name,
            uplink.Description,
            uplink.Link,
            uplink.IsUp,
            uplink.LinkSpeedBitsPerSecond,
            Metric.Mbps(0),
            Metric.Mbps(0),
            Metric.Mbps(0),
            Metric.Mbps(0),
            Metric.Mbps(0),
            Metric.Mbps(0),
            Metric.Plain(0),
            uplink.IsUp ? "IDLE" : "OFF");
    }

    private static bool IsVirtualSwitchAdapter(NetworkRow adapter)
        => adapter.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase)
           || adapter.Description.Contains("Hyper-V Virtual Ethernet", StringComparison.OrdinalIgnoreCase);
}

