namespace hvtop.Native;

internal static class HyperVNaming
{
    public static string NormalizeDiskCounterKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        return NormalizeStorageCounterIdentity(Path.GetFileName(name.Trim()));
    }

    public static string NormalizeStorageCounterIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        value = value.Trim().Trim('"');
        value = value.Replace("--?-", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("\\\\?\\", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace('/', '-')
            .Replace('\\', '-')
            .ToLowerInvariant();

        while (value.Contains("--", StringComparison.Ordinal))
            value = value.Replace("--", "-", StringComparison.Ordinal);

        return value.Trim();
    }

    public static string NormalizeVmIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = NormalizeStorageCounterIdentity(value);
        var buffer = new char[normalized.Length];
        for (var i = 0; i < normalized.Length; i++)
            buffer[i] = char.IsLetterOrDigit(normalized[i]) || normalized[i] == '-' || normalized[i] == '_' ? normalized[i] : '-';

        var collapsed = new string(buffer);
        while (collapsed.Contains("--", StringComparison.Ordinal))
            collapsed = collapsed.Replace("--", "-", StringComparison.Ordinal);
        return collapsed.Trim('-');
    }

    public static bool ContainsIdentityToken(string haystack, string token)
    {
        if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(token))
            return false;

        var start = 0;
        while (true)
        {
            var idx = haystack.IndexOf(token, start, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return false;

            var beforeOk = idx == 0 || !char.IsLetterOrDigit(haystack[idx - 1]);
            var afterPos = idx + token.Length;
            var afterOk = afterPos >= haystack.Length || !char.IsLetterOrDigit(haystack[afterPos]);
            if (beforeOk && afterOk)
                return true;

            start = idx + 1;
        }
    }

    public static VirtualDiskStats? ResolveDiskStats(Dictionary<string, VirtualDiskStats> counters, string? path, string? diskName)
    {
        var pathKey = NormalizeStorageCounterIdentity(path);
        if (!string.IsNullOrWhiteSpace(pathKey) && counters.TryGetValue(pathKey, out var byPath))
            return byPath;

        var nameKey = NormalizeDiskCounterKey(diskName);
        if (!string.IsNullOrWhiteSpace(nameKey))
        {
            if (counters.TryGetValue(nameKey, out var byName))
                return byName;

            foreach (var pair in counters)
            {
                if (pair.Key.EndsWith(nameKey, StringComparison.OrdinalIgnoreCase))
                    return pair.Value;
            }
        }

        return null;
    }

    public static VirtualNetworkStats? ResolveNetworkStats(Dictionary<string, VirtualNetworkStats> counters, string vmName, string adapterName, bool singleAdapter)
    {
        var vmKey = NormalizeVmIdentity(vmName);
        var adapterKey = NormalizeVmIdentity(adapterName);
        if (string.IsNullOrWhiteSpace(vmKey))
            return null;

        var matches = counters
            .Where(pair => ContainsIdentityToken(pair.Key, vmKey))
            .ToArray();
        if (matches.Length == 0)
            return null;

        var exact = matches
            .Where(pair => !string.IsNullOrWhiteSpace(adapterKey) && ContainsIdentityToken(pair.Key, adapterKey))
            .ToArray();
        if (exact.Length > 0)
            matches = exact;
        else if (!singleAdapter)
            return null;

        return new VirtualNetworkStats(
            matches.Sum(pair => pair.Value.RxMbps),
            matches.Sum(pair => pair.Value.TxMbps));
    }
}

