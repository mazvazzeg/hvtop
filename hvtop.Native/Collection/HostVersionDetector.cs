namespace hvtop.Native;

internal static class HostVersionDetector
{
    private static string? cached;

    public static string Detect()
    {
        if (!string.IsNullOrWhiteSpace(cached))
            return cached;

        cached = DetectCore();
        return cached;
    }

    private static string DetectCore()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var productName = key?.GetValue("ProductName")?.ToString() ?? string.Empty;
            var build = key?.GetValue("CurrentBuildNumber")?.ToString() ?? string.Empty;
            var ubr = key?.GetValue("UBR")?.ToString() ?? string.Empty;
            var displayVersion = key?.GetValue("DisplayVersion")?.ToString() ?? key?.GetValue("ReleaseId")?.ToString() ?? string.Empty;
            var release = NormalizeProductName(productName, build);
            if (string.IsNullOrWhiteSpace(release))
                release = "WIN";

            var fullBuild = string.IsNullOrWhiteSpace(ubr) ? build : $"{build}.{ubr}";
            if (string.IsNullOrWhiteSpace(fullBuild))
                return release;

            return string.IsNullOrWhiteSpace(displayVersion)
                ? $"{release} ({fullBuild})"
                : $"{release} ({displayVersion}/{fullBuild})";
        }
        catch
        {
            var version = Environment.OSVersion.Version;
            return version.Build > 0 ? $"WIN.{version.Build}" : "n/a";
        }
    }

    private static string NormalizeProductName(string productName, string build)
    {
        var value = productName.Trim();
        var isStandaloneHyperV = value.Contains("Hyper-V Server", StringComparison.OrdinalIgnoreCase);
        var isServer = value.Contains("Server", StringComparison.OrdinalIgnoreCase);
        var serverPrefix = isStandaloneHyperV ? "HVS" : isServer ? "SRV" : string.Empty;

        if (isServer)
        {
            if (value.Contains("2012 R2", StringComparison.OrdinalIgnoreCase)) return $"{serverPrefix}2012R2";
            if (value.Contains("2008 R2", StringComparison.OrdinalIgnoreCase)) return $"{serverPrefix}2008R2";

            foreach (var year in new[] { "2025", "2022", "2019", "2016", "2012", "2008" })
            {
                if (value.Contains(year, StringComparison.OrdinalIgnoreCase))
                    return $"{serverPrefix}{year}";
            }
        }

        if (value.Contains("Windows 11", StringComparison.OrdinalIgnoreCase)
            || (serverPrefix.Length == 0 && int.TryParse(build, out var buildNumber) && buildNumber >= 22000))
            return "WIN11";
        if (value.Contains("Windows 10", StringComparison.OrdinalIgnoreCase)) return "WIN10";
        if (value.Contains("Windows 8.1", StringComparison.OrdinalIgnoreCase)) return "WIN8.1";
        if (value.Contains("Windows 8", StringComparison.OrdinalIgnoreCase)) return "WIN8";
        return value.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase)
            ? value["Microsoft ".Length..].Trim()
            : value;
    }
}

