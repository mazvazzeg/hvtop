#if RDC
using System.ServiceProcess;

namespace hvtop.Native;

internal sealed class RdcWindowsService : ServiceBase
{
    public const string Name = "hvtop-rdc";
    private readonly string[] baseArgs;
    private CancellationTokenSource? cts;
    private Task? worker;

    public RdcWindowsService(string[] args)
    {
        baseArgs = args;
        ServiceName = Name;
        CanStop = true;
        CanShutdown = true;
    }

    protected override void OnStart(string[] args)
    {
        var mergedArgs = MergeArgs(baseArgs, args);
        var options = RdcOptions.Parse(mergedArgs);
        if (options.ParseError is not null)
            throw new InvalidOperationException(options.ParseError);

        RdcLog.Configure(options.DebugLog, "hvtop-rdc.log");
        DebugCounterLog.Configure(options.DebugCounters, "hvtop-debug.log");
        RdcLog.Info($"{Program.AppName} {Program.DisplayVersion} service starting args='{RdcLog.SafeArgs(mergedArgs)}'");
        cts = new CancellationTokenSource();
        worker = Task.Run(() => Program.RunRdcAsync(options, cts.Token, writeConsole: false, manageFirewallRule: false));
    }

    protected override void OnStop()
    {
        RdcLog.Info("service stop requested");
        try { cts?.Cancel(); } catch { }
        try { worker?.Wait(TimeSpan.FromSeconds(15)); } catch { }
        RdcLog.Info("service stopped");
    }

    protected override void OnShutdown() => OnStop();

    private static string[] MergeArgs(string[] original, string[] startup)
        => original
            .Where(arg => !IsServiceControlArg(arg))
            .Concat(startup ?? [])
            .ToArray();

    private static bool IsServiceControlArg(string arg)
        => arg.Equals("--service", StringComparison.OrdinalIgnoreCase)
           || arg.Equals("--install-service", StringComparison.OrdinalIgnoreCase)
           || arg.Equals("--uninstall-service", StringComparison.OrdinalIgnoreCase);
}

internal static class RdcServiceInstaller
{
    private const string DisplayName = "hvtop Remote Data Collector";

    public static int Install(RdcOptions options, string[] originalArgs)
    {
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unable to determine current executable path.");
            var binPath = $"{Quote(exe)} {BuildServiceArgs(options)}";
            var exists = ScOk("query", RdcWindowsService.Name);
            var verb = exists ? "config" : "create";
            var result = exists
                ? RunSc("config", RdcWindowsService.Name, "binPath=", binPath, "start=", "auto", "DisplayName=", DisplayName)
                : RunSc("create", RdcWindowsService.Name, "binPath=", binPath, "start=", "auto", "DisplayName=", DisplayName);
            if (result.ExitCode != 0)
            {
                Console.Error.WriteLine($"Failed to {verb} {RdcWindowsService.Name}: {result.Output}");
                return 1;
            }

            RunSc("description", RdcWindowsService.Name, "Remote telemetry collector for hvtop.");
            RdcFirewallRule.EnsurePermanent(options.Port);
            var start = RunSc("start", RdcWindowsService.Name);
            if (start.ExitCode != 0 && !start.Output.Contains("1056", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"Failed to start {RdcWindowsService.Name}: {start.Output}");
                return 1;
            }

            Console.WriteLine($"{RdcWindowsService.Name} service {(exists ? "updated" : "installed")} on TCP/{options.Port}.");
            Console.WriteLine(start.ExitCode == 0
                ? "Service started."
                : "Service was already running.");
            Console.WriteLine($"Service command: {binPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to install {RdcWindowsService.Name}: {ex.Message}");
            return 1;
        }
    }

    public static int Uninstall(RdcOptions options)
    {
        try
        {
            if (!ScOk("query", RdcWindowsService.Name))
            {
                Console.WriteLine($"{RdcWindowsService.Name} service is not installed.");
                RdcFirewallRule.RemovePermanent(options.Port);
                return 0;
            }

            RunSc("stop", RdcWindowsService.Name);
            var result = RunSc("delete", RdcWindowsService.Name);
            if (result.ExitCode != 0)
            {
                Console.Error.WriteLine($"Failed to delete {RdcWindowsService.Name}: {result.Output}");
                return 1;
            }

            RdcFirewallRule.RemovePermanent(options.Port);
            Console.WriteLine($"{RdcWindowsService.Name} service removed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to uninstall {RdcWindowsService.Name}: {ex.Message}");
            return 1;
        }
    }

    private static string BuildServiceArgs(RdcOptions options)
    {
        var args = new List<string>
        {
            "--service",
            "--port", options.Port.ToString(CultureInfo.InvariantCulture),
            "--refresh", options.Refresh.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "--history", options.History.TotalMinutes.ToString("0.###", CultureInfo.InvariantCulture),
            "--listen", options.ListenPrefix
        };
        if (!string.IsNullOrWhiteSpace(options.Token))
            args.AddRange(["--token", options.Token]);
        if (options.DebugLog)
            args.Add("--debug-log");
        if (options.DebugCounters)
            args.Add("--debug-counters");
        return string.Join(" ", args.Select(QuoteArg));
    }

    private static bool ScOk(params string[] args) => RunSc(args).ExitCode == 0;

    private static (int ExitCode, string Output) RunSc(params string[] args)
    {
        using var process = new Process
        {
            StartInfo =
            {
                FileName = "sc.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(10000);
        return (process.ExitCode, output.Trim());
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static string QuoteArg(string value)
        => value.Any(char.IsWhiteSpace) || value.Contains('"')
            ? Quote(value)
            : value;
}
#endif
