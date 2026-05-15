namespace hvtop.Native;

internal static class PdhWildcardReader
{
    public static Dictionary<string, int> LastInstanceCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, double> Read(string wildcardPath, Func<string, string>? normalizeInstance = null)
    {
        var result = ReadMany([(wildcardPath, wildcardPath)], normalizeInstance);
        if (!result.TryGetValue(wildcardPath, out var group))
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        LastInstanceCounts.Clear();
        foreach (var pair in group.InstanceCounts)
            LastInstanceCounts[pair.Key] = pair.Value;
        return group.Values;
    }

    public static Dictionary<string, PdhWildcardGroupResult> ReadMany((string Name, string Path)[] specs, Func<string, string>? normalizeInstance = null)
    {
        normalizeInstance ??= NormalizeDefaultInstance;
        var results = specs
            .Select(spec => spec.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                name => name,
                _ => new PdhWildcardGroupResult(
                    new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase);

        using var query = new PdhQuery();
        var counters = new List<(string Name, Counter Counter)>();
        foreach (var spec in specs)
        {
            string[] paths;
            try
            {
                paths = Native.ExpandWildcardPath(spec.Path);
            }
            catch
            {
                continue;
            }

            foreach (var path in paths)
            {
                try
                {
                    counters.Add((spec.Name, query.Add(path)));
                }
                catch
                {
                    // Some wildcard expansions can include transient instances. Ignore them.
                }
            }
        }

        if (counters.Count == 0) return results;

        try
        {
            query.Collect();
            Thread.Sleep(100);
            query.Collect();
        }
        catch
        {
            return results;
        }

        foreach (var item in counters)
        {
            if (!results.TryGetValue(item.Name, out var group))
                continue;

            var instance = normalizeInstance(ExtractInstance(item.Counter.Path));
            if (string.IsNullOrWhiteSpace(instance)) continue;
            group.InstanceCounts[instance] = group.InstanceCounts.TryGetValue(instance, out var count) ? count + 1 : 1;
            var value = item.Counter.Read();
            if (double.IsNaN(value)) continue;
            group.Values[instance] = group.Values.TryGetValue(instance, out var prior) ? prior + value : value;
        }

        return results;
    }

    private static string ExtractInstance(string counterPath)
    {
        var open = counterPath.IndexOf('(');
        if (open < 0) return string.Empty;
        var close = counterPath.IndexOf(')', open + 1);
        if (close < 0 || close <= open + 1) return string.Empty;
        return counterPath.Substring(open + 1, close - open - 1);
    }

    private static string NormalizeDefaultInstance(string instance)
    {
        var value = instance.Trim();
        var colon = value.IndexOf(':');
        if (colon > 0) value = value[..colon];
        var dash = value.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) value = value[..dash];
        var slash = value.IndexOf('/');
        if (slash > 0) value = value[..slash];
        return value.Trim();
    }
}

internal sealed record PdhWildcardGroupResult(Dictionary<string, double> Values, Dictionary<string, int> InstanceCounts);

