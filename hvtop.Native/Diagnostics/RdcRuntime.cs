namespace hvtop.Native;

internal sealed record RdcStatus(string Status, string Host);

[JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(Snapshot))]
[JsonSerializable(typeof(RdcStatus))]
internal sealed partial class HvtopJsonContext : JsonSerializerContext;

internal static class RdcLog
{
    private static readonly object Gate = new();
    private static string path = System.IO.Path.Combine(AppContext.BaseDirectory, "hvtop-rdc.log");
    private static bool enabled;

    public static void Configure(bool debugLog, string fileName)
    {
        lock (Gate)
        {
            enabled = debugLog;
            path = System.IO.Path.Combine(AppContext.BaseDirectory, fileName);
        }
    }

    public static void Info(string message)
    {
        if (!enabled)
            return;

        try
        {
            lock (Gate)
            {
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }

    public static string SafeArgs(string[] args)
    {
        var copy = args.ToArray();
        for (var i = 0; i < copy.Length; i++)
        {
            if (copy[i].Equals("--token", StringComparison.OrdinalIgnoreCase) && i + 1 < copy.Length)
                copy[i + 1] = "<redacted>";
        }

        return string.Join(" ", copy);
    }
}

internal sealed class RdcFirewallRule : IDisposable
{
    private readonly string ruleName;
    private readonly bool shouldRemove;

    private RdcFirewallRule(string ruleName, string status, bool shouldRemove)
    {
        this.ruleName = ruleName;
        Status = status;
        this.shouldRemove = shouldRemove;
    }

    public string Status { get; }

    public static RdcFirewallRule Ensure(int port)
    {
        var ruleName = $"hvtop-rdc TCP {port}";
        try
        {
            RdcLog.Info($"firewall check rule='{ruleName}' port={port}");
            if (!FirewallEnabled())
            {
                RdcLog.Info("firewall disabled, no rule needed");
                return new RdcFirewallRule(ruleName, "disabled", false);
            }

            RdcLog.Info("firewall enabled, adding temporary inbound port-only allow rule");
            RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");
            var ok = RunNetsh($"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=TCP localport={port} enable=yes profile=any");
            RdcLog.Info($"firewall add rule result={(ok ? "ok" : "failed")}");
            return new RdcFirewallRule(ruleName, ok ? "allow-rule-added" : "allow-rule-failed", ok);
        }
        catch (Exception ex)
        {
            RdcLog.Info($"firewall rule setup failed: {ex}");
            return new RdcFirewallRule(ruleName, "allow-rule-failed", false);
        }
    }

    public void Dispose()
    {
        if (!shouldRemove)
            return;

        try
        {
            RdcLog.Info($"firewall removing temporary rule='{ruleName}'");
            RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");
        }
        catch (Exception ex)
        {
            RdcLog.Info($"firewall rule removal failed: {ex.Message}");
        }
    }

    private static bool FirewallEnabled()
    {
        var output = RunNetshCapture("advfirewall show allprofiles state");
        RdcLog.Info($"firewall state output='{output.ReplaceLineEndings(" ").Trim()}'");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Contains("State", StringComparison.OrdinalIgnoreCase)
                         && line.Contains("ON", StringComparison.OrdinalIgnoreCase));
    }

    private static bool RunNetsh(string arguments)
    {
        using var process = CreateNetsh(arguments, redirectOutput: false);
        process.Start();
        return process.WaitForExit(5000) && process.ExitCode == 0;
    }

    private static string RunNetshCapture(string arguments)
    {
        using var process = CreateNetsh(arguments, redirectOutput: true);
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        return output;
    }

    private static Process CreateNetsh(string arguments, bool redirectOutput)
    {
        return new Process
        {
            StartInfo =
            {
                FileName = "netsh.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = redirectOutput,
                RedirectStandardError = redirectOutput,
                CreateNoWindow = true
            }
        };
    }
}

