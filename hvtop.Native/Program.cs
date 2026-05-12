using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace hvtop.Native;

internal static class Program
{
#if RDC
    public const string DisplayVersion = "0.5.2-rdc+20260511.0001";
    public const string AppName = "hvtop-rdc";

    public static async Task<int> Main(string[] args)
    {
        ConfigureConsoleEncoding();
        var options = RdcOptions.Parse(args);
        if (options.ShowVersion)
        {
            Console.WriteLine($"{AppName} {DisplayVersion}");
            return 0;
        }
        if (options.ShowHelp || options.ParseError is not null)
        {
            if (options.ParseError is not null)
                Console.Error.WriteLine(options.ParseError);
            Console.WriteLine(RdcOptions.HelpText);
            return options.ParseError is null ? 0 : 2;
        }
        RdcLog.Configure(options.DebugLog, "hvtop-rdc.log");
        RdcLog.Info($"{AppName} {DisplayVersion} starting base='{AppContext.BaseDirectory}' process='{Environment.ProcessPath}' args='{RdcLog.SafeArgs(args)}'");
        try
        {
            RdcLog.Info($"parsed options listen='{options.ListenPrefix}' port={options.Port} refresh={options.Refresh.TotalSeconds:N1}s history={options.History.TotalMinutes:N0}m token={(string.IsNullOrWhiteSpace(options.Token) ? "none" : "set")}");
            using var cts = new CancellationTokenSource();
            using var firewallRule = RdcFirewallRule.Ensure(options.Port);
            using var collector = new Collector(new Options(options.Refresh, options.History, false, false, options.Port, options.Refresh, options.DebugLog, false, false, null));
            var current = Snapshot.Empty;
            var firstSample = true;
            var sampler = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var started = Stopwatch.GetTimestamp();
                        RdcLog.Info($"sample start first={firstSample}");
                        current = collector.Collect(firstSample);
                        firstSample = false;
                        RdcLog.Info($"sample complete in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:N0} ms hosts={current.Hosts.Length} vms={current.Vms.Length} disks={current.Disks.Length} networks={current.Networks.Length} switches={current.NetworkSwitches.Length}");
                    }
                    catch (Exception ex)
                    {
                        RdcLog.Info($"sample failed: {ex}");
                    }

                    try
                    {
                        await Task.Delay(options.Refresh, cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            });

            using var listener = new HttpListener();
            listener.Prefixes.Add(options.ListenPrefix);
            RdcLog.Info($"listener starting prefix='{options.ListenPrefix}' firewall={firewallRule.Status}");
            listener.Start();
            using var listenerStop = cts.Token.Register(() =>
            {
                RdcLog.Info("stop signal received, stopping listener");
                try { listener.Stop(); } catch (Exception ex) { RdcLog.Info($"listener stop failed: {ex.Message}"); }
            });
            RdcLog.Info("listener started");
            Console.WriteLine($"{AppName} {DisplayVersion} listening on {options.ListenPrefix} firewall={firewallRule.Status}");

            while (!cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch when (cts.IsCancellationRequested)
                {
                    break;
                }

                _ = Task.Run(() => HandleRdcRequest(context, options, () => current, cts));
            }

            try { listener.Stop(); } catch { }
            try { await Task.WhenAny(sampler, Task.Delay(1000)).ConfigureAwait(false); } catch { }
            RdcLog.Info("stopped");
            return 0;
        }
        catch (Exception ex)
        {
            RdcLog.Info($"FATAL: {ex}");
            return 1;
        }
    }

    private static async Task HandleRdcRequest(HttpListenerContext context, RdcOptions options, Func<Snapshot> readSnapshot, CancellationTokenSource cts)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            RdcLog.Info($"request {context.Request.HttpMethod} {path} from {context.Request.RemoteEndPoint}");
            if (!RdcAuthorized(context.Request, options.Token))
            {
                RdcLog.Info($"request unauthorized {path} from {context.Request.RemoteEndPoint}");
                context.Response.StatusCode = 403;
                context.Response.Close();
                return;
            }

            if (path.Equals("/stop", StringComparison.OrdinalIgnoreCase))
            {
                RdcLog.Info("stop requested");
                await WriteJson(context.Response, new RdcStatus("stopping", Environment.MachineName)).ConfigureAwait(false);
                cts.Cancel();
                return;
            }

            if (path.Equals("/health", StringComparison.OrdinalIgnoreCase))
            {
                RdcLog.Info("health requested");
                await WriteJson(context.Response, new RdcStatus("ok", Environment.MachineName)).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/snapshot", StringComparison.OrdinalIgnoreCase) || path.Equals("/", StringComparison.OrdinalIgnoreCase))
            {
                var snapshot = readSnapshot();
                RdcLog.Info($"snapshot requested loading={snapshot.Loading} at={snapshot.At:HH:mm:ss} hosts={snapshot.Hosts.Length} vms={snapshot.Vms.Length} events={snapshot.Events.Length}");
                await WriteJson(context.Response, snapshot).ConfigureAwait(false);
                return;
            }

            RdcLog.Info($"request not found {path}");
            context.Response.StatusCode = 404;
            context.Response.Close();
        }
        catch (Exception ex)
        {
            RdcLog.Info($"request failed: {ex}");
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch
            {
            }
        }
    }

    private static bool RdcAuthorized(HttpListenerRequest request, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return true;

        return string.Equals(request.QueryString["token"], token, StringComparison.Ordinal)
               || string.Equals(request.Headers["X-Hvtop-Token"], token, StringComparison.Ordinal);
    }

    private static Task WriteJson(HttpListenerResponse response, Snapshot value)
        => WriteJsonBytes(response, JsonSerializer.SerializeToUtf8Bytes(value, HvtopJsonContext.Default.Snapshot));

    private static Task WriteJson(HttpListenerResponse response, RdcStatus value)
        => WriteJsonBytes(response, JsonSerializer.SerializeToUtf8Bytes(value, HvtopJsonContext.Default.RdcStatus));

    private static async Task WriteJsonBytes(HttpListenerResponse response, byte[] bytes)
    {
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

#else
    public const string DisplayVersion = "0.5.2+20260511.0001";
    public const string AppName = "hvtop";

    public static async Task<int> Main(string[] args)
    {
        ConfigureConsoleEncoding();
        var options = Options.Parse(args);
        if (options.ShowVersion)
        {
            Console.WriteLine($"{AppName} {DisplayVersion}");
            return 0;
        }
        if (options.ShowHelp || options.ParseError is not null)
        {
            if (options.ParseError is not null)
                Console.Error.WriteLine(options.ParseError);
            Console.WriteLine(Options.HelpText);
            return options.ParseError is null ? 0 : 2;
        }
        RdcLog.Configure(options.DebugLog, "hvtop.log");
        RdcLog.Info($"{AppName} {DisplayVersion} starting base='{AppContext.BaseDirectory}' process='{Environment.ProcessPath}' args='{RdcLog.SafeArgs(args)}'");
        using var cts = new CancellationTokenSource();
        using var collector = new Collector(options);

        if (options.Smoke)
        {
            var snapshot = collector.Collect();
            Console.WriteLine($"{AppName} {DisplayVersion} smoke sample at {snapshot.At:yyyy-MM-dd HH:mm:ss}");
            foreach (var cluster in snapshot.Clusters)
                Console.WriteLine($"CLUSTER {cluster.Name} NODES {cluster.NodeCount} UP {cluster.UpNodeCount} OWNER {cluster.OwnerNode} QUORUM {cluster.Quorum} STA {cluster.Status}");
            foreach (var host in snapshot.Hosts)
                Console.WriteLine($"HOST {host.Name} VER {host.Version} CPU {FormatSmoke(host.Cpu)} | {FormatSmokeMax(host.Cpu)} | ({host.CpuCapacity}) MEM {FormatSmoke(host.Mem)} | {FormatSmokeMax(host.Mem)} | ({host.MemCapacity}) IO {FormatSmoke(host.Io)} NET {FormatSmoke(host.Net)} STA {host.Status}");
            foreach (var disk in snapshot.Disks.Take(5))
                Console.WriteLine($"DISK {disk.HostName} {disk.Name} SIZE {disk.Size} FREE {FormatSmoke(disk.Free)} IO {FormatSmoke(disk.Io)} IOPS {FormatSmoke(disk.Iops)} QD {FormatSmoke(disk.QueueDepth)} LAT {FormatSmoke(disk.Latency)} STA {disk.Status}");
            foreach (var net in snapshot.Networks.Take(5))
                Console.WriteLine($"NET  {net.HostName} {net.Name} THR {FormatSmoke(net.Throughput)} RX {FormatSmoke(net.Rx)} TX {FormatSmoke(net.Tx)} STA {net.Status}");
            return 0;
        }

        var state = new AppState(options.Refresh);
        var sampler = Task.Run(() => RunSamplerAsync(collector, state, options, cts.Token));

        try
        {
            var ui = new Tui(state, options);
            ui.Run(cts);
        }
        finally
        {
            cts.Cancel();
            try { await Task.WhenAny(sampler, Task.Delay(1500)).ConfigureAwait(false); } catch (OperationCanceledException) { }
            RdcLog.Info("hvtop stopped");
        }

        return 0;
    }

    private static string FormatSmoke(Metric metric)
    {
        if (double.IsNaN(metric.Current)) return "n/a";
        return metric.Unit switch
        {
            Unit.Percent => $"{metric.Current,3:N0}%",
            Unit.Mbps => FormatRate(metric.Current),
            Unit.Iops => FormatCompact(metric.Current, suffix: string.Empty, kiloSuffix: "k"),
            Unit.Milliseconds => $"{FormatNumber4(metric.Current)} ms",
            _ => FormatNumber4(metric.Current)
        };
    }

    private static string FormatSmokeMax(Metric metric) => FormatSmoke(metric with { Current = metric.Max });

    private static string FormatRate(double megabytesPerSecond)
    {
        var kb = megabytesPerSecond * 1024;
        if (Math.Abs(kb) < 1000)
            return $"{FormatNumber4(kb)} KB/s";

        if (Math.Abs(megabytesPerSecond) < 1000)
            return $"{FormatNumber4(megabytesPerSecond)} MB/s";

        return $"{FormatNumber4(megabytesPerSecond / 1024)} GB/s";
    }

    private static string FormatCompact(double value, string suffix, string kiloSuffix)
    {
        if (Math.Abs(value) >= 1000)
            return $"{FormatNumber4(value / 1000)}{kiloSuffix}";
        return $"{FormatNumber4(value)}{suffix}";
    }

    private static string FormatNumber4(double value)
    {
        var abs = Math.Abs(value);
        string text;
        if (abs >= 100) text = value.ToString("0");
        else if (abs >= 10) text = value.ToString("0.0");
        else text = value.ToString("0.00");
        return text.Length > 4 ? text[..4] : text.PadLeft(4);
    }
#endif

    private static void ConfigureConsoleEncoding()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
        }
        catch
        {
        }
    }

    private static async Task RunSamplerAsync(Collector collector, AppState state, Options options, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                var snapshot = collector.Collect(state.ConsumeRefreshRequest());
                state.Publish(snapshot);
            }
            catch (Exception ex)
            {
                state.AddEvent("ERR", $"Collector failed: {ex.Message}");
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            var delay = TimeSpan.FromMilliseconds(Math.Max(50, state.Refresh.TotalMilliseconds - elapsed.TotalMilliseconds));
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
    }
}

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

internal sealed record RdcOptions(TimeSpan Refresh, TimeSpan History, int Port, string ListenPrefix, string Token, bool DebugLog, bool ShowHelp, bool ShowVersion, string? ParseError)
{
    public static string HelpText =>
        """
        hvtop-rdc remote data collector

        Usage:
          hvtop-rdc.exe [options]

        Options:
          --port <n>            Listen TCP port. Default: 54321
          --listen <prefix>     HTTP listener prefix. Default: http://+:<port>/
          --refresh <seconds>   Collection interval. Default: 1
          --history <minutes>   History window. Default: 15
          --token <value>       Required token for incoming requests.
          --debug-log           Write hvtop-rdc.log beside the executable.
          --help                Show this help.
          --version             Show version and exit.
        """;

    public static RdcOptions Parse(string[] args)
    {
        var refresh = TimeSpan.FromSeconds(1);
        var history = TimeSpan.FromMinutes(15);
        var port = 54321;
        string? listen = null;
        var token = string.Empty;
        var debugLog = false;
        var showHelp = false;
        var showVersion = false;
        string? error = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i].Trim();
            if (ArgumentHelper.IsHelp(arg))
            {
                showHelp = true;
                break;
            }
            if (ArgumentHelper.IsVersion(arg))
            {
                showVersion = true;
                break;
            }
            if (arg.Equals("--refresh", StringComparison.OrdinalIgnoreCase))
            {
                if (!ArgumentHelper.TryReadDouble(args, ref i, arg, out var value, out error)) break;
                refresh = TimeSpan.FromSeconds(Math.Max(1, value));
            }
            else if (arg.Equals("--history", StringComparison.OrdinalIgnoreCase))
            {
                if (!ArgumentHelper.TryReadDouble(args, ref i, arg, out var value, out error)) break;
                history = TimeSpan.FromMinutes(Math.Max(1, value));
            }
            else if (arg.Equals("--port", StringComparison.OrdinalIgnoreCase))
            {
                if (!ArgumentHelper.TryReadInt(args, ref i, arg, out var value, out error)) break;
                port = Math.Clamp(value, 1, 65535);
            }
            else if (arg.Equals("--listen", StringComparison.OrdinalIgnoreCase))
            {
                if (!ArgumentHelper.TryReadString(args, ref i, arg, out var value, out error)) break;
                listen = value;
            }
            else if (arg.Equals("--token", StringComparison.OrdinalIgnoreCase))
            {
                if (!ArgumentHelper.TryReadString(args, ref i, arg, out var value, out error)) break;
                token = value;
            }
            else if (arg.Equals("--debug-log", StringComparison.OrdinalIgnoreCase))
                debugLog = true;
            else
            {
                error = $"Unknown option: {arg}";
                break;
            }
        }

        listen ??= $"http://+:{port}/";
        if (!listen.EndsWith("/", StringComparison.Ordinal))
            listen += "/";

        return new RdcOptions(refresh, history, port, listen, token, debugLog, showHelp, showVersion, error);
    }
}

internal sealed record Options(TimeSpan Refresh, TimeSpan History, bool Smoke, bool RemoteCollectors, int RdcPort, TimeSpan RemoteRefresh, bool DebugLog, bool ShowHelp, bool ShowVersion, string? ParseError)
{
    public static string HelpText =>
        """
        hvtop - Windows / Hyper-V / Failover Cluster TUI monitor

        Usage:
          hvtop.exe [options]

        Options:
          --refresh <seconds>        Local UI/data refresh interval. Default: 1
          --history <minutes>        History window for max/min values. Default: 15
          --rdc-port <n>             Remote Data Collector TCP port. Default: 54321
          --rdc-refresh <seconds>    Remote Data Collector interval. Default: 1
          --rdc-disable              Disable remote data collection on cluster peers.
          --debug-log                Write hvtop.log; also enables remote hvtop-rdc.log.
          --smoke                    Print one sample and exit.
          --help                     Show this help.
          --version                  Show version and exit.
        """;

    public static Options Parse(string[] args)
    {
        var refresh = TimeSpan.FromSeconds(1);
        var history = TimeSpan.FromMinutes(15);
        var smoke = false;
        var remoteCollectors = true;
        var rdcPort = 54321;
        var remoteRefresh = TimeSpan.FromSeconds(1);
        var debugLog = false;
        var showHelp = false;
        var showVersion = false;
        string? error = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i].Trim();
            if (ArgumentHelper.IsHelp(arg))
            {
                showHelp = true;
                break;
            }
            if (ArgumentHelper.IsVersion(arg))
            {
                showVersion = true;
                break;
            }
            if (arg.Equals("--refresh", StringComparison.OrdinalIgnoreCase))
            {
                if (!ArgumentHelper.TryReadDouble(args, ref i, arg, out var value, out error)) break;
                refresh = TimeSpan.FromSeconds(Math.Max(0.2, value));
            }
            else if (arg.Equals("--history", StringComparison.OrdinalIgnoreCase))
            {
                if (!ArgumentHelper.TryReadDouble(args, ref i, arg, out var value, out error)) break;
                history = TimeSpan.FromMinutes(Math.Max(1, value));
            }
            else if (arg.Equals("--smoke", StringComparison.OrdinalIgnoreCase))
                smoke = true;
            else if (arg.Equals("--rdc-disable", StringComparison.OrdinalIgnoreCase))
                remoteCollectors = false;
            else if (arg.Equals("--rdc-port", StringComparison.OrdinalIgnoreCase))
            {
                if (!ArgumentHelper.TryReadInt(args, ref i, arg, out var value, out error)) break;
                rdcPort = Math.Clamp(value, 1, 65535);
            }
            else if (arg.Equals("--rdc-refresh", StringComparison.OrdinalIgnoreCase))
            {
                if (!ArgumentHelper.TryReadDouble(args, ref i, arg, out var value, out error)) break;
                remoteRefresh = TimeSpan.FromSeconds(Math.Max(1, value));
            }
            else if (arg.Equals("--debug-log", StringComparison.OrdinalIgnoreCase))
                debugLog = true;
            else
            {
                error = $"Unknown option: {arg}";
                break;
            }
        }

        if (smoke)
            remoteCollectors = false;

        return new Options(refresh, history, smoke, remoteCollectors, rdcPort, remoteRefresh, debugLog, showHelp, showVersion, error);
    }
}

internal static class ArgumentHelper
{
    public static bool IsHelp(string arg)
        => arg.Equals("--help", StringComparison.OrdinalIgnoreCase);

    public static bool IsVersion(string arg)
        => arg.Equals("--version", StringComparison.OrdinalIgnoreCase);

    public static bool TryReadString(string[] args, ref int index, string option, out string value, out string? error)
    {
        value = string.Empty;
        error = null;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal))
        {
            error = $"Missing value for {option}";
            return false;
        }

        value = args[++index];
        return true;
    }

    public static bool TryReadInt(string[] args, ref int index, string option, out int value, out string? error)
    {
        value = 0;
        if (!TryReadString(args, ref index, option, out var text, out error))
            return false;

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return true;

        error = $"Invalid integer for {option}: {text}";
        return false;
    }

    public static bool TryReadDouble(string[] args, ref int index, string option, out double value, out string? error)
    {
        value = 0;
        if (!TryReadString(args, ref index, option, out var text, out error))
            return false;

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            return true;

        error = $"Invalid number for {option}: {text}";
        return false;
    }
}

internal sealed class AppState
{
    private readonly object gate = new();
    private Snapshot snapshot = Snapshot.Empty;
    private bool refreshRequested = true;
    private TimeSpan refresh;

    public AppState(TimeSpan refresh)
    {
        this.refresh = refresh;
    }

    public TimeSpan Refresh
    {
        get
        {
            lock (gate)
                return refresh;
        }
    }

    public void Publish(Snapshot next)
    {
        lock (gate)
        {
            snapshot = next;
        }
    }

    public Snapshot Read()
    {
        lock (gate)
        {
            return snapshot;
        }
    }

    public void AddEvent(string severity, string message)
    {
        RdcLog.Info($"{severity} {message}");
        lock (gate)
        {
            snapshot = snapshot with
            {
                Events = snapshot.Events.Prepend(new EventRow(DateTime.Now, severity, message)).Take(200).ToArray()
            };
        }
    }

    public void RequestRefresh()
    {
        lock (gate)
            refreshRequested = true;
    }

    public TimeSpan CycleRefresh(bool reverse = false)
    {
        var steps = new[] { 0.5, 1.0, 2.0, 5.0, 10.0 };
        lock (gate)
        {
            var current = refresh.TotalSeconds;
            var index = Array.FindIndex(steps, value => Math.Abs(value - current) < 0.01);
            if (index < 0)
                index = steps.Select((value, i) => new { value, i }).OrderBy(item => Math.Abs(item.value - current)).First().i;

            index = reverse
                ? (index - 1 + steps.Length) % steps.Length
                : (index + 1) % steps.Length;
            refresh = TimeSpan.FromSeconds(steps[index]);
            return refresh;
        }
    }

    public bool ConsumeRefreshRequest()
    {
        lock (gate)
        {
            var requested = refreshRequested;
            refreshRequested = false;
            return requested;
        }
    }
}

internal sealed class Collector : IDisposable
{
    private readonly Options options;
    private readonly RollingHistory history;
    private readonly PdhQuery pdh = new();
    private readonly NetworkSampler network = new();
    private readonly HyperVVmSampler hyperV = new();
    private readonly HyperVInventory inventory = new();
    private readonly HyperVTopology topology = new();
    private readonly ClusterInventory clusterInventory = new();
    private readonly RemoteCollectorManager remote;
    private readonly Counter cpu;
    private readonly Counter memory;
    private readonly Counter diskBytes;
    private readonly Counter diskIops;
    private readonly Counter diskQueue;
    private readonly Counter diskLatencySeconds;
    private EventRow[] events = [new(DateTime.Now, "INFO", "hvtop native collector started")];
    private bool networkDiagnosticsLogged;
    private bool initialDiscoveryComplete;
    private bool discoveryBannerLogged;
    private bool hostsDiscoveryLogged;
    private bool vmsDiscoveryLogged;
    private bool storageDiscoveryLogged;
    private bool networkDiscoveryLogged;
    private bool discoveryCompleteLogged;
    private bool emptyVmInventoryLogged;

    public Collector(Options options)
    {
        this.options = options;
        history = new RollingHistory(options.History);
        remote = new RemoteCollectorManager(options);
        cpu = pdh.Add(@"\Processor(_Total)\% Processor Time");
        memory = pdh.Add(@"\Memory\% Committed Bytes In Use");
        diskBytes = pdh.Add(@"\LogicalDisk(_Total)\Disk Bytes/sec");
        diskIops = pdh.Add(@"\LogicalDisk(_Total)\Disk Transfers/sec");
        diskQueue = pdh.Add(@"\LogicalDisk(_Total)\Current Disk Queue Length");
        diskLatencySeconds = pdh.Add(@"\LogicalDisk(_Total)\Avg. Disk sec/Transfer");
        pdh.Collect();
        Thread.Sleep(250);
        if (!ElevationChecker.IsElevated())
            AddEvent("WARN", "hvtop is not running as Administrator. Hyper-V inventory and some counters may be unavailable.");
        AddDiscoveryBanner();
    }

    public Snapshot Collect(bool refreshRequested = false)
    {
        pdh.Collect();
        var hostCpu = cpu.Read();
        var hostMem = memory.Read();
        var hostIo = diskBytes.Read() / 1024 / 1024;
        var adapterRates = network.Sample();
        var visibleAdapterRates = adapterRates.Where(a => a.IsVisibleAdapter).ToArray();
        var hostNet = visibleAdapterRates.Sum(a => a.TotalBytesPerSecond) / 1024 / 1024;
        var iops = diskIops.Read();
        var queue = diskQueue.Read();
        var latency = diskLatencySeconds.Read() * 1000;
        var topologyRefreshRequested = false;
        if (refreshRequested)
        {
            inventory.RequestRefresh();
            clusterInventory.RequestRefresh();
            topologyRefreshRequested = true;
            networkDiagnosticsLogged = false;
        }

        var inventoryResult = inventory.TryRead();
        var clusterResult = clusterInventory.TryRead();
        var inventoryVms = inventoryResult.Vms;
        LogicalDiskSampler.Refresh();

        foreach (var evt in inventoryResult.Events)
            AddEvent(evt.Severity, evt.Message);
        if (!string.IsNullOrWhiteSpace(clusterResult.EventMessage))
            AddEvent(clusterResult.EventSeverity, clusterResult.EventMessage);

        var host = new HostRow(
            Environment.MachineName,
            HostVersionDetector.Detect(),
            TimeSpan.FromMilliseconds(Environment.TickCount64),
            Metric.Percent(hostCpu),
            $"{Native.GetActiveLogicalProcessorCount()} CPU",
            Metric.Percent(hostMem),
            CapacityFormatter.FormatConfigCapacity(Native.GetPhysicalMemoryBytes()),
            Metric.Mbps(hostIo),
            Metric.Mbps(hostNet),
            Status.From(hostCpu, hostMem, latency, queue));
        var hosts = BuildHosts(host, clusterResult.Nodes);
        remote.UpdateTargets(clusterResult.Nodes, host.Name);

        var disks = StorageInventory.Enumerate()
            .Select(storage =>
            {
                var free = storage.TotalBytes > 0 ? 100.0 * storage.FreeBytes / storage.TotalBytes : 0;
                var diskReadIo = LogicalDiskSampler.ReadMbps(storage.CounterKey);
                var diskWriteIo = LogicalDiskSampler.WriteMbps(storage.CounterKey);
                var diskIo = diskReadIo + diskWriteIo;
                var diskReadIopsValue = LogicalDiskSampler.ReadIops(storage.CounterKey);
                var diskWriteIopsValue = LogicalDiskSampler.WriteIops(storage.CounterKey);
                var diskIopsValue = diskReadIopsValue + diskWriteIopsValue;
                var diskQueueDepth = LogicalDiskSampler.QueueDepth(storage.CounterKey);
                var diskLatencyMs = LogicalDiskSampler.LatencyMs(storage.CounterKey);
                var row = new DiskRow(
                    host.Name,
                    storage.DisplayName,
                    CapacityFormatter.FormatCapacity(storage.TotalBytes),
                    CapacityFormatter.FormatCapacity(storage.TotalBytes - storage.FreeBytes),
                    CapacityFormatter.FormatCapacity(storage.FreeBytes),
                    Metric.Percent(free),
                    Metric.Mbps(diskIo),
                    Metric.Mbps(diskReadIo),
                    Metric.Mbps(diskWriteIo),
                    Metric.Iops(diskIopsValue),
                    Metric.Iops(diskReadIopsValue),
                    Metric.Iops(diskWriteIopsValue),
                    Metric.Plain(diskQueueDepth),
                    Metric.Milliseconds(diskLatencyMs),
                    Status.From(10, 100 - free, diskLatencyMs, diskQueueDepth));
                return row;
            })
            .ToArray();

        var adapters = visibleAdapterRates
            .Select(a =>
            {
                var row = new NetworkRow(
                    host.Name,
                    a.Name,
                    a.Description,
                    NetworkLinkFormatter.Format(a.LinkSpeedBitsPerSecond, a.IsUp),
                    a.IsUp,
                    a.LinkSpeedBitsPerSecond,
                    Metric.Mbps(a.TotalBytesPerSecond / 1024 / 1024),
                    Metric.Mbps(a.ReceivedBytesPerSecond / 1024 / 1024),
                    Metric.Mbps(a.SentBytesPerSecond / 1024 / 1024),
                    Metric.Plain(a.DropsPerSecond),
                    Status.FromNetwork(a.TotalBytesPerSecond, a.LinkSpeedBitsPerSecond, a.IsUp),
                    a.PdhInstance,
                    a.RawReceivedBytesPerSecond,
                    a.RawSentBytesPerSecond,
                    a.PdhReceivedBytesPerSecond,
                    a.PdhSentBytesPerSecond);
                return row;
            })
            .ToArray();

        var vms = hyperV.Collect(history, host.Name, inventoryVms);
        if (vms.Length == 0 && !inventory.IsRefreshing && !emptyVmInventoryLogged)
        {
            emptyVmInventoryLogged = true;
            AddEvent("INFO", "No Hyper-V VMs detected; VM pane is empty on this host.");
        }

        var vmTopologyResult = topology.TryRead(disks, adapters);
        if (topologyRefreshRequested)
            topology.RequestRefresh();
        if (!string.IsNullOrWhiteSpace(vmTopologyResult.EventMessage))
            AddEvent(vmTopologyResult.EventSeverity, vmTopologyResult.EventMessage);

        var networkSwitches = topology.IsRefreshing && vmTopologyResult.Switches.Length == 0
            ? []
            : BuildNetworkSwitches(host.Name, adapters, vmTopologyResult.Switches);
        MaybeLogNetworkDiagnostics(refreshRequested, adapterRates, adapters, vmTopologyResult.Switches);
        var discovery = BuildDiscoveryProgress(host, vms, disks, adapters, networkSwitches);
        MaybeLogDiscoveryProgress(discovery, host, vms, disks, adapters, networkSwitches);
        var liveTopology = EnrichTopologyWithLiveStats(vmTopologyResult.Topology)
            .Select(t => t with { HostName = host.Name })
            .ToArray();
        if (liveTopology.Length > 0)
        {
            var mergedTopology = MergeVmTotalsIntoTopology(vms, liveTopology);
            mergedTopology = mergedTopology.Select(t => history.Apply("vmtopo:" + t.HostName + ":" + t.VmName, t)).ToArray();
            disks = ApplyVmDiskLoadToStorage(disks, mergedTopology);
            vms = ApplyTopologyFallback(vms, mergedTopology);
            host = host with
            {
                Io = Metric.Mbps(Math.Max(host.Io.Current, Math.Max(vms.Sum(v => v.Io.Current), disks.Sum(d => d.Io.Current))))
            };
            host = history.Apply("host:" + host.Name, host);
            hosts = BuildHosts(host, clusterResult.Nodes);
            disks = disks.Select(d => history.Apply("disk:" + d.HostName + ":" + d.Name, d)).ToArray();
            networkSwitches = networkSwitches.Select(n => history.Apply("vswitch:" + n.HostName + ":" + n.Name, n)).ToArray();
            adapters = adapters.Select(n => history.Apply("net:" + n.HostName + ":" + n.Name, n)).ToArray();
            MergeRemoteTelemetry(ref hosts, ref vms, ref disks, ref networkSwitches, ref adapters, ref mergedTopology);
            MaybeAddSpikeEvent(host, disks);
            return new Snapshot(DateTime.Now, clusterResult.Clusters, hosts, vms, disks, networkSwitches, adapters, events, mergedTopology, !initialDiscoveryComplete, inventory.IsRefreshing, topology.IsRefreshing, discovery);
        }

        host = history.Apply("host:" + host.Name, host);
        hosts = BuildHosts(host, clusterResult.Nodes);
        liveTopology = liveTopology.Select(t => history.Apply("vmtopo:" + t.HostName + ":" + t.VmName, t)).ToArray();
        disks = disks.Select(d => history.Apply("disk:" + d.HostName + ":" + d.Name, d)).ToArray();
        networkSwitches = networkSwitches.Select(n => history.Apply("vswitch:" + n.HostName + ":" + n.Name, n)).ToArray();
        adapters = adapters.Select(n => history.Apply("net:" + n.HostName + ":" + n.Name, n)).ToArray();
        MergeRemoteTelemetry(ref hosts, ref vms, ref disks, ref networkSwitches, ref adapters, ref liveTopology);
        MaybeAddSpikeEvent(host, disks);
        return new Snapshot(DateTime.Now, clusterResult.Clusters, hosts, vms, disks, networkSwitches, adapters, events, liveTopology, !initialDiscoveryComplete, inventory.IsRefreshing, topology.IsRefreshing, discovery);
    }

    private void MergeRemoteTelemetry(ref HostRow[] hosts, ref VmRow[] vms, ref DiskRow[] disks, ref NetworkSwitchRow[] networkSwitches, ref NetworkRow[] networks, ref VmTopologyRow[] topology)
    {
        foreach (var evt in remote.DrainEvents())
            AddEvent(evt.Severity, evt.Message);

        var snapshots = remote.ReadSnapshots();
        if (snapshots.Length == 0)
            return;

        hosts = RemoteCollectorManager.MergeHosts(hosts, snapshots);
        vms = RemoteCollectorManager.MergeVms(vms, snapshots);
        disks = RemoteCollectorManager.MergeDisks(disks, snapshots);
        networkSwitches = RemoteCollectorManager.MergeNetworkSwitches(networkSwitches, snapshots);
        networks = RemoteCollectorManager.MergeNetworks(networks, snapshots);
        topology = RemoteCollectorManager.MergeTopology(topology, snapshots);
    }

    private static HostRow[] BuildHosts(HostRow localHost, ClusterNodeRow[] clusterNodes)
    {
        if (clusterNodes.Length == 0)
            return [localHost];

        return clusterNodes
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .Select(node =>
            {
                if (node.Name.Equals(localHost.Name, StringComparison.OrdinalIgnoreCase)
                    || node.Name.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                    return localHost with { Status = MergeHostStatus(localHost.Status, node.Status) };

                return new HostRow(
                    node.Name,
                    "n/a",
                    null,
                    Metric.Percent(double.NaN),
                    "n/a CPU",
                    Metric.Percent(double.NaN),
                    "n/a",
                    Metric.Mbps(double.NaN),
                    Metric.Mbps(double.NaN),
                    node.Status);
            })
            .ToArray();
    }

    private static string MergeHostStatus(string metricStatus, string nodeStatus)
    {
        if (nodeStatus.Equals("HOT", StringComparison.OrdinalIgnoreCase)) return "HOT";
        if (nodeStatus.Equals("BUSY", StringComparison.OrdinalIgnoreCase) && metricStatus is "IDLE" or "OK") return "BUSY";
        return metricStatus;
    }

    private static NetworkSwitchRow[] BuildNetworkSwitches(string hostName, NetworkRow[] adapters, NetworkSwitchTopologyRow[] switches)
    {
        var hyperVSwitchRates = HyperVNetworkPdhSampler.ReadSwitchRates();
        var switchRows = switches.Length > 0
            ? switches
            : hyperVSwitchRates.Count > 0
                ? hyperVSwitchRates.Keys
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .Select(name => new NetworkSwitchTopologyRow(name, "Switch", [], string.Empty))
                    .ToArray()
            : adapters.Select(adapter => new NetworkSwitchTopologyRow(adapter.Name, "Adapter", [new NetworkUplinkInfo(adapter.Name, adapter.Description, adapter.Link, adapter.IsUp, adapter.LinkSpeedBitsPerSecond)], string.Empty)).ToArray();

        return switchRows
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(switchRow =>
            {
                var uplinks = switchRow.Uplinks
                    .Select(uplink => NetworkTopologyMatcher.MergeWithLive(adapters, uplink, hostName))
                    .Where(adapter => adapter is not null)
                    .Cast<NetworkRow>()
                    .DistinctBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var throughput = uplinks.Sum(a => a.Throughput.Current);
                var rx = uplinks.Sum(a => a.Rx.Current);
                var tx = uplinks.Sum(a => a.Tx.Current);
                if (hyperVSwitchRates.TryGetValue(switchRow.Name, out var switchRate))
                {
                    var switchRx = switchRate.ReceivedBytesPerSecond / 1024d / 1024d;
                    var switchTx = switchRate.SentBytesPerSecond / 1024d / 1024d;
                    rx = Math.Max(rx, switchRx);
                    tx = Math.Max(tx, switchTx);
                    throughput = Math.Max(throughput, switchRx + switchTx);
                }
                var drops = uplinks.Sum(a => a.Drops.Current);
                var linkSpeedBitsPerSecond = uplinks.Length > 0
                    ? uplinks.Where(a => a.IsUp).Sum(a => Math.Max(0L, a.LinkSpeedBitsPerSecond))
                    : switchRow.Uplinks.Where(u => u.IsUp).Sum(u => Math.Max(0L, u.LinkSpeedBitsPerSecond));
                var status = switchRow.Uplinks.Length == 0
                    ? "IDLE"
                    : uplinks.Length == 0 ? (switchRow.Uplinks.All(u => !u.IsUp) ? "OFF" : "OK")
                    : uplinks.All(a => !a.IsUp) ? "OFF"
                    : Status.FromNetwork(throughput * 1024d * 1024d, linkSpeedBitsPerSecond, true);

                return new NetworkSwitchRow(
                    hostName,
                    switchRow.Name,
                    switchRow.SwitchType,
                    switchRow.TeamMode,
                    switchRow.Uplinks,
                    SummarizeLink(switchRow.Uplinks, uplinks),
                    Metric.Mbps(throughput),
                    Metric.Mbps(rx),
                    Metric.Mbps(tx),
                    Metric.Plain(drops),
                    status);
            })
            .ToArray();
    }

    private static string SummarizeLink(NetworkUplinkInfo[] topologyUplinks, NetworkRow[] liveUplinks)
    {
        if (liveUplinks.Length > 0)
        {
            if (liveUplinks.All(a => !a.IsUp))
                return "DOWN";

            var upLinks = liveUplinks.Where(a => a.IsUp).ToArray();
            var distinct = upLinks.Select(a => a.Link).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (distinct.Length == 1)
                return upLinks.Length == 1 ? distinct[0] : $"{upLinks.Length}x{distinct[0]}";

            return "MIXED";
        }

        if (topologyUplinks.Length == 0 || topologyUplinks.All(a => !a.IsUp))
            return "DOWN";

        var topologyUp = topologyUplinks.Where(a => a.IsUp).ToArray();
        var distinctTopology = topologyUp.Select(a => a.Link).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (distinctTopology.Length == 1)
            return topologyUp.Length == 1 ? distinctTopology[0] : $"{topologyUp.Length}x{distinctTopology[0]}";

        return "MIXED";
    }

    private static VmRow[] ApplyTopologyFallback(VmRow[] vms, VmTopologyRow[] topology)
    {
        var topologyMap = topology.ToDictionary(t => $"{t.HostName}\0{t.VmName}", StringComparer.OrdinalIgnoreCase);
        return vms.Select(vm =>
        {
            if (!topologyMap.TryGetValue($"{vm.HostName}\0{vm.Name}", out var topo) || topo.Disks.Length == 0)
                return vm;

            var io = topo.Disks.Sum(d => d.ReadMbps + d.WriteMbps);
            var iops = topo.Disks.Sum(d => d.ReadIops + d.WriteIops);
            return vm with
            {
                Io = (vm.Io.Current <= 0 && io > 0) ? Metric.Mbps(io) with { Max = Math.Max(vm.Io.Max, io) } : vm.Io,
                Iops = (vm.Iops.Current <= 0 && iops > 0) ? Metric.Iops(iops) with { Max = Math.Max(vm.Iops.Max, iops) } : vm.Iops
            };
        }).ToArray();
    }

    private static VmTopologyRow[] MergeVmTotalsIntoTopology(VmRow[] vms, VmTopologyRow[] topology)
    {
        var vmMap = vms.ToDictionary(v => $"{v.HostName}\0{v.Name}", StringComparer.OrdinalIgnoreCase);
        return topology.Select(vm =>
        {
            if (!vmMap.TryGetValue($"{vm.HostName}\0{vm.VmName}", out var liveVm) || vm.Disks.Length == 0)
                return vm;

            var diskIo = vm.Disks.Sum(d => d.ReadMbps + d.WriteMbps);
            var diskIops = vm.Disks.Sum(d => d.ReadIops + d.WriteIops);
            if (diskIo > 0 || diskIops > 0)
                return vm;

            if (liveVm.Io.Current <= 0 && liveVm.Iops.Current <= 0)
                return vm;

            if (vm.Disks.Length == 1)
            {
                var only = vm.Disks[0];
                return vm with
                {
                    Disks =
                    [
                        only with
                        {
                            ReadMbps = liveVm.Io.Current * 0.25,
                            WriteMbps = liveVm.Io.Current * 0.75,
                            ReadIops = liveVm.Iops.Current * 0.25,
                            WriteIops = liveVm.Iops.Current * 0.75
                        }
                    ]
                };
            }

            var equalIo = liveVm.Io.Current / vm.Disks.Length;
            var equalIops = liveVm.Iops.Current / vm.Disks.Length;
            return vm with
            {
                Disks = vm.Disks.Select(d => d with
                {
                    ReadMbps = equalIo * 0.25,
                    WriteMbps = equalIo * 0.75,
                    ReadIops = equalIops * 0.25,
                    WriteIops = equalIops * 0.75
                }).ToArray()
            };
        }).ToArray();
    }

    private static VmTopologyRow[] EnrichTopologyWithLiveStats(VmTopologyRow[] topology)
    {
        if (topology.Length == 0) return topology;
        var liveDisks = VirtualDiskCounterSampler.Read();
        var liveNetworks = VirtualNetworkCounterSampler.Read();
        return topology.Select(vm => vm with
        {
            Disks = vm.Disks.Select(disk =>
            {
                var stats = HyperVNaming.ResolveDiskStats(liveDisks, disk.Path, disk.Name) ?? new VirtualDiskStats(0, 0, 0, 0);
                return disk with
                {
                    ReadMbps = stats.ReadMbps,
                    ReadIops = stats.ReadIops,
                    WriteMbps = stats.WriteMbps,
                    WriteIops = stats.WriteIops
                };
            }).ToArray(),
            Networks = vm.Networks.Select(adapter =>
            {
                var stats = HyperVNaming.ResolveNetworkStats(liveNetworks, vm.VmName, adapter.Name, vm.Networks.Length == 1) ?? new VirtualNetworkStats(0, 0);
                return adapter with
                {
                    RxMbps = stats.RxMbps,
                    TxMbps = stats.TxMbps
                };
            }).ToArray()
        }).ToArray();
    }

    private DiskRow[] ApplyVmDiskLoadToStorage(DiskRow[] disks, VmTopologyRow[] topology)
    {
        var byStorage = topology
            .SelectMany(vm => vm.Disks.Select(disk => new { vm.HostName, Disk = disk }))
            .GroupBy(d => $"{d.HostName}\0{d.Disk.StorageName}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    Io = g.Sum(d => d.Disk.ReadMbps + d.Disk.WriteMbps),
                    Iops = g.Sum(d => d.Disk.ReadIops + d.Disk.WriteIops)
                },
                StringComparer.OrdinalIgnoreCase);

        return disks.Select(disk =>
        {
            if (!byStorage.TryGetValue($"{disk.HostName}\0{disk.Name}", out var totals))
                return disk;

            var ioCurrent = Math.Max(disk.Io.Current, totals.Io);
            var ioMax = Math.Max(disk.Io.Max, ioCurrent);
            var iopsCurrent = Math.Max(disk.Iops.Current, totals.Iops);
            var iopsMax = Math.Max(disk.Iops.Max, iopsCurrent);
            return disk with
            {
                Io = Metric.Mbps(ioCurrent) with { Max = ioMax },
                Iops = Metric.Iops(iopsCurrent) with { Max = iopsMax }
            };
        }).ToArray();
    }

    private void MaybeAddSpikeEvent(HostRow host, DiskRow[] disks)
    {
        if (host.Cpu.Current >= 85)
            AddEvent("WARN", $"Host CPU hot at {host.Cpu.Current:N0}%");

        var hotDisk = disks.FirstOrDefault(d => d.Latency.Current >= 25);
        if (hotDisk is not null)
            AddEvent("WARN", $"{hotDisk.Name} latency hot at {hotDisk.Latency.Current:N1} ms");
    }

    private DiscoveryProgress BuildDiscoveryProgress(
        HostRow host,
        VmRow[] vms,
        DiskRow[] disks,
        NetworkRow[] adapters,
        NetworkSwitchRow[] networkSwitches)
    {
        var hostsReady = !string.IsNullOrWhiteSpace(host.Name);
        var vmsReady = !inventory.IsRefreshing;
        var storageReady = disks.Length > 0;
        var networkReady = !topology.IsRefreshing;
        var complete = hostsReady && vmsReady && storageReady && networkReady;

        if (complete)
            initialDiscoveryComplete = true;

        return new DiscoveryProgress(
            hostsReady,
            vmsReady,
            storageReady,
            networkReady,
            complete,
            vms.Length,
            disks.Length,
            adapters.Count(a => a.IsUp),
            networkSwitches.Length);
    }

    private void AddDiscoveryBanner()
    {
        if (discoveryBannerLogged)
            return;

        discoveryBannerLogged = true;
        AddEvent("INFO", "Please wait, discovering inventory and topology....");
        AddEvent("INFO", "Hosts...");
        AddEvent("INFO", "VMs...");
        AddEvent("INFO", "Storage...");
        AddEvent("INFO", "Network...");
    }

    private void MaybeLogDiscoveryProgress(
        DiscoveryProgress discovery,
        HostRow host,
        VmRow[] vms,
        DiskRow[] disks,
        NetworkRow[] adapters,
        NetworkSwitchRow[] networkSwitches)
    {
        if (discovery.HostsReady && !hostsDiscoveryLogged)
        {
            hostsDiscoveryLogged = true;
            AddEvent("INFO", $"Discovery Hosts: {host.Name}");
        }

        if (discovery.StorageReady && !storageDiscoveryLogged)
        {
            storageDiscoveryLogged = true;
            var storageNames = string.Join(", ", disks.Select(d => d.Name).Take(8));
            AddEvent("INFO", $"Discovery Storage: {disks.Length} target(s): {storageNames}");
        }

        if (discovery.VmsReady && !vmsDiscoveryLogged)
        {
            vmsDiscoveryLogged = true;
            AddEvent("INFO", $"Discovery VMs: {vms.Length} VM(s)");
        }

        if (discovery.NetworkReady && !networkDiscoveryLogged)
        {
            networkDiscoveryLogged = true;
            AddEvent("INFO", $"Discovery Network: {networkSwitches.Length} network target(s), {adapters.Length} adapter(s)");
        }

        if (discovery.Complete && !discoveryCompleteLogged)
        {
            discoveryCompleteLogged = true;
            AddEvent("INFO", "Discovery complete.");
        }
    }

    private void MaybeLogNetworkDiagnostics(bool refreshRequested, AdapterRate[] adapterRates, NetworkRow[] adapters, NetworkSwitchTopologyRow[] switches)
    {
        if (!refreshRequested && (networkDiagnosticsLogged || (switches.Length == 0 && adapterRates.Length == 0)))
            return;

        networkDiagnosticsLogged = true;
        var hardware = adapterRates.Where(a => a.IsVisibleAdapter).ToArray();
        var upHardware = hardware.Count(a => a.IsUp);
        var totalHardwareMbps = hardware.Sum(a => a.TotalBytesPerSecond) / 1024d / 1024d;
        var pdhRates = NetworkPdhSampler.LastRates;
        var pdhMatched = adapterRates.Count(a => !string.IsNullOrWhiteSpace(a.PdhInstance));
        AddEvent("INFO", $"NETDIAG live={adapterRates.Length} visible={hardware.Length} up={upHardware} sw={switches.Length} pdh={pdhRates.Length} matched={pdhMatched} throughput={totalHardwareMbps:0.00} MB/s");

        foreach (var pdh in pdhRates
                     .OrderByDescending(p => p.ReceivedBytesPerSecond + p.SentBytesPerSecond)
                     .Take(8))
        {
            AddEvent(
                "INFO",
                $"NETPDH inst='{TrimForEvent(pdh.Instance, 48)}' rx={pdh.ReceivedBytesPerSecond / 1024d / 1024d:0.00} tx={pdh.SentBytesPerSecond / 1024d / 1024d:0.00} MB/s");
        }

        foreach (var family in HyperVNetworkPdhSampler.Read())
        {
            var active = family.Rates.Count(r => r.TotalBytesPerSecond > 0 || r.ReceivedBytesPerSecond > 0 || r.SentBytesPerSecond > 0);
            AddEvent("INFO", $"NETVSW family='{family.Name}' instances={family.Rates.Length} active={active}");
            foreach (var rate in family.Rates.OrderByDescending(r => Math.Max(r.TotalBytesPerSecond, r.ReceivedBytesPerSecond + r.SentBytesPerSecond)).Take(6))
            {
                AddEvent(
                    "INFO",
                    $"NETVSW family='{family.Name}' inst='{TrimForEvent(rate.Instance, 44)}' total={rate.TotalBytesPerSecond / 1024d / 1024d:0.00} rx={rate.ReceivedBytesPerSecond / 1024d / 1024d:0.00} tx={rate.SentBytesPerSecond / 1024d / 1024d:0.00} MB/s");
            }
        }

        foreach (var adapter in adapterRates
                     .OrderByDescending(a => a.IsHardwareInterface)
                     .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                     .Take(10))
        {
            AddEvent(
                "INFO",
                $"NETIF name='{TrimForEvent(adapter.Name, 28)}' desc='{TrimForEvent(adapter.Description, 42)}' guid='{adapter.InterfaceId}' hw={adapter.IsHardwareInterface} visible={adapter.IsVisibleAdapter} up={adapter.IsUp} link={NetworkLinkFormatter.Format(adapter.LinkSpeedBitsPerSecond, adapter.IsUp)} rx={adapter.ReceivedBytesPerSecond / 1024d / 1024d:0.00} tx={adapter.SentBytesPerSecond / 1024d / 1024d:0.00} rawRx={adapter.RawReceivedBytesPerSecond / 1024d / 1024d:0.00} rawTx={adapter.RawSentBytesPerSecond / 1024d / 1024d:0.00} pdh='{TrimForEvent(adapter.PdhInstance, 32)}' pdhRx={adapter.PdhReceivedBytesPerSecond / 1024d / 1024d:0.00} pdhTx={adapter.PdhSentBytesPerSecond / 1024d / 1024d:0.00} MB/s");
        }

        foreach (var switchRow in switches.Take(4))
        {
            AddEvent("INFO", $"NETSW '{switchRow.Name}' type={switchRow.SwitchType} team={switchRow.TeamMode} uplinks={switchRow.Uplinks.Length}");
            foreach (var uplink in switchRow.Uplinks.Take(6))
            {
                var match = NetworkTopologyMatcher.MatchAdapter(adapters, uplink.Name, uplink.Description);
                AddEvent(
                    match is null ? "WARN" : "INFO",
                    $"NETMAP sw='{TrimForEvent(switchRow.Name, 18)}' uplink='{TrimForEvent(uplink.Name, 28)}' desc='{TrimForEvent(uplink.Description, 36)}' -> {(match is null ? "NO MATCH" : $"'{TrimForEvent(match.Name, 28)}' rx={match.Rx.Current:0.00} tx={match.Tx.Current:0.00} pdh='{TrimForEvent(match.PdhInstance, 28)}'")}");
            }
        }
    }

    private static string TrimForEvent(string value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        value = value.Trim();
        return value.Length <= max ? value : value[..max];
    }

    private void AddEvent(string severity, string message)
    {
        if (events.FirstOrDefault()?.Message == message && DateTime.Now - events[0].At < TimeSpan.FromSeconds(30))
            return;

        RdcLog.Info($"{severity} {message}");
        events = events.Prepend(new EventRow(DateTime.Now, severity, message)).Take(200).ToArray();
    }

    public void Dispose()
    {
        remote.Dispose();
        pdh.Dispose();
    }
}

internal sealed class RemoteCollectorManager : IDisposable
{
    private const string RemoteInstallRelative = @"Temp\hvtop-rdc";
    private const string RemoteExePath = @"C:\Windows\Temp\hvtop-rdc\hvtop-rdc.exe";
    private static readonly TimeSpan NoDataRedeployAfter = TimeSpan.FromMinutes(3);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly Options options;
    private readonly object gate = new();
    private readonly Dictionary<string, RemoteNodeSession> sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<EventRow> events = new();
    private readonly HttpClient http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(3)
    })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };
    private readonly string token = Guid.NewGuid().ToString("N");
    private bool clusterDetectedLogged;
    private string lastTargetSummary = string.Empty;

    public RemoteCollectorManager(Options options)
    {
        this.options = options;
    }

    public void UpdateTargets(ClusterNodeRow[] nodes, string localHost)
    {
        if (!options.RemoteCollectors)
            return;

        var wanted = nodes
            .Where(n => n.Status != "HOT")
            .Select(n => n.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name)
                           && !name.Equals(localHost, StringComparison.OrdinalIgnoreCase)
                           && !name.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (nodes.Length > 1 && !clusterDetectedLogged)
        {
            clusterDetectedLogged = true;
            Enqueue("INFO", $"Cluster detected, deploying hvtop remote data collection agent to peer node(s) on TCP/{options.RdcPort}");
        }

        var targetSummary = wanted.Length == 0 ? "(none)" : string.Join(", ", wanted);
        if (!targetSummary.Equals(lastTargetSummary, StringComparison.OrdinalIgnoreCase))
        {
            lastTargetSummary = targetSummary;
            Enqueue("INFO", $"RDC target nodes: {targetSummary}");
        }

        lock (gate)
        {
            foreach (var node in wanted)
            {
                if (sessions.ContainsKey(node))
                    continue;

                var session = new RemoteNodeSession(node);
                sessions[node] = session;
                Enqueue("INFO", $"RDC {node}: scheduling remote collector deployment");
                session.Worker = Task.Run(() => RunNodeAsync(session));
            }

            foreach (var node in sessions.Keys.Except(wanted, StringComparer.OrdinalIgnoreCase).ToArray())
            {
                Enqueue("INFO", $"RDC {node}: node no longer targeted, stopping remote collector");
                sessions[node].Cancel();
                sessions.Remove(node);
            }
        }
    }

    public RemoteSnapshot[] ReadSnapshots()
    {
        lock (gate)
        {
            return sessions.Values
                .Where(s => s.Latest is not null)
                .Select(s => new RemoteSnapshot(s.NodeName, s.Latest!))
                .ToArray();
        }
    }

    public EventRow[] DrainEvents()
    {
        var result = new List<EventRow>();
        while (events.TryDequeue(out var evt))
            result.Add(evt);
        return result.ToArray();
    }

    public static HostRow[] MergeHosts(HostRow[] local, RemoteSnapshot[] remote)
    {
        var rows = local.ToDictionary(h => h.Name, h => h, StringComparer.OrdinalIgnoreCase);
        foreach (var item in remote)
        {
            var host = item.Snapshot.Hosts.FirstOrDefault(h => h.Name.Equals(item.NodeName, StringComparison.OrdinalIgnoreCase))
                       ?? item.Snapshot.Hosts.FirstOrDefault(h => !double.IsNaN(h.Cpu.Current))
                       ?? item.Snapshot.Hosts.FirstOrDefault();
            if (host is not null)
                rows[host.Name] = host;
        }

        return rows.Values.OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static VmRow[] MergeVms(VmRow[] local, RemoteSnapshot[] remote)
    {
        return local
            .Concat(remote.SelectMany(r => r.Snapshot.Vms))
            .Where(vm => !string.IsNullOrWhiteSpace(vm.Name))
            .GroupBy(vm => $"{vm.HostName}\0{vm.Name}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .OrderBy(vm => vm.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static DiskRow[] MergeDisks(DiskRow[] local, RemoteSnapshot[] remote)
    {
        return local
            .Concat(remote.SelectMany(r => r.Snapshot.Disks.Select(d => string.IsNullOrWhiteSpace(d.HostName) ? d with { HostName = r.NodeName } : d)))
            .Where(disk => !string.IsNullOrWhiteSpace(disk.Name))
            .GroupBy(disk => $"{disk.HostName}\0{disk.Name}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .OrderBy(disk => disk.HostName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(disk => disk.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static NetworkSwitchRow[] MergeNetworkSwitches(NetworkSwitchRow[] local, RemoteSnapshot[] remote)
    {
        return local
            .Concat(remote.SelectMany(r => r.Snapshot.NetworkSwitches.Select(n => string.IsNullOrWhiteSpace(n.HostName) ? n with { HostName = r.NodeName } : n)))
            .Where(network => !string.IsNullOrWhiteSpace(network.Name))
            .GroupBy(network => $"{network.HostName}\0{network.Name}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .OrderBy(network => network.HostName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(network => network.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static NetworkRow[] MergeNetworks(NetworkRow[] local, RemoteSnapshot[] remote)
    {
        return local
            .Concat(remote.SelectMany(r => r.Snapshot.Networks.Select(n => string.IsNullOrWhiteSpace(n.HostName) ? n with { HostName = r.NodeName } : n)))
            .Where(network => !string.IsNullOrWhiteSpace(network.Name))
            .GroupBy(network => $"{network.HostName}\0{network.Name}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .OrderBy(network => network.HostName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(network => network.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static VmTopologyRow[] MergeTopology(VmTopologyRow[] local, RemoteSnapshot[] remote)
    {
        return local
            .Concat(remote.SelectMany(r => r.Snapshot.VmTopology.Select(t => string.IsNullOrWhiteSpace(t.HostName) ? t with { HostName = r.NodeName } : t)))
            .Where(topology => !string.IsNullOrWhiteSpace(topology.VmName))
            .GroupBy(topology => $"{topology.HostName}\0{topology.VmName}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .OrderBy(topology => topology.HostName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(topology => topology.VmName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task RunNodeAsync(RemoteNodeSession session)
    {
        while (!session.Cts.IsCancellationRequested)
        {
            try
            {
                if (!session.Deployed)
                {
                    SetState(session, "deploying");
                    DeployAndStart(session);
                    session.Deployed = true;
                    session.DeployedAt = DateTime.UtcNow;
                    session.RedeployRequested = false;
                    SetState(session, "started");
                }

                var snapshot = await PollAsync(session).ConfigureAwait(false);
                lock (gate)
                    session.Latest = snapshot;
                session.PollFailures = 0;
                session.FirstDataAt ??= DateTime.UtcNow;
                session.LastDataAt = DateTime.UtcNow;
                SetState(session, "connected");
            }
            catch (HttpRequestException ex)
            {
                session.PollFailures++;
                session.PollEndpoint = null;
                var tcpProbe = await TcpProbeAsync(session.PollHost ?? session.NodeName, options.RdcPort, session.Cts.Token).ConfigureAwait(false);
                SetState(session, "poll-error", $"HTTP poll failed ({session.PollFailures}): {Trim(ex.Message, 100)}; TCP probe {tcpProbe}");
                MaybeScheduleRedeploy(session);
            }
            catch (TaskCanceledException ex) when (!session.Cts.IsCancellationRequested)
            {
                session.PollFailures++;
                session.PollEndpoint = null;
                var tcpProbe = await TcpProbeAsync(session.PollHost ?? session.NodeName, options.RdcPort, session.Cts.Token).ConfigureAwait(false);
                SetState(session, "poll-error", $"HTTP poll timed out ({session.PollFailures}): {Trim(ex.Message, 100)}; TCP probe {tcpProbe}");
                MaybeScheduleRedeploy(session);
            }
            catch (Exception ex)
            {
                SetState(session, "error", $"deploy/start failed: {Trim(ex.Message, 120)}");
                session.Deployed = false;
            }

            try
            {
                await Task.Delay(options.RemoteRefresh, session.Cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private void MaybeScheduleRedeploy(RemoteNodeSession session)
    {
        if (session.FirstDataAt is not null || session.RedeployRequested)
            return;

        var age = DateTime.UtcNow - session.DeployedAt;
        if (age < NoDataRedeployAfter)
            return;

        session.RedeployRequested = true;
        session.Deployed = false;
        session.PollingLogged = false;
        session.PollFailures = 0;
        Enqueue("WARN", $"RDC {session.NodeName}: deployed but no data received for {NoDataRedeployAfter.TotalMinutes:N0} minutes; redeploying remote collector");
    }

    private void DeployAndStart(RemoteNodeSession session)
    {
        var localExe = Path.Combine(AppContext.BaseDirectory, "hvtop-rdc.exe");
        if (!File.Exists(localExe))
            throw new FileNotFoundException("hvtop-rdc.exe not found next to hvtop.exe", localExe);

        var remoteShareDir = $@"\\{session.NodeName}\ADMIN$\{RemoteInstallRelative}";
        SetState(session, "stopping");
        StopRemoteProcess(session.NodeName);

        SetState(session, "copying");
        Directory.CreateDirectory(remoteShareDir);
        CopyRemoteCollector(localExe, Path.Combine(remoteShareDir, "hvtop-rdc.exe"), session.NodeName);

        var refresh = options.RemoteRefresh.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var history = options.History.TotalMinutes.ToString("0.###", CultureInfo.InvariantCulture);
        var logging = options.DebugLog ? " --debug-log" : string.Empty;
        var commandLine = $"\"{RemoteExePath}\" --port {options.RdcPort} --refresh {refresh} --history {history} --token {token}{logging}";
        var script =
            "$ErrorActionPreference='Stop'; " +
            $"$node={PsSingle(session.NodeName)}; $cmd={PsSingle(commandLine)}; " +
            "Invoke-CimMethod -ComputerName $node -ClassName Win32_Process -MethodName Create -Arguments @{CommandLine=$cmd} | Out-Null";

        SetState(session, "starting");
        if (!PowerShellRunner.TryRun(script, 10000, out _, out var error, out var exitCode, out var timedOut))
            throw new InvalidOperationException(timedOut ? "remote process start timed out" : $"remote process start failed exit={exitCode}: {Trim(error, 100)}");
    }

    private void StopRemoteProcess(string nodeName)
    {
        var script =
            "$ErrorActionPreference='SilentlyContinue'; " +
            $"$node={PsSingle(nodeName)}; " +
            "Get-CimInstance -ComputerName $node -ClassName Win32_Process -Filter \"Name='hvtop-rdc.exe'\" | Invoke-CimMethod -MethodName Terminate | Out-Null";
        PowerShellRunner.TryRun(script, 7000, out _);
        Thread.Sleep(500);
    }

    private void CopyRemoteCollector(string source, string destination, string nodeName)
    {
        try
        {
            File.Copy(source, destination, overwrite: true);
            return;
        }
        catch (IOException ex)
        {
            Enqueue("WARN", $"RDC {nodeName}: remote collector exe was locked, stopping old agent and retrying copy");
            StopRemoteProcess(nodeName);
            Thread.Sleep(1000);
            try
            {
                File.Copy(source, destination, overwrite: true);
                return;
            }
            catch (IOException)
            {
                throw new IOException($"copy failed after stopping old agent: {ex.Message}");
            }
        }
    }

    private async Task<Snapshot> PollAsync(RemoteNodeSession session)
    {
        var uri = await ResolvePollEndpointAsync(session).ConfigureAwait(false);
        if (!session.PollingLogged)
        {
            session.PollingLogged = true;
            Enqueue("INFO", $"RDC {session.NodeName}: polling {uri} with direct HTTP proxy=off");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("X-Hvtop-Token", token);
        using var response = await http.SendAsync(request, session.Cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(session.Cts.Token).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, HvtopJsonContext.Default.Snapshot, session.Cts.Token).ConfigureAwait(false)
               ?? throw new InvalidOperationException("remote snapshot was empty");
    }

    private async Task<string> ResolvePollEndpointAsync(RemoteNodeSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.PollEndpoint))
            return session.PollEndpoint;

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(session.NodeName, session.Cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Enqueue("WARN", $"RDC {session.NodeName}: DNS resolution failed: {Trim(ex.Message, 120)}");
            addresses = [];
        }
        var ordered = addresses
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ThenBy(a => a.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var summary = ordered.Length == 0 ? "(none)" : string.Join(", ", ordered.Select(a => a.ToString()));
        Enqueue("INFO", $"RDC {session.NodeName}: resolved addresses {summary}");

        foreach (var address in ordered)
        {
            var probeHost = address.ToString();
            var uriHost = FormatUriHost(address);
            var probe = await TcpProbeAsync(probeHost, options.RdcPort, session.Cts.Token).ConfigureAwait(false);
            Enqueue("INFO", $"RDC {session.NodeName}: TCP probe {uriHost}:{options.RdcPort} {probe}");
            if (!probe.Equals("connect OK", StringComparison.OrdinalIgnoreCase))
                continue;

            session.PollHost = probeHost;
            session.PollEndpoint = $"http://{uriHost}:{options.RdcPort}/snapshot";
            session.PollingLogged = false;
            return session.PollEndpoint;
        }

        session.PollHost = session.NodeName;
        session.PollEndpoint = $"http://{session.NodeName}:{options.RdcPort}/snapshot";
        session.PollingLogged = false;
        return session.PollEndpoint;
    }

    private static string FormatUriHost(IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();

    private static async Task<string> TcpProbeAsync(string host, int port, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return "connect OK";
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return "connect timeout";
        }
        catch (Exception ex)
        {
            return $"connect failed: {Trim(ex.Message, 80)}";
        }
    }

    private void SetState(RemoteNodeSession session, string state, string detail = "")
    {
        if (session.LastState == state && string.IsNullOrWhiteSpace(detail))
            return;

        session.LastState = state;
        var severity = state == "error" ? "WARN" : "INFO";
        var message = state switch
        {
            "deploying" => $"RDC {session.NodeName}: deploying hvtop-rdc",
            "stopping" => $"RDC {session.NodeName}: stopping old hvtop-rdc process if present",
            "copying" => $"RDC {session.NodeName}: copying hvtop-rdc.exe via ADMIN$",
            "starting" => $"RDC {session.NodeName}: starting remote collector",
            "started" => $"RDC {session.NodeName}: deployed hvtop-rdc on TCP/{options.RdcPort}",
            "connected" => $"RDC {session.NodeName}: connected, polling every {options.RemoteRefresh.TotalSeconds:N0}s",
            "poll-error" => $"RDC {session.NodeName}: {detail}; keeping remote collector running",
            _ => $"RDC {session.NodeName}: {detail}"
        };
        Enqueue(severity, message);
    }

    private void Enqueue(string severity, string message)
    {
        RdcLog.Info($"{severity} {message}");
        events.Enqueue(new EventRow(DateTime.Now, severity, message));
    }

    private static string PsSingle(string value) => "'" + value.Replace("'", "''") + "'";

    private static string Trim(string value, int max)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= max ? value : value[..max];
    }

    public void Dispose()
    {
        RemoteNodeSession[] copy;
        lock (gate)
            copy = sessions.Values.ToArray();

        foreach (var session in copy)
        {
            session.Cancel();
            try
            {
                var stopHost = session.PollHost ?? session.NodeName;
                using var stop = new HttpRequestMessage(HttpMethod.Get, $"http://{stopHost}:{options.RdcPort}/stop");
                stop.Headers.TryAddWithoutValidation("X-Hvtop-Token", token);
                http.Send(stop);
                Enqueue("INFO", $"RDC {session.NodeName}: stop requested, preserving remote log at \\\\{session.NodeName}\\ADMIN$\\{RemoteInstallRelative}\\hvtop-rdc.log");
            }
            catch
            {
                Enqueue("WARN", $"RDC {session.NodeName}: stop request failed, preserving remote log at \\\\{session.NodeName}\\ADMIN$\\{RemoteInstallRelative}\\hvtop-rdc.log");
            }

            try
            {
                File.Delete($@"\\{session.NodeName}\ADMIN$\{RemoteInstallRelative}\hvtop-rdc.exe");
            }
            catch
            {
            }
        }

        http.Dispose();
    }

    private sealed class RemoteNodeSession
    {
        public RemoteNodeSession(string nodeName) => NodeName = nodeName;
        public string NodeName { get; }
        public CancellationTokenSource Cts { get; } = new();
        public Task? Worker { get; set; }
        public Snapshot? Latest { get; set; }
        public bool Deployed { get; set; }
        public DateTime DeployedAt { get; set; } = DateTime.UtcNow;
        public DateTime? FirstDataAt { get; set; }
        public DateTime? LastDataAt { get; set; }
        public bool PollingLogged { get; set; }
        public bool RedeployRequested { get; set; }
        public int PollFailures { get; set; }
        public string? PollHost { get; set; }
        public string? PollEndpoint { get; set; }
        public string LastState { get; set; } = string.Empty;
        public void Cancel()
        {
            try { Cts.Cancel(); } catch { }
        }
    }
}

internal sealed record RemoteSnapshot(string NodeName, Snapshot Snapshot);

internal sealed class HyperVVmSampler
{
    private static readonly string[] CpuCounters =
    [
        @"\Hyper-V Hypervisor Virtual Processor(*)\% Guest Run Time",
        @"\Hyper-V Hypervisor Virtual Processor(*)\% Total Run Time"
    ];

    private static readonly string[] MemoryCounters =
    [
        @"\Hyper-V Dynamic Memory VM(*)\Physical Memory",
        @"\Hyper-V Dynamic Memory VM(*)\Guest Visible Physical Memory"
    ];

    public VmRow[] Collect(RollingHistory history, string hostName, HyperVInventoryVm[] inventoryVms)
    {
        var cpu = ReadFirst(CpuCounters, NormalizeCpuInstance);
        var cpuInstanceCounts = new Dictionary<string, int>(PdhWildcardReader.LastInstanceCounts, StringComparer.OrdinalIgnoreCase);
        var assignedMem = PdhWildcardReader.Read(@"\Hyper-V Dynamic Memory VM(*)\Physical Memory", NormalizeDynamicMemoryInstance);
        var visibleMem = PdhWildcardReader.Read(@"\Hyper-V Dynamic Memory VM(*)\Guest Visible Physical Memory", NormalizeDynamicMemoryInstance);
        var net = PdhWildcardReader.Read(@"\Hyper-V Virtual Network Adapter(*)\Bytes/sec", NormalizeNetworkInstance);
        var readBytes = PdhWildcardReader.Read(@"\Hyper-V Virtual Storage Device(*)\Read Bytes/sec", HyperVNaming.NormalizeStorageCounterIdentity);
        var writeBytes = PdhWildcardReader.Read(@"\Hyper-V Virtual Storage Device(*)\Write Bytes/sec", HyperVNaming.NormalizeStorageCounterIdentity);
        var readOps = PdhWildcardReader.Read(@"\Hyper-V Virtual Storage Device(*)\Read Operations/Sec", HyperVNaming.NormalizeStorageCounterIdentity);
        var writeOps = PdhWildcardReader.Read(@"\Hyper-V Virtual Storage Device(*)\Write Operations/Sec", HyperVNaming.NormalizeStorageCounterIdentity);

        return inventoryVms
            .Where(vm => !string.IsNullOrWhiteSpace(vm.Name))
            .OrderBy(vm => vm.Name, StringComparer.OrdinalIgnoreCase)
            .Select(vm =>
            {
                var name = vm.Name;
                var vcpuCount = CountVirtualProcessors(cpuInstanceCounts, name);
                var cpuValue = vm.IsRunning ? (vcpuCount > 0 ? Get(cpu, name) / vcpuCount : Get(cpu, name)) : 0;
                var assignedMb = Get(assignedMem, name);
                var visibleMb = Get(visibleMem, name);
                var assignedBytes = vm.MemoryAssignedBytes > 0 ? vm.MemoryAssignedBytes : (assignedMb > 0 ? assignedMb * 1024d * 1024d : 0);
                var demandBytes = vm.MemoryDemandBytes > 0 ? vm.MemoryDemandBytes : 0;
                var memPercent = ComputeVmMemoryPercent(vm, assignedBytes, demandBytes, visibleMb);
                var memoryCapacityBytes = assignedBytes > 0
                    ? assignedBytes
                    : (visibleMb > 0 ? visibleMb * 1024d * 1024d : 0);
                var memoryCapacityLabel = BuildVmMemoryCapacityLabel(vm, memoryCapacityBytes);
                var readBytesValue = vm.IsRunning ? SumStorageCounters(readBytes, name) : 0;
                var writeBytesValue = vm.IsRunning ? SumStorageCounters(writeBytes, name) : 0;
                var ioMbps = (readBytesValue + writeBytesValue) / 1024 / 1024;
                var netMbps = vm.IsRunning ? Get(net, name) / 1024 / 1024 : 0;
                var iops = vm.IsRunning ? SumStorageCounters(readOps, name) + SumStorageCounters(writeOps, name) : 0;
                var status = vm.IsRunning ? Status.From(cpuValue, memPercent, 0, 0) : "OFF";
                var uptime = vm.IsRunning
                    ? vm.Uptime + (DateTime.UtcNow - vm.UptimeSampledAt)
                    : TimeSpan.Zero;
                if (uptime < TimeSpan.Zero)
                    uptime = TimeSpan.Zero;
                var row = new VmRow(
                    name,
                    hostName,
                    FormatVersion(vm.Version),
                    uptime,
                    vm.IsRunning,
                    vm.ReplicationDisplay,
                    vm.ReplicationStatus,
                    Metric.Percent(cpuValue),
                    vcpuCount > 0 ? $"{vcpuCount} vCPU" : "n/a vCPU",
                    Metric.Percent(memPercent),
                    memoryCapacityLabel,
                    Metric.Mbps(ioMbps),
                    Metric.Mbps(netMbps),
                    Metric.Iops(iops),
                    Metric.Milliseconds(0),
                    status);
                return history.Apply("vm:" + row.Name, row);
            })
            .ToArray();
    }

    private static double ComputeVmMemoryPercent(HyperVInventoryVm vm, double assignedBytes, double demandBytes, double visibleMb)
    {
        if (!vm.IsRunning)
            return 0;

        if (assignedBytes > 0 && demandBytes > 0)
            return Math.Clamp(demandBytes / assignedBytes * 100, 0, 100);

        if (visibleMb > 0)
        {
            var assignedMb = assignedBytes / 1024d / 1024d;
            if (assignedMb > 0)
                return Math.Clamp(assignedMb / visibleMb * 100, 0, 100);
        }

        return 0;
    }

    private static double SumStorageCounters(Dictionary<string, double> counters, string vmName)
    {
        var normalizedVmName = HyperVNaming.NormalizeVmIdentity(vmName);
        if (string.IsNullOrWhiteSpace(normalizedVmName))
            return 0;

        double total = 0;
        foreach (var pair in counters)
        {
            if (HyperVNaming.ContainsIdentityToken(pair.Key, normalizedVmName) && !double.IsNaN(pair.Value))
                total += pair.Value;
        }

        return total;
    }

    private static string BuildVmMemoryCapacityLabel(HyperVInventoryVm vm, double memoryCapacityBytes)
    {
        if (memoryCapacityBytes <= 0)
            return "n/a";

        var label = CapacityFormatter.FormatConfigCapacity(memoryCapacityBytes);
        return vm.DynamicMemoryEnabled ? $"D {label}" : label;
    }

    private static string FormatVersion(string? version)
        => string.IsNullOrWhiteSpace(version) ? "n/a" : version.Trim();

    private static Dictionary<string, double> ReadFirst(string[] paths, Func<string, string> normalizeInstance)
    {
        foreach (var path in paths)
        {
            var values = PdhWildcardReader.Read(path, normalizeInstance);
            if (values.Count > 0) return values;
        }
        return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    }

    private static double Get(Dictionary<string, double> values, string key)
        => values.TryGetValue(key, out var value) && !double.IsNaN(value) ? value : 0;

    private static int CountVirtualProcessors(Dictionary<string, int> cpuInstanceCounts, string vmName)
        => cpuInstanceCounts.TryGetValue(vmName, out var count) ? Math.Max(1, count) : 0;

    private static string NormalizeCpuInstance(string instance)
    {
        var colon = instance.IndexOf(':');
        return colon > 0 ? instance[..colon].Trim() : instance.Trim();
    }

    private static string NormalizeDynamicMemoryInstance(string instance) => instance.Trim();

    private static string NormalizeNetworkInstance(string instance)
    {
        var marker = instance.IndexOf("_Network Adapter", StringComparison.OrdinalIgnoreCase);
        if (marker > 0) return instance[..marker].Trim();
        if (instance.Contains("__DEVICE_", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        if (instance.StartsWith("vSwitch", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        return instance.Trim();
    }

}

internal sealed class HyperVInventory
{
    private readonly object gate = new();
    private HyperVInventoryVm[] cache = [];
    private string? lastEventMessage;
    private readonly Queue<InventoryEvent> pendingEvents = new();
    public bool IsRefreshing { get; private set; }

    public void RequestRefresh()
    {
        lock (gate)
        {
            if (IsRefreshing) return;
            IsRefreshing = true;
            _ = Task.Run(RefreshAsync);
        }
    }

    public HyperVInventoryResult TryRead()
    {
        lock (gate)
        {
            var events = pendingEvents.ToArray();
            pendingEvents.Clear();
            return new HyperVInventoryResult(cache, "PowerShell", events);
        }
    }

    private void RefreshAsync()
    {
        try
        {
            var fallback = TryReadPowerShell();
            lock (gate)
            {
                if (fallback.Available)
                {
                    cache = fallback.Vms;
                    EnqueueEvent("WARN", "Hyper-V native WMI inventory disabled in single-file build, using PowerShell fallback.");
                }
                else
                {
                    EnqueueEvent("INFO", "Hyper-V inventory not detected; standard Windows host mode active.");
                }

                foreach (var evt in fallback.Events)
                    EnqueueEvent(evt.Severity, evt.Message);
            }
        }
        finally
        {
            lock (gate)
                IsRefreshing = false;
        }
    }

    private static HyperVInventoryData TryReadPowerShell()
    {
        const string script = "$ErrorActionPreference='Stop'; " +
            "$available=$false; $rows=@(); " +
            "try { " +
            "Import-Module Hyper-V -ErrorAction Stop | Out-Null; $available=$true; " +
            "$rows = @(Get-VM -ErrorAction Stop | Select-Object Name,@{N='Version';E={[string]$_.Version}},@{N='IsRunning';E={[bool]($_.State -eq 'Running')}},MemoryAssigned,MemoryDemand,MemoryStatus,DynamicMemoryEnabled,@{N='ReplicationState';E={try {[string]$_.ReplicationState} catch {''}}},@{N='ReplicationHealth';E={try {[string]$_.ReplicationHealth} catch {''}}}); " +
            "} catch { " +
            "$rows = @(Get-CimInstance -Namespace root/virtualization/v2 -ClassName Msvm_ComputerSystem -ErrorAction Stop " +
            "| Where-Object { $_.Caption -eq 'Virtual Machine' } " +
            "| Select-Object @{N='Name';E={$_.ElementName}},@{N='Version';E={''}},@{N='IsRunning';E={[bool]($_.EnabledState -eq 2)}},@{N='UptimeSeconds';E={if ($_.EnabledState -eq 2 -and $_.OnTimeInMilliseconds) { [double]$_.OnTimeInMilliseconds / 1000 } else { 0 }}},@{N='MemoryAssigned';E={0}},@{N='MemoryDemand';E={0}},@{N='MemoryStatus';E={''}},@{N='DynamicMemoryEnabled';E={$false}},@{N='ReplicationState';E={''}},@{N='ReplicationHealth';E={''}}); " +
            "if ($rows -and $rows.Count -gt 0) { $available=$true }; " +
            "} ; " +
            "[pscustomobject]@{Available=$available; Vms=@($rows)} | ConvertTo-Json -Compress -Depth 4";

        if (!PowerShellRunner.TryRun(script, 30000, out var output, out var error, out var exitCode, out var timedOut))
        {
            var reason = timedOut ? "timed out" : $"failed exit={exitCode}";
            return new HyperVInventoryData([], false, [new InventoryEvent("WARN", $"HVDIAG inventory PowerShell {reason}: {TrimForEvent(error, 140)}")]);
        }

        var parsed = ParseInventoryJson(output);
        var uptime = parsed.Vms.Length > 0 ? TryReadPowerShellUptime() : new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        var sampledAt = DateTime.UtcNow;
        if (uptime.Count > 0)
            parsed = parsed with
            {
                Vms = parsed.Vms
                    .Select(vm => uptime.TryGetValue(vm.Name, out var value) ? vm with { Uptime = value, UptimeSampledAt = sampledAt } : vm)
                    .ToArray()
            };
        var zeroVmEvents = parsed.Vms.Length == 0
            ? new[] { new InventoryEvent("WARN", $"HVDIAG raw='{TrimForEvent(output, 120)}'") }
            : [];
        return parsed with
        {
            Events =
            [
                .. parsed.Events,
                new InventoryEvent("INFO", $"HVDIAG inventory available={parsed.Available} rows={parsed.Vms.Length} json={output.Length}"),
                .. parsed.Vms.Take(3).Select(vm => new InventoryEvent("INFO", $"HVDIAG vm='{TrimForEvent(vm.Name, 28)}' run={vm.IsRunning} up={UptimeFormatter.FormatShort(vm.Uptime)} ver='{TrimForEvent(vm.Version, 10)}' repl='{TrimForEvent(vm.ReplicationDisplay, 22)}'")),
                .. zeroVmEvents
            ]
        };
    }

    private static Dictionary<string, TimeSpan> TryReadPowerShellUptime()
    {
        const string script = "Import-Module Hyper-V -ErrorAction Stop | Out-Null; " +
            "@(Get-VM -ErrorAction Stop | Select-Object Name,@{N='UptimeSeconds';E={try { [double]$_.Uptime.TotalSeconds } catch { 0 }}}) | ConvertTo-Json -Compress -Depth 3";

        if (!PowerShellRunner.TryRun(script, 5000, out var output) || string.IsNullOrWhiteSpace(output))
            return new(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var document = JsonDocument.Parse(output);
            var rows = document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => document.RootElement.EnumerateArray().ToArray(),
                JsonValueKind.Object => [document.RootElement],
                _ => []
            };

            return rows
                .Select(row => new
                {
                    Name = ReadJsonString(row, "Name"),
                    Uptime = TimeSpan.FromSeconds(Math.Max(0, ReadJsonDouble(row, "UptimeSeconds")))
                })
                .Where(row => !string.IsNullOrWhiteSpace(row.Name))
                .ToDictionary(row => row.Name, row => row.Uptime, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static HyperVInventoryData ParseInventoryJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return HyperVInventoryData.Empty;

        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement[] rows;
            var available = true;
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("Vms", out var vmsElement))
            {
                available = ReadJsonBool(document.RootElement, "Available");
                rows = vmsElement.ValueKind switch
                {
                    JsonValueKind.Array => vmsElement.EnumerateArray().ToArray(),
                    JsonValueKind.Object => [vmsElement],
                    _ => []
                };
            }
            else
            {
                rows = document.RootElement.ValueKind switch
                {
                    JsonValueKind.Array => document.RootElement.EnumerateArray().ToArray(),
                    JsonValueKind.Object => [document.RootElement],
                    _ => []
                };
            }

            var vms = rows
                .Select(element =>
                {
                    var name = ReadJsonString(element, "Name");
                    var version = ReadJsonString(element, "Version");
                    var isRunning = ReadJsonBool(element, "IsRunning");
                    var uptime = ReadJsonTimeSpan(element, "Uptime");
                    if (uptime == TimeSpan.Zero)
                    {
                        var uptimeSeconds = ReadJsonDouble(element, "UptimeSeconds");
                        if (uptimeSeconds > 0)
                            uptime = TimeSpan.FromSeconds(uptimeSeconds);
                    }
                    var memoryAssignedBytes = ReadJsonDouble(element, "MemoryAssigned");
                    var memoryDemandBytes = ReadJsonDouble(element, "MemoryDemand");
                    var memoryStatus = ReadJsonString(element, "MemoryStatus");
                    var dynamicMemoryEnabled = ReadJsonBool(element, "DynamicMemoryEnabled");
                    var replicationState = ReadJsonString(element, "ReplicationState");
                    var replicationHealth = ReadJsonString(element, "ReplicationHealth");
                    return new HyperVInventoryVm(name, version, isRunning, uptime, DateTime.UtcNow, memoryAssignedBytes, memoryDemandBytes, memoryStatus, dynamicMemoryEnabled, replicationState, replicationHealth);
                })
                .Where(vm => !string.IsNullOrWhiteSpace(vm.Name))
                .DistinctBy(vm => vm.Name, StringComparer.OrdinalIgnoreCase)
                .OrderBy(vm => vm.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new HyperVInventoryData(vms, available, []);
        }
        catch (Exception ex)
        {
            return new HyperVInventoryData([], false, [new InventoryEvent("WARN", $"HVDIAG parse failed: {TrimForEvent(ex.Message, 120)}"), new InventoryEvent("WARN", $"HVDIAG raw='{TrimForEvent(json, 120)}'")]);
        }
    }

    internal static string ReadJsonString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) ? value.ToString().Trim() : string.Empty;

    internal static bool ReadJsonBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return false;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetString()?.Equals("True", StringComparison.OrdinalIgnoreCase) == true,
            JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
            _ => false
        };
    }

    internal static double ReadJsonDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDouble(out var number) ? number : 0,
            JsonValueKind.String => double.TryParse(value.GetString(), out var number) ? number : 0,
            _ => 0
        };
    }

    private static TimeSpan ReadJsonTimeSpan(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return TimeSpan.Zero;

        try
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var ticks = ReadJsonDouble(value, "Ticks");
                if (ticks > 0)
                    return TimeSpan.FromTicks((long)ticks);

                var days = ReadJsonDouble(value, "Days");
                var hours = ReadJsonDouble(value, "Hours");
                var minutes = ReadJsonDouble(value, "Minutes");
                var seconds = ReadJsonDouble(value, "Seconds");
                return TimeSpan.FromDays(days) + TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
            }

            if (value.ValueKind == JsonValueKind.String && TimeSpan.TryParse(value.GetString(), out var parsed))
                return parsed;
        }
        catch
        {
        }

        return TimeSpan.Zero;
    }

    internal static string[] ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var ch in line)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        values.Add(current.ToString());
        return values.ToArray();
    }

    private string? DedupEvent(string message)
    {
        if (string.Equals(lastEventMessage, message, StringComparison.Ordinal))
            return null;
        lastEventMessage = message;
        return message;
    }

    private void EnqueueEvent(string severity, string message)
    {
        var deduped = DedupEvent(message);
        if (!string.IsNullOrWhiteSpace(deduped))
            pendingEvents.Enqueue(new InventoryEvent(severity, deduped));
    }

    private static string TrimForEvent(string value, int max)
    {
        value = (value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return value.Length <= max ? value : value[..max];
    }

}

internal sealed record HyperVInventoryVm(
    string Name,
    string Version,
    bool IsRunning,
    TimeSpan Uptime,
    DateTime UptimeSampledAt,
    double MemoryAssignedBytes,
    double MemoryDemandBytes,
    string MemoryStatus,
    bool DynamicMemoryEnabled,
    string ReplicationState,
    string ReplicationHealth)
{
    public string ReplicationDisplay => ReplicationFormatter.Display(ReplicationState, ReplicationHealth);
    public string ReplicationStatus => ReplicationFormatter.Status(ReplicationState, ReplicationHealth);
}
internal sealed record HyperVInventoryData(HyperVInventoryVm[] Vms, bool Available, InventoryEvent[] Events)
{
    public static HyperVInventoryData Empty { get; } = new([], false, []);
}
internal sealed record InventoryEvent(string Severity, string Message);
internal sealed record HyperVInventoryResult(HyperVInventoryVm[] Vms, string Source, InventoryEvent[] Events);

internal sealed class ClusterInventory
{
    private readonly object gate = new();
    private ClusterRow[] clusters = [];
    private ClusterNodeRow[] nodes = [];
    private string? pendingEventMessage;
    private string pendingEventSeverity = "INFO";
    private bool refreshRequested = true;
    private bool loggedState;

    public void RequestRefresh()
    {
        lock (gate)
            refreshRequested = true;
    }

    public ClusterInventoryResult TryRead()
    {
        lock (gate)
        {
            if (refreshRequested)
            {
                refreshRequested = false;
                Refresh();
            }

            var eventMessage = pendingEventMessage;
            var eventSeverity = pendingEventSeverity;
            pendingEventMessage = null;
            return new ClusterInventoryResult(clusters, nodes, eventMessage, eventSeverity);
        }
    }

    private void Refresh()
    {
        var data = TryReadPowerShell();
        clusters = data.Clusters;
        nodes = data.Nodes;

        if (!loggedState)
        {
            loggedState = true;
            if (clusters.Length > 0)
            {
                var cluster = clusters[0];
                pendingEventMessage = $"Failover cluster detected: {cluster.Name} ({cluster.UpNodeCount}/{cluster.NodeCount} nodes up)";
                pendingEventSeverity = "INFO";
            }
            else
            {
                pendingEventMessage = "No failover cluster detected on this host.";
                pendingEventSeverity = "INFO";
            }
        }
    }

    private static ClusterInventoryData TryReadPowerShell()
    {
        const string script = "$ErrorActionPreference='Stop'; " +
            "Import-Module FailoverClusters -ErrorAction Stop | Out-Null; " +
            "$cluster=Get-Cluster -ErrorAction Stop; " +
            "$nodes=@(Get-ClusterNode -Cluster $cluster.Name | Select-Object Name,State); " +
            "$owner=''; " +
            "try { $core=@(Get-ClusterGroup -Cluster $cluster.Name | Where-Object { $_.GroupType -eq 'Cluster' -or $_.Name -eq 'Cluster Group' } | Select-Object -First 1); if ($core.Count -gt 0 -and $core[0].OwnerNode) { $owner=[string]$core[0].OwnerNode.Name } } catch { } ; " +
            "if ([string]::IsNullOrWhiteSpace($owner)) { try { $owner=[string](Get-ClusterGroup -Cluster $cluster.Name | Select-Object -First 1 -ExpandProperty OwnerNode).Name } catch { } } ; " +
            "[pscustomobject]@{ " +
            "Name=[string]$cluster.Name; " +
            "OwnerNode=$owner; " +
            "Quorum=[string]$cluster.QuorumType; " +
            "FunctionalLevel=[string]$cluster.ClusterFunctionalLevel; " +
            "Nodes=@($nodes) " +
            "} | ConvertTo-Json -Compress -Depth 4";

        if (!PowerShellRunner.TryRun(script, 8000, out var output))
            return ClusterInventoryData.Empty;

        return ParseJson(output);
    }

    private static ClusterInventoryData ParseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return ClusterInventoryData.Empty;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return ClusterInventoryData.Empty;

            var name = HyperVInventory.ReadJsonString(document.RootElement, "Name");
            if (string.IsNullOrWhiteSpace(name))
                return ClusterInventoryData.Empty;

            var nodes = ReadNodes(document.RootElement);
            var upNodes = nodes.Count(n => n.Status is "OK" or "BUSY");
            var clusterStatus = nodes.Length == 0 ? "OK"
                : upNodes == nodes.Length ? "OK"
                : upNodes == 0 ? "HOT"
                : "BUSY";
            var cluster = new ClusterRow(
                name,
                nodes.Length,
                upNodes,
                HyperVInventory.ReadJsonString(document.RootElement, "OwnerNode"),
                HyperVInventory.ReadJsonString(document.RootElement, "Quorum"),
                HyperVInventory.ReadJsonString(document.RootElement, "FunctionalLevel"),
                clusterStatus);
            return new ClusterInventoryData([cluster], nodes);
        }
        catch
        {
            return ClusterInventoryData.Empty;
        }
    }

    private static ClusterNodeRow[] ReadNodes(JsonElement root)
    {
        if (!root.TryGetProperty("Nodes", out var value))
            return [];

        var rows = value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray().ToArray(),
            JsonValueKind.Object => [value],
            _ => []
        };

        return rows
            .Select(row =>
            {
                var name = HyperVInventory.ReadJsonString(row, "Name");
                var state = HyperVInventory.ReadJsonString(row, "State");
                return new ClusterNodeRow(name, state, ClusterNodeStatus(state));
            })
            .Where(row => !string.IsNullOrWhiteSpace(row.Name))
            .DistinctBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ClusterNodeStatus(string state)
    {
        if (state.Equals("Up", StringComparison.OrdinalIgnoreCase)) return "OK";
        if (state.Equals("Paused", StringComparison.OrdinalIgnoreCase)) return "BUSY";
        if (state.Equals("Joining", StringComparison.OrdinalIgnoreCase)) return "BUSY";
        if (state.Equals("Down", StringComparison.OrdinalIgnoreCase)) return "HOT";
        return string.IsNullOrWhiteSpace(state) ? "OK" : "BUSY";
    }
}

internal sealed record ClusterInventoryData(ClusterRow[] Clusters, ClusterNodeRow[] Nodes)
{
    public static ClusterInventoryData Empty { get; } = new([], []);
}

internal sealed record ClusterInventoryResult(ClusterRow[] Clusters, ClusterNodeRow[] Nodes, string? EventMessage, string EventSeverity);

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

internal sealed class HyperVTopology
{
    private readonly object gate = new();
    private VmTopologyRow[] cache = [];
    private NetworkSwitchTopologyRow[] switchCache = [];
    private string? lastEventMessage;
    private string? pendingEventMessage;
    private string pendingEventSeverity = "INFO";
    public bool IsRefreshing { get; private set; }
    private DiskRow[] latestDisks = [];
    private NetworkRow[] latestNetworks = [];

    public void RequestRefresh()
    {
        lock (gate)
        {
            if (IsRefreshing) return;
            IsRefreshing = true;
            var disks = latestDisks;
            var networks = latestNetworks;
            _ = Task.Run(() => RefreshAsync(disks, networks));
        }
    }

    public HyperVTopologyResult TryRead(DiskRow[] disks, NetworkRow[] networks)
    {
        lock (gate)
        {
            latestDisks = disks;
            latestNetworks = networks;

            var eventMessage = pendingEventMessage;
            var eventSeverity = pendingEventSeverity;
            pendingEventMessage = null;
            return new HyperVTopologyResult(cache, switchCache, "PowerShell", eventMessage, eventSeverity);
        }
    }

    private void RefreshAsync(DiskRow[] disks, NetworkRow[] networks)
    {
        try
        {
            var data = TryReadPowerShell(disks, networks);
            lock (gate)
            {
                if (data.Topology.Length > 0 || cache.Length == 0)
                    cache = data.Topology;
                if (data.Switches.Length > 0 || switchCache.Length == 0)
                    switchCache = data.Switches;
                if (data.Topology.Length == 0 && data.Switches.Length == 0)
                {
                    pendingEventMessage = DedupEvent("Hyper-V network topology not detected; using Windows network adapter view.");
                    pendingEventSeverity = "INFO";
                }
                else
                {
                    pendingEventMessage = DedupEvent("Hyper-V native WMI topology disabled in single-file build, using PowerShell fallback.");
                    pendingEventSeverity = "WARN";
                }
            }
        }
        catch
        {
            lock (gate)
            {
                pendingEventMessage = DedupEvent("Hyper-V topology unavailable; using Windows network adapter view.");
                pendingEventSeverity = "WARN";
            }
        }
        finally
        {
            lock (gate)
                IsRefreshing = false;
        }
    }

    private static HyperVTopologyData TryReadPowerShell(DiskRow[] disks, NetworkRow[] networks)
    {
        var diskMap = TryReadPowerShellDisks(disks);
        var switches = TryReadPowerShellSwitches();
        var netMap = TryReadPowerShellNetworks(switches).ToDictionary(vm => vm.VmName, StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(diskMap.Keys.Concat(netMap.Keys), StringComparer.OrdinalIgnoreCase);
        return new HyperVTopologyData(
            names
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new VmTopologyRow(
                name,
                diskMap.TryGetValue(name, out var vmDisks) ? vmDisks : [],
                netMap.TryGetValue(name, out var vmNets) ? vmNets.Networks : []))
            .ToArray(),
            switches);
    }

    private static Dictionary<string, VDiskRow[]> TryReadPowerShellDisks(DiskRow[] disks)
    {
        if (!PowerShellRunner.TryRun("Import-Module Hyper-V -ErrorAction Stop; Get-VM | Get-VMHardDiskDrive | Select-Object VMName,Path | ConvertTo-Csv -NoTypeInformation", 5000, out var output))
            return new(StringComparer.OrdinalIgnoreCase);

        var storageCounters = VirtualDiskCounterSampler.Read();
        return output
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(HyperVInventory.ParseCsvLine)
            .Where(parts => parts.Length >= 2 && IsVirtualDiskPath(parts[1]))
            .GroupBy(parts => parts[0].Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(parts =>
                {
                    var path = parts[1]?.Trim() ?? string.Empty;
                    var diskName = Path.GetFileName(path);
                    var stats = HyperVNaming.ResolveDiskStats(storageCounters, path, diskName);
                    stats ??= new VirtualDiskStats(0, 0, 0, 0);
                    return new VDiskRow(
                        string.IsNullOrWhiteSpace(diskName) ? path : diskName,
                        path,
                        ResolveStorageName(path, disks),
                        stats.ReadMbps,
                        stats.ReadIops,
                        stats.WriteMbps,
                        stats.WriteIops);
                }).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static VmTopologyRow[] TryReadPowerShellNetworks(NetworkSwitchTopologyRow[] switches)
    {
        const string script = "Import-Module Hyper-V -ErrorAction Stop; @(Get-VMNetworkAdapter -VMName * | Select-Object VMName,Name,SwitchName) | ConvertTo-Json -Compress";
        if (!PowerShellRunner.TryRun(script, 5000, out var output))
            return [];

        return ParseVmNetworkJson(output, switches);
    }

    private static NetworkSwitchTopologyRow[] TryReadPowerShellSwitches()
    {
        const string script = "Import-Module Hyper-V -ErrorAction Stop | Out-Null; " +
            "$netAdapters = @(Get-NetAdapter | Where-Object { $_.HardwareInterface -eq $true } | Select-Object Name,InterfaceDescription,Status,LinkSpeed); " +
            "$switches = @(Get-VMSwitch | ForEach-Object { " +
            "$sw = $_; $members=@(); $teamMode=''; " +
            "$descs=@(); " +
            "if ($sw.PSObject.Properties.Match('NetAdapterInterfaceDescriptions').Count -gt 0) { $descs = @($sw.NetAdapterInterfaceDescriptions) } " +
            "elseif ($sw.PSObject.Properties.Match('NetAdapterInterfaceDescription').Count -gt 0 -and $sw.NetAdapterInterfaceDescription) { $descs = @($sw.NetAdapterInterfaceDescription) } " +
            "try { if ($sw.PSObject.Properties.Match('EmbeddedTeamingEnabled').Count -gt 0 -and [bool]$sw.EmbeddedTeamingEnabled) { $teamMode='SET' } } catch { } ; " +
            "try { if (Get-Command Get-VMSwitchTeam -ErrorAction SilentlyContinue) { " +
            "$setTeam = Get-VMSwitchTeam -Name $sw.Name -ErrorAction SilentlyContinue; " +
            "if ($setTeam) { $teamMode='SET'; " +
            "if ($setTeam.PSObject.Properties.Match('NetAdapterInterfaceDescriptions').Count -gt 0) { $members += @($setTeam.NetAdapterInterfaceDescriptions) } " +
            "elseif ($setTeam.PSObject.Properties.Match('NetAdapterInterfaceDescription').Count -gt 0) { $members += @($setTeam.NetAdapterInterfaceDescription) } " +
            "if ($setTeam.PSObject.Properties.Match('NetAdapterNames').Count -gt 0) { $members += @($setTeam.NetAdapterNames) } " +
            "elseif ($setTeam.PSObject.Properties.Match('NetAdapterName').Count -gt 0) { $members += @($setTeam.NetAdapterName) } " +
            "} } } catch { } ; " +
            "if ([string]::IsNullOrWhiteSpace($teamMode)) { try { if (Get-Command Get-NetLbfoTeam -ErrorAction SilentlyContinue) { " +
            "$bound=@(Get-NetAdapter | Where-Object { $descs -contains $_.InterfaceDescription -or $descs -contains $_.Name }); " +
            "foreach ($adapter in $bound) { $team=@(Get-NetLbfoTeam -ErrorAction Stop | Where-Object { $_.Name -eq $adapter.Name -or $_.TeamNics -contains $adapter.Name } | Select-Object -First 1); " +
            "if ($team.Count -gt 0) { $teamMode='LBFO'; $members += @(Get-NetLbfoTeamMember -Team $team[0].Name -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name); break } } " +
            "} } catch { } } " +
            "$keys=@($members + $descs | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique); " +
            "$uplinks=@(); " +
            "foreach ($key in $keys) { " +
            "$matches = @($netAdapters | Where-Object { $_.Name -eq $key -or $_.InterfaceDescription -eq $key -or $_.Name -like ('*' + $key + '*') -or $_.InterfaceDescription -like ('*' + $key + '*') -or $key -like ('*' + $_.Name + '*') -or $key -like ('*' + $_.InterfaceDescription + '*') } | Select-Object Name,InterfaceDescription,Status,LinkSpeed -Unique); " +
            "foreach ($match in $matches) { " +
            "if ($match.Name -like 'vEthernet*') { continue } " +
            "$uplinks += [pscustomobject]@{ Name=[string]$match.Name; Description=[string]$match.InterfaceDescription; Link=[string]$match.LinkSpeed; IsUp=[bool]($match.Status -eq 'Up') } " +
            "} " +
            "} ; " +
            "$uplinks = @($uplinks | Group-Object Name | ForEach-Object { $_.Group | Select-Object -First 1 }); " +
            "[pscustomobject]@{ Name=$sw.Name; SwitchType=[string]$sw.SwitchType; Uplinks=@($uplinks); TeamMode=$teamMode } " +
            "}); @($switches) | ConvertTo-Json -Compress -Depth 4";
        if (!PowerShellRunner.TryRun(script, 7000, out var output))
            return [];

        return ParseSwitchTopologyJson(output);
    }

    private static VmTopologyRow[] ParseVmNetworkJson(string json, NetworkSwitchTopologyRow[] switches)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            var rows = document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => document.RootElement.EnumerateArray().ToArray(),
                JsonValueKind.Object => [document.RootElement],
                _ => []
            };

            var switchMap = switches.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
            return rows
                .Select(element => new
                {
                    VmName = HyperVInventory.ReadJsonString(element, "VMName"),
                    Name = HyperVInventory.ReadJsonString(element, "Name"),
                    SwitchName = HyperVInventory.ReadJsonString(element, "SwitchName")
                })
                .Where(row => !string.IsNullOrWhiteSpace(row.VmName))
                .GroupBy(row => row.VmName, StringComparer.OrdinalIgnoreCase)
                .Select(group => new VmTopologyRow(
                    group.Key,
                    [],
                    group.Select(row =>
                    {
                        var switchName = string.IsNullOrWhiteSpace(row.SwitchName) ? "n/a" : row.SwitchName;
                        var uplinkSummary = switchMap.TryGetValue(switchName, out var switchRow)
                            ? BuildUplinkSummary(switchRow.Uplinks)
                            : "n/a";
                        return new VmNetworkPathRow(
                            string.IsNullOrWhiteSpace(row.Name) ? "Ethernet" : row.Name,
                            switchName,
                            uplinkSummary);
                    })
                    .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
                .OrderBy(row => row.VmName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static NetworkSwitchTopologyRow[] ParseSwitchTopologyJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            var rows = document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => document.RootElement.EnumerateArray().ToArray(),
                JsonValueKind.Object => [document.RootElement],
                _ => []
            };

            return rows
                .Select(element => new NetworkSwitchTopologyRow(
                    HyperVInventory.ReadJsonString(element, "Name"),
                    HyperVInventory.ReadJsonString(element, "SwitchType"),
                    ReadJsonUplinks(element, "Uplinks"),
                    HyperVInventory.ReadJsonString(element, "TeamMode")))
                .Where(row => !string.IsNullOrWhiteSpace(row.Name))
                .DistinctBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static NetworkUplinkInfo[] ReadJsonUplinks(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return [];

        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray()
                .Select(item => new NetworkUplinkInfo(
                    HyperVInventory.ReadJsonString(item, "Name"),
                    HyperVInventory.ReadJsonString(item, "Description"),
                    NormalizeTopologyLink(HyperVInventory.ReadJsonString(item, "Link"), HyperVInventory.ReadJsonBool(item, "IsUp")),
                    HyperVInventory.ReadJsonBool(item, "IsUp"),
                    NetworkLinkFormatter.ParseBitsPerSecond(NormalizeTopologyLink(HyperVInventory.ReadJsonString(item, "Link"), HyperVInventory.ReadJsonBool(item, "IsUp")))))
                .Where(item => !string.IsNullOrWhiteSpace(item.Name) || !string.IsNullOrWhiteSpace(item.Description))
                .DistinctBy(item => $"{item.Name}|{item.Description}", StringComparer.OrdinalIgnoreCase)
                .ToArray();

        var single = value.ToString().Trim();
        return string.IsNullOrWhiteSpace(single) ? [] : [new NetworkUplinkInfo(single, single, "DOWN", false, 0)];
    }

    private static string NormalizeTopologyLink(string raw, bool isUp)
    {
        if (!isUp)
            return "DOWN";

        var text = raw?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return "DOWN";
        if (text.Contains("100", StringComparison.OrdinalIgnoreCase)) return "100G";
        if (text.Contains("40", StringComparison.OrdinalIgnoreCase)) return "40G";
        if (text.Contains("25", StringComparison.OrdinalIgnoreCase)) return "25G";
        if (text.Contains("10", StringComparison.OrdinalIgnoreCase)) return "10G";
        if (text.Contains("1 G", StringComparison.OrdinalIgnoreCase) || text.Contains("1000", StringComparison.OrdinalIgnoreCase)) return "GbE";
        if (text.Contains("100 M", StringComparison.OrdinalIgnoreCase) || text.Contains("100Mbps", StringComparison.OrdinalIgnoreCase)) return "FE";
        return text;
    }

    private static string BuildUplinkSummary(NetworkUplinkInfo[] uplinks)
    {
        var names = uplinks
            .Select(u => string.IsNullOrWhiteSpace(u.Name) ? u.Description : u.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 0) return "n/a";
        return string.Join(", ", names);
    }

    private static bool IsVirtualDiskPath(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && (path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".avhdx", StringComparison.OrdinalIgnoreCase));

    private static string ResolveStorageName(string path, DiskRow[] disks)
    {
        var root = StorageInventory.ResolveStorageKey(path);
        if (string.IsNullOrWhiteSpace(root))
            return string.Empty;

        var match = disks
            .Where(d =>
                d.Name.Equals(root, StringComparison.OrdinalIgnoreCase)
                || d.Name.StartsWith(root + " ", StringComparison.OrdinalIgnoreCase)
                || root.StartsWith(d.Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.Name.Length)
            .FirstOrDefault();
        return match?.Name ?? root;
    }

    private string? DedupEvent(string message)
    {
        if (string.Equals(lastEventMessage, message, StringComparison.Ordinal))
            return null;
        lastEventMessage = message;
        return message;
    }
}

internal sealed record HyperVTopologyData(VmTopologyRow[] Topology, NetworkSwitchTopologyRow[] Switches);

internal sealed record HyperVTopologyResult(VmTopologyRow[] Topology, NetworkSwitchTopologyRow[] Switches, string Source, string? EventMessage, string EventSeverity)
{
    public static HyperVTopologyResult Empty { get; } = new([], [], "None", null, "INFO");
}

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
            Metric.Plain(0),
            uplink.IsUp ? "IDLE" : "OFF");
    }

    private static bool IsVirtualSwitchAdapter(NetworkRow adapter)
        => adapter.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase)
           || adapter.Description.Contains("Hyper-V Virtual Ethernet", StringComparison.OrdinalIgnoreCase);
}

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

internal static class VirtualDiskCounterSampler
{
    public static Dictionary<string, VirtualDiskStats> Read()
    {
        var readBytes = PdhWildcardReader.Read(@"\Hyper-V Virtual Storage Device(*)\Read Bytes/sec", NormalizeDiskCounterInstance);
        var writeBytes = PdhWildcardReader.Read(@"\Hyper-V Virtual Storage Device(*)\Write Bytes/sec", NormalizeDiskCounterInstance);
        var readOps = PdhWildcardReader.Read(@"\Hyper-V Virtual Storage Device(*)\Read Operations/Sec", NormalizeDiskCounterInstance);
        var writeOps = PdhWildcardReader.Read(@"\Hyper-V Virtual Storage Device(*)\Write Operations/Sec", NormalizeDiskCounterInstance);
        var keys = new HashSet<string>(readBytes.Keys.Concat(writeBytes.Keys).Concat(readOps.Keys).Concat(writeOps.Keys), StringComparer.OrdinalIgnoreCase);
        return keys.ToDictionary(
            key => key,
            key => new VirtualDiskStats(
                ReadValue(readBytes, key) / 1024 / 1024,
                ReadValue(readOps, key),
                ReadValue(writeBytes, key) / 1024 / 1024,
                ReadValue(writeOps, key)),
            StringComparer.OrdinalIgnoreCase);
    }

    private static double ReadValue(Dictionary<string, double> values, string key)
        => values.TryGetValue(key, out var value) && !double.IsNaN(value) ? value : 0;

    private static string NormalizeDiskCounterInstance(string instance)
        => HyperVNaming.NormalizeStorageCounterIdentity(instance);
}

internal sealed record VirtualDiskStats(double ReadMbps, double ReadIops, double WriteMbps, double WriteIops);

internal static class VirtualNetworkCounterSampler
{
    public static Dictionary<string, VirtualNetworkStats> Read()
    {
        var rx = PdhWildcardReader.Read(@"\Hyper-V Virtual Network Adapter(*)\Bytes Received/sec", NormalizeNetworkCounterInstance);
        var tx = PdhWildcardReader.Read(@"\Hyper-V Virtual Network Adapter(*)\Bytes Sent/sec", NormalizeNetworkCounterInstance);
        var keys = new HashSet<string>(rx.Keys.Concat(tx.Keys), StringComparer.OrdinalIgnoreCase);
        return keys.ToDictionary(
            key => key,
            key => new VirtualNetworkStats(
                ReadValue(rx, key) / 1024 / 1024,
                ReadValue(tx, key) / 1024 / 1024),
            StringComparer.OrdinalIgnoreCase);
    }

    private static double ReadValue(Dictionary<string, double> values, string key)
        => values.TryGetValue(key, out var value) && !double.IsNaN(value) ? value : 0;

    private static string NormalizeNetworkCounterInstance(string instance)
        => HyperVNaming.NormalizeStorageCounterIdentity(instance);
}

internal sealed record VirtualNetworkStats(double RxMbps, double TxMbps);

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

internal static class BoundedCall
{
    public static bool TryExecute<T>(Func<T> func, int timeoutMs, out T result)
    {
        try
        {
            var task = Task.Run(func);
            if (task.Wait(timeoutMs))
            {
                result = task.Result;
                return true;
            }
        }
        catch
        {
        }
        result = default!;
        return false;
    }
}

internal static class PowerShellRunner
{
    public static bool TryRun(string command, int timeoutMs, out string output)
        => TryRun(command, timeoutMs, out output, out _, out _, out _);

    public static bool TryRun(string command, int timeoutMs, out string output, out string error, out int exitCode, out bool timedOut)
    {
        using var process = new Process();
        process.StartInfo.FileName = "powershell.exe";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command));
        process.StartInfo.Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}";
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(true); } catch { }
            output = string.Empty;
            error = "timeout";
            exitCode = -1;
            timedOut = true;
            return false;
        }

        Task.WaitAll([stdoutTask, stderrTask], 500);
        output = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
        error = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;
        exitCode = process.ExitCode;
        timedOut = false;
        return process.ExitCode == 0;
    }
}

internal static class LogicalDiskSampler
{
    private static readonly Dictionary<string, double> DiskBytes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, double> DiskReadBytes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, double> DiskWriteBytes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, double> DiskIops = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, double> DiskReadIops = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, double> DiskWriteIops = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, double> Queue = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, double> Latency = new(StringComparer.OrdinalIgnoreCase);

    public static void Refresh()
    {
        Replace(DiskBytes, PdhWildcardReader.Read(@"\LogicalDisk(*)\Disk Bytes/sec", NormalizeInstance));
        Replace(DiskReadBytes, PdhWildcardReader.Read(@"\LogicalDisk(*)\Disk Read Bytes/sec", NormalizeInstance));
        Replace(DiskWriteBytes, PdhWildcardReader.Read(@"\LogicalDisk(*)\Disk Write Bytes/sec", NormalizeInstance));
        Replace(DiskIops, PdhWildcardReader.Read(@"\LogicalDisk(*)\Disk Transfers/sec", NormalizeInstance));
        Replace(DiskReadIops, PdhWildcardReader.Read(@"\LogicalDisk(*)\Disk Reads/sec", NormalizeInstance));
        Replace(DiskWriteIops, PdhWildcardReader.Read(@"\LogicalDisk(*)\Disk Writes/sec", NormalizeInstance));
        Replace(Queue, PdhWildcardReader.Read(@"\LogicalDisk(*)\Current Disk Queue Length", NormalizeInstance));
        Replace(Latency, PdhWildcardReader.Read(@"\LogicalDisk(*)\Avg. Disk sec/Transfer", NormalizeInstance)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value * 1000, StringComparer.OrdinalIgnoreCase));
    }

    public static double TotalMbps(string drive) => Read(DiskBytes, drive) / 1024 / 1024;
    public static double ReadMbps(string drive) => Read(DiskReadBytes, drive) / 1024 / 1024;
    public static double WriteMbps(string drive) => Read(DiskWriteBytes, drive) / 1024 / 1024;
    public static double TotalIops(string drive) => Read(DiskIops, drive);
    public static double ReadIops(string drive) => Read(DiskReadIops, drive);
    public static double WriteIops(string drive) => Read(DiskWriteIops, drive);
    public static double QueueDepth(string drive) => Read(Queue, drive);
    public static double LatencyMs(string drive) => Read(Latency, drive);

    private static void Replace(Dictionary<string, double> target, Dictionary<string, double> source)
    {
        target.Clear();
        foreach (var pair in source)
            target[pair.Key] = pair.Value;
    }

    private static double Read(Dictionary<string, double> values, string drive)
    {
        var key = NormalizeLookupKey(drive);
        if (values.TryGetValue(key, out var value) && !double.IsNaN(value))
            return value;

        foreach (var pair in values)
        {
            var candidate = NormalizeLookupKey(pair.Key);
            if (candidate.Equals(key, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(candidate + "\\", StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase))
                return !double.IsNaN(pair.Value) ? pair.Value : 0;
        }

        return 0;
    }

    private static string NormalizeInstance(string instance)
    {
        var value = NormalizeLookupKey(instance);
        if (value.Equals("_Total", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        if (value.StartsWith(@"C:\ClusterStorage\", StringComparison.OrdinalIgnoreCase))
            return StorageInventory.ResolveStorageKey(value);
        if (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':')
            return value.Length == 2 ? value[..2].ToUpperInvariant() : value;
        return value;
    }

    private static string NormalizeLookupKey(string value)
        => value.Trim().TrimEnd('\\');
}

internal static class StorageInventory
{
    private const string ClusterStorageRoot = @"C:\ClusterStorage";

    public static StorageEntry[] Enumerate()
    {
        var storages = new List<StorageEntry>();

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed && d.TotalSize > 0))
        {
            var root = drive.Name.TrimEnd('\\');
            var displayName = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? root : $"{root} {drive.VolumeLabel}";
            storages.Add(new StorageEntry(displayName, root, root, (ulong)drive.TotalSize, (ulong)drive.AvailableFreeSpace, false));
        }

        if (Directory.Exists(ClusterStorageRoot))
        {
            foreach (var dir in Directory.GetDirectories(ClusterStorageRoot))
            {
                var root = dir.TrimEnd('\\');
                if (!Native.TryGetDiskFreeSpace(root + "\\", out var freeBytes, out var totalBytes) || totalBytes == 0)
                    continue;

                storages.Add(new StorageEntry(root, root, root, totalBytes, freeBytes, true));
            }
        }

        return storages
            .DistinctBy(s => s.CounterKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s.IsClusterSharedVolume ? 0 : 1)
            .ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string ResolveStorageKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var full = path.Trim().Trim('"').TrimEnd('\\');
        if (full.StartsWith(ClusterStorageRoot + "\\", StringComparison.OrdinalIgnoreCase))
        {
            var relative = full[ClusterStorageRoot.Length..].TrimStart('\\');
            var firstSegment = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(firstSegment))
                return $@"{ClusterStorageRoot}\{firstSegment}";
        }

        return Path.GetPathRoot(full)?.TrimEnd('\\') ?? string.Empty;
    }
}

internal sealed record StorageEntry(string DisplayName, string CounterKey, string MatchRoot, ulong TotalBytes, ulong FreeBytes, bool IsClusterSharedVolume);

internal static class PdhWildcardReader
{
    public static Dictionary<string, int> LastInstanceCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, double> Read(string wildcardPath, Func<string, string>? normalizeInstance = null)
    {
        normalizeInstance ??= NormalizeDefaultInstance;
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        LastInstanceCounts.Clear();
        string[] paths;
        try
        {
            paths = Native.ExpandWildcardPath(wildcardPath);
        }
        catch
        {
            return result;
        }

        if (paths.Length == 0) return result;

        using var query = new PdhQuery();
        var counters = new List<Counter>();
        foreach (var path in paths)
        {
            try
            {
                counters.Add(query.Add(path));
            }
            catch
            {
                // Some wildcard expansions can include transient instances. Ignore them.
            }
        }

        if (counters.Count == 0) return result;

        try
        {
            query.Collect();
            Thread.Sleep(100);
            query.Collect();
        }
        catch
        {
            return result;
        }

        foreach (var counter in counters)
        {
            var instance = normalizeInstance(ExtractInstance(counter.Path));
            if (string.IsNullOrWhiteSpace(instance)) continue;
            LastInstanceCounts[instance] = LastInstanceCounts.TryGetValue(instance, out var count) ? count + 1 : 1;
            var value = counter.Read();
            if (double.IsNaN(value)) continue;
            result[instance] = result.TryGetValue(instance, out var prior) ? prior + value : value;
        }

        return result;
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

internal sealed class NetworkSampler
{
    private readonly ConcurrentDictionary<string, InterfaceCounterSnapshot> previous = new();

    public AdapterRate[] Sample()
    {
        var now = DateTime.UtcNow;
        var pdhRates = NetworkPdhSampler.Read();
        return ReadInterfaceRows()
            .Where(row => row.Type != Native.IF_TYPE_SOFTWARE_LOOPBACK)
            .Select(row => TryRead(row, now, pdhRates, out var rate) ? rate : null)
            .Where(rate => rate is not null)
            .Cast<AdapterRate>()
            .ToArray();
    }

    private bool TryRead(MibIfRow2 row, DateTime now, NetworkPdhRate[] pdhRates, out AdapterRate rate)
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
        var rx = PdhWildcardReader.Read(@"\Network Interface(*)\Bytes Received/sec", NormalizeInstance);
        var tx = PdhWildcardReader.Read(@"\Network Interface(*)\Bytes Sent/sec", NormalizeInstance);
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
        var total = PdhWildcardReader.Read(totalPath, NormalizeInstance);
        var rx = PdhWildcardReader.Read(rxPath, NormalizeInstance);
        var tx = PdhWildcardReader.Read(txPath, NormalizeInstance);
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

internal sealed record AdapterRate(string Name, string Description, string InterfaceId, long LinkSpeedBitsPerSecond, bool IsUp, bool IsHardwareInterface, bool IsVisibleAdapter, double ReceivedBytesPerSecond, double SentBytesPerSecond, double RawReceivedBytesPerSecond, double RawSentBytesPerSecond, string PdhInstance, double PdhReceivedBytesPerSecond, double PdhSentBytesPerSecond, double DropsPerSecond)
{
    public double TotalBytesPerSecond => ReceivedBytesPerSecond + SentBytesPerSecond;
}

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

internal static class ElevationChecker
{
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}

internal static class CapacityFormatter
{
    public static string FormatCapacity(double bytes)
    {
        if (bytes <= 0) return "n/a";
        var teb = bytes / 1024d / 1024d / 1024d / 1024d;
        if (teb >= 1) return $"{teb:0.00} TB";
        var gib = bytes / 1024d / 1024d / 1024d;
        return $"{gib:0.0} GB";
    }

    public static string FormatConfigCapacity(double bytes)
    {
        if (bytes <= 0) return "n/a";
        var teb = bytes / 1024d / 1024d / 1024d / 1024d;
        if (teb >= 1) return $"{teb:0.0} TB";
        var gib = bytes / 1024d / 1024d / 1024d;
        return $"{Math.Round(gib, MidpointRounding.AwayFromZero):0} GB";
    }

}

internal static class UptimeFormatter
{
    public static string FormatShort(TimeSpan? uptime)
    {
        if (uptime is null)
            return "n/a";

        var value = uptime.Value < TimeSpan.Zero ? TimeSpan.Zero : uptime.Value;
        var minutes = Math.Floor(value.TotalMinutes);
        if (minutes <= 120)
            return Clamp4($"{Math.Max(0, (int)minutes)}m");

        var hours = value.TotalHours;
        if (hours <= 24)
            return Clamp4(FormatCompactUnit(hours, "h"));

        var days = value.TotalDays;
        if (days <= 365)
            return Clamp4(FormatCompactUnit(days, "d"));

        return Clamp4(FormatCompactUnit(days / 365d, "y"));
    }

    public static string FormatExact(TimeSpan? uptime)
    {
        if (uptime is null)
            return "n/a";

        var value = uptime.Value < TimeSpan.Zero ? TimeSpan.Zero : uptime.Value;
        var totalDays = Math.Max(0, (int)Math.Floor(value.TotalDays));
        var years = totalDays / 365;
        var daysAfterYears = totalDays % 365;
        var months = daysAfterYears / 30;
        var days = daysAfterYears % 30;
        var parts = new[]
        {
            FormatUnit(years, "year"),
            FormatUnit(months, "month"),
            FormatUnit(days, "day"),
            FormatUnit(value.Hours, "hour"),
            FormatUnit(value.Minutes, "minute"),
            FormatUnit(value.Seconds, "second")
        };
        return $"{totalDays}:{value.Hours:00}:{value.Minutes:00}:{value.Seconds:00} ({string.Join(", ", parts)})";
    }

    private static string FormatCompactUnit(double value, string suffix)
    {
        if (value < 10)
        {
            var rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
            if (Math.Abs(rounded - Math.Round(rounded)) >= 0.05)
                return $"{rounded:0.0}{suffix}";
        }

        return $"{Math.Round(value, MidpointRounding.AwayFromZero):0}{suffix}";
    }

    private static string Clamp4(string value)
        => value.Length <= 4 ? value : value[..4];

    private static string FormatUnit(int value, string unit)
        => $"{value} {unit}{(value == 1 ? string.Empty : "s")}";
}

internal static class ReplicationFormatter
{
    public static string Display(string state, string health)
    {
        state = Normalize(state);
        health = Normalize(health);
        if (IsNotConfigured(state))
            return "N/A";

        if (!IsNotConfigured(health) && !IsNotConfigured(state))
            return $"{health} ({state})";

        return !IsNotConfigured(health) ? health : state;
    }

    public static string Status(string state, string health)
    {
        state = Normalize(state);
        health = Normalize(health);
        if (IsNotConfigured(state))
            return "N/A";

        var text = $"{health} {state}".Trim();
        if (ContainsAny(text, "Critical", "Error", "Failed", "Failover", "Suspended", "ResynchronizationRequired"))
            return "HOT";

        if (ContainsAny(text, "Warning", "InProgress", "Waiting", "ReadyForInitialReplication", "Resynchronizing"))
            return "BUSY";

        return "OK";
    }

    private static string Normalize(string value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static bool IsNotConfigured(string value)
        => string.IsNullOrWhiteSpace(value)
           || value.Equals("N/A", StringComparison.OrdinalIgnoreCase)
           || value.Equals("None", StringComparison.OrdinalIgnoreCase)
           || value.Equals("Disabled", StringComparison.OrdinalIgnoreCase)
           || value.Equals("NotApplicable", StringComparison.OrdinalIgnoreCase)
           || value.Equals("Not Applicable", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAny(string value, params string[] needles)
        => needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
}

internal sealed class RollingHistory
{
    private readonly TimeSpan window;
    private readonly Dictionary<string, Queue<HistoryPoint>> points = new();

    public RollingHistory(TimeSpan window) => this.window = window;

    public HostRow Apply(string key, HostRow row)
    {
        var values = Add(key, row.Metrics);
        return row with
        {
            Cpu = row.Cpu with { Max = values[nameof(row.Cpu)] },
            Mem = row.Mem with { Max = values[nameof(row.Mem)] },
            Io = row.Io with { Max = values[nameof(row.Io)] },
            Net = row.Net with { Max = values[nameof(row.Net)] }
        };
    }

    public VmRow Apply(string key, VmRow row)
    {
        var values = Add(key, row.Metrics);
        return row with
        {
            Cpu = row.Cpu with { Max = values[nameof(row.Cpu)] },
            Mem = row.Mem with { Max = values[nameof(row.Mem)] },
            Io = row.Io with { Max = values[nameof(row.Io)] },
            Net = row.Net with { Max = values[nameof(row.Net)] },
            Iops = row.Iops with { Max = values[nameof(row.Iops)] },
            Latency = row.Latency with { Max = values[nameof(row.Latency)] }
        };
    }

    public DiskRow Apply(string key, DiskRow row)
    {
        var values = Add(key, row.Metrics);
        var freeMin = points.TryGetValue(key, out var queue)
            ? queue.Select(p => p.Values[nameof(row.Free)]).Where(v => !double.IsNaN(v)).DefaultIfEmpty(row.Free.Current).Min()
            : row.Free.Current;
        return row with
        {
            Free = row.Free with { Max = freeMin },
            Io = row.Io with { Max = values[nameof(row.Io)] },
            ReadIo = row.ReadIo with { Max = values[nameof(row.ReadIo)] },
            WriteIo = row.WriteIo with { Max = values[nameof(row.WriteIo)] },
            Iops = row.Iops with { Max = values[nameof(row.Iops)] },
            ReadIops = row.ReadIops with { Max = values[nameof(row.ReadIops)] },
            WriteIops = row.WriteIops with { Max = values[nameof(row.WriteIops)] },
            QueueDepth = row.QueueDepth with { Max = values[nameof(row.QueueDepth)] },
            Latency = row.Latency with { Max = values[nameof(row.Latency)] }
        };
    }

    public NetworkRow Apply(string key, NetworkRow row)
    {
        var values = Add(key, row.Metrics);
        return row with
        {
            Throughput = row.Throughput with { Max = values[nameof(row.Throughput)] },
            Rx = row.Rx with { Max = values[nameof(row.Rx)] },
            Tx = row.Tx with { Max = values[nameof(row.Tx)] },
            Drops = row.Drops with { Max = values[nameof(row.Drops)] }
        };
    }

    public NetworkSwitchRow Apply(string key, NetworkSwitchRow row)
    {
        var values = Add(key, row.Metrics);
        return row with
        {
            Throughput = row.Throughput with { Max = values[nameof(row.Throughput)] },
            Rx = row.Rx with { Max = values[nameof(row.Rx)] },
            Tx = row.Tx with { Max = values[nameof(row.Tx)] },
            Drops = row.Drops with { Max = values[nameof(row.Drops)] }
        };
    }

    public VmTopologyRow Apply(string key, VmTopologyRow row)
    {
        return row with
        {
            Disks = row.Disks.Select(disk =>
            {
                var diskKey = $"{key}:vdisk:{disk.Path}:{disk.Name}";
                var values = Add(diskKey, disk.Metrics);
                return disk with
                {
                    TotalMbpsMax = values[nameof(disk.TotalMbps)],
                    TotalIopsMax = values[nameof(disk.TotalIops)],
                    ReadMbpsMax = values[nameof(disk.ReadMbps)],
                    ReadIopsMax = values[nameof(disk.ReadIops)],
                    WriteMbpsMax = values[nameof(disk.WriteMbps)],
                    WriteIopsMax = values[nameof(disk.WriteIops)]
                };
            }).ToArray(),
            Networks = row.Networks.Select(adapter =>
            {
                var adapterKey = $"{key}:vnic:{adapter.Name}:{adapter.SwitchName}";
                var values = Add(adapterKey, adapter.Metrics);
                return adapter with
                {
                    ThroughputMbpsMax = values[nameof(adapter.ThroughputMbps)],
                    RxMbpsMax = values[nameof(adapter.RxMbps)],
                    TxMbpsMax = values[nameof(adapter.TxMbps)]
                };
            }).ToArray()
        };
    }

    private Dictionary<string, double> Add(string key, IReadOnlyDictionary<string, double> values)
    {
        var now = DateTime.UtcNow;
        if (!points.TryGetValue(key, out var queue))
        {
            queue = new Queue<HistoryPoint>();
            points[key] = queue;
        }

        queue.Enqueue(new HistoryPoint(now, values));
        while (queue.Count > 0 && now - queue.Peek().At > window)
            queue.Dequeue();

        return values.Keys.ToDictionary(k => k, k => queue.Select(p => p.Values[k]).Where(v => !double.IsNaN(v)).DefaultIfEmpty(values[k]).Max());
    }

    private sealed record HistoryPoint(DateTime At, IReadOnlyDictionary<string, double> Values);
}

internal sealed record Snapshot(
    DateTime At,
    ClusterRow[] Clusters,
    HostRow[] Hosts,
    VmRow[] Vms,
    DiskRow[] Disks,
    NetworkSwitchRow[] NetworkSwitches,
    NetworkRow[] Networks,
    EventRow[] Events,
    VmTopologyRow[] VmTopology,
    bool Loading,
    bool InventoryRefreshing,
    bool TopologyRefreshing,
    DiscoveryProgress Discovery)
{
    public static Snapshot Empty { get; } = new(DateTime.Now, [], [], [], [], [], [], [], [], true, false, false, DiscoveryProgress.Empty);
}

internal sealed record DiscoveryProgress(
    bool HostsReady,
    bool VmsReady,
    bool StorageReady,
    bool NetworkReady,
    bool Complete,
    int VmCount,
    int StorageCount,
    int NetworkInterfaceCount,
    int NetworkSwitchCount)
{
    public static DiscoveryProgress Empty { get; } = new(false, false, false, false, false, 0, 0, 0, 0);
}

internal sealed record Metric(double Current, double Max, Unit Unit)
{
    public static Metric Percent(double value) => new(value, value, Unit.Percent);
    public static Metric Mbps(double value) => new(value, value, Unit.Mbps);
    public static Metric Iops(double value) => new(value, value, Unit.Iops);
    public static Metric Milliseconds(double value) => new(value, value, Unit.Milliseconds);
    public static Metric Plain(double value) => new(value, value, Unit.Plain);
}

internal enum Unit { Plain, Percent, Mbps, Iops, Milliseconds }

internal sealed record ClusterRow(string Name, int NodeCount, int UpNodeCount, string OwnerNode, string Quorum, string FunctionalLevel, string Status);

internal sealed record ClusterNodeRow(string Name, string State, string Status);

internal sealed record HostRow(string Name, string Version, TimeSpan? Uptime, Metric Cpu, string CpuCapacity, Metric Mem, string MemCapacity, Metric Io, Metric Net, string Status)
{
    public IReadOnlyDictionary<string, double> Metrics => new Dictionary<string, double>
    {
        [nameof(Cpu)] = Cpu.Current,
        [nameof(Mem)] = Mem.Current,
        [nameof(Io)] = Io.Current,
        [nameof(Net)] = Net.Current
    };
}

internal sealed record VmRow(string Name, string HostName, string Version, TimeSpan Uptime, bool IsRunning, string Replication, string ReplicationStatus, Metric Cpu, string CpuCapacity, Metric Mem, string MemCapacity, Metric Io, Metric Net, Metric Iops, Metric Latency, string Status)
{
    public IReadOnlyDictionary<string, double> Metrics => new Dictionary<string, double>
    {
        [nameof(Cpu)] = Cpu.Current,
        [nameof(Mem)] = Mem.Current,
        [nameof(Io)] = Io.Current,
        [nameof(Net)] = Net.Current,
        [nameof(Iops)] = Iops.Current,
        [nameof(Latency)] = Latency.Current
    };
}

internal sealed record DiskRow(string HostName, string Name, string Size, string UsedSpace, string FreeSpace, Metric Free, Metric Io, Metric ReadIo, Metric WriteIo, Metric Iops, Metric ReadIops, Metric WriteIops, Metric QueueDepth, Metric Latency, string Status)
{
    public IReadOnlyDictionary<string, double> Metrics => new Dictionary<string, double>
    {
        [nameof(Free)] = Free.Current,
        [nameof(Io)] = Io.Current,
        [nameof(ReadIo)] = ReadIo.Current,
        [nameof(WriteIo)] = WriteIo.Current,
        [nameof(Iops)] = Iops.Current,
        [nameof(ReadIops)] = ReadIops.Current,
        [nameof(WriteIops)] = WriteIops.Current,
        [nameof(QueueDepth)] = QueueDepth.Current,
        [nameof(Latency)] = Latency.Current
    };
}

internal sealed record NetworkRow(string HostName, string Name, string Description, string Link, bool IsUp, long LinkSpeedBitsPerSecond, Metric Throughput, Metric Rx, Metric Tx, Metric Drops, string Status, string PdhInstance = "", double RawReceivedBytesPerSecond = 0, double RawSentBytesPerSecond = 0, double PdhReceivedBytesPerSecond = 0, double PdhSentBytesPerSecond = 0)
{
    public IReadOnlyDictionary<string, double> Metrics => new Dictionary<string, double>
    {
        [nameof(Throughput)] = Throughput.Current,
        [nameof(Rx)] = Rx.Current,
        [nameof(Tx)] = Tx.Current,
        [nameof(Drops)] = Drops.Current
    };
}

internal sealed record NetworkUplinkInfo(string Name, string Description, string Link, bool IsUp, long LinkSpeedBitsPerSecond);

internal sealed record NetworkSwitchRow(string HostName, string Name, string SwitchType, string TeamMode, NetworkUplinkInfo[] Uplinks, string Link, Metric Throughput, Metric Rx, Metric Tx, Metric Drops, string Status)
{
    public IReadOnlyDictionary<string, double> Metrics => new Dictionary<string, double>
    {
        [nameof(Throughput)] = Throughput.Current,
        [nameof(Rx)] = Rx.Current,
        [nameof(Tx)] = Tx.Current,
        [nameof(Drops)] = Drops.Current
    };
}

internal sealed record EventRow(DateTime At, string Severity, string Message);

internal sealed record VDiskRow(
    string Name,
    string Path,
    string StorageName,
    double ReadMbps,
    double ReadIops,
    double WriteMbps,
    double WriteIops,
    double TotalMbpsMax = double.NaN,
    double TotalIopsMax = double.NaN,
    double ReadMbpsMax = double.NaN,
    double ReadIopsMax = double.NaN,
    double WriteMbpsMax = double.NaN,
    double WriteIopsMax = double.NaN)
{
    public double TotalMbps => ReadMbps + WriteMbps;
    public double TotalIops => ReadIops + WriteIops;
    public IReadOnlyDictionary<string, double> Metrics => new Dictionary<string, double>
    {
        [nameof(TotalMbps)] = TotalMbps,
        [nameof(TotalIops)] = TotalIops,
        [nameof(ReadMbps)] = ReadMbps,
        [nameof(ReadIops)] = ReadIops,
        [nameof(WriteMbps)] = WriteMbps,
        [nameof(WriteIops)] = WriteIops
    };
}

internal sealed record VmNetworkPathRow(
    string Name,
    string SwitchName,
    string PhysicalAdapterName,
    double RxMbps = 0,
    double TxMbps = 0,
    double ThroughputMbpsMax = double.NaN,
    double RxMbpsMax = double.NaN,
    double TxMbpsMax = double.NaN)
{
    public double ThroughputMbps => RxMbps + TxMbps;
    public IReadOnlyDictionary<string, double> Metrics => new Dictionary<string, double>
    {
        [nameof(ThroughputMbps)] = ThroughputMbps,
        [nameof(RxMbps)] = RxMbps,
        [nameof(TxMbps)] = TxMbps
    };
}

internal sealed record VDiskDetailRow(string HostName, string VmName, VDiskRow Disk);

internal sealed record VmNetworkDetailRow(string HostName, string VmName, VmNetworkPathRow Adapter, NetworkSwitchRow? Switch);

internal sealed record NetworkSwitchTopologyRow(
    string Name,
    string SwitchType,
    NetworkUplinkInfo[] Uplinks,
    string TeamMode = "");

internal sealed record VmTopologyRow(
    string VmName,
    VDiskRow[] Disks,
    VmNetworkPathRow[] Networks,
    string HostName = "");

internal static class Status
{
    public static string From(double cpu, double mem, double latency, double queueDepth)
    {
        if (cpu < 5 && mem < 15 && latency < 3) return "IDLE";
        if (cpu >= 85 || mem >= 90 || latency >= 25 || queueDepth >= 16) return "HOT";
        if (cpu >= 70 || mem >= 75 || latency >= 12 || queueDepth >= 8) return "BUSY";
        return "OK";
    }

    public static string FromNetwork(double throughputBytesPerSecond, long linkSpeedBitsPerSecond, bool isUp)
    {
        if (!isUp)
            return "OFF";

        if (throughputBytesPerSecond <= 0.01)
            return "IDLE";

        if (linkSpeedBitsPerSecond > 0)
        {
            var utilization = throughputBytesPerSecond * 8d / linkSpeedBitsPerSecond;
            if (utilization >= 0.80) return "HOT";
            if (utilization >= 0.50) return "BUSY";
        }
        else if (throughputBytesPerSecond > 300 * 1024d * 1024d)
        {
            return "BUSY";
        }

        return "OK";
    }
}

internal sealed class Tui
{
    private readonly AppState state;
    private readonly Options options;
    private const string ColGap = "    ";
    private const int MaxNameWidth = 32;
    private const int MinNameWidth = 20;
    private const int CapacityMetricWidth = 25;
    private const int MetricWidth = 21;
    private const int ShortMetricWidth = 13;
    private const int HostVersionWidth = 28;
    private const int VmVersionWidth = 7;
    private const int HostColumnWidth = 14;
    private const int UptimeWidth = 4;
    private const int CountWidth = 5;
    private const int OwnerWidth = 18;
    private const int QuorumWidth = 14;
    private const int SizeWidth = 10;
    private const int UplinkWidth = 3;
    private const int LinkWidth = 6;
    private const int StatusWidth = 6;
    private Panel panel = Panel.Hosts;
    private int selected;
    private DrillView drillView = DrillView.Overview;
    private Panel detailPanel;
    private string? selectedHostName;
    private string? selectedItemName;
    private readonly Stack<ViewState> backStack = new();
    private readonly Dictionary<string, SortState> sortStates = new(StringComparer.OrdinalIgnoreCase);
    private string[] previousLines = [];
    private ConsoleColor[] previousForegrounds = [];
    private ConsoleColor[] previousBackgrounds = [];
    private bool[] touchedLines = [];
    private int previousWidth;
    private int previousHeight;
    private int frameWidth;
    private int frameHeight;

    public Tui(AppState state, Options options)
    {
        this.state = state;
        this.options = options;
    }

    public void Run(CancellationTokenSource cts)
    {
        Console.CursorVisible = false;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                Render();
                var until = DateTime.UtcNow.AddMilliseconds(120);
                while (DateTime.UtcNow < until)
                {
                    if (Console.KeyAvailable)
                    {
                        Handle(Console.ReadKey(true), cts);
                        break;
                    }
                    Thread.Sleep(15);
                }
            }
        }
        finally
        {
            Console.ResetColor();
            Console.CursorVisible = true;
            Console.Clear();
        }
    }

    private void Handle(ConsoleKeyInfo key, CancellationTokenSource cts)
    {
        switch (key.Key)
        {
            case ConsoleKey.Q:
                cts.Cancel();
                return;
            case ConsoleKey.C:
                SetPanel(Panel.Cluster);
                return;
            case ConsoleKey.H:
                SetPanel(Panel.Hosts);
                return;
            case ConsoleKey.V:
                SetPanel(Panel.Vms);
                return;
            case ConsoleKey.D:
                SetPanel(Panel.Disks);
                return;
            case ConsoleKey.N:
                SetPanel(Panel.Network);
                return;
            case ConsoleKey.E:
                SetPanel(Panel.Events);
                return;
            case ConsoleKey.R:
                state.RequestRefresh();
                state.AddEvent("INFO", "Inventory/topology refresh requested");
                return;
            case ConsoleKey.F:
                var refresh = state.CycleRefresh((key.Modifiers & ConsoleModifiers.Shift) != 0);
                state.AddEvent("INFO", $"Refresh delay set to {refresh.TotalSeconds:N1}s");
                return;
            case ConsoleKey.S:
                CycleSort((key.Modifiers & ConsoleModifiers.Shift) != 0);
                return;
            case ConsoleKey.UpArrow:
                selected = Math.Max(0, selected - 1);
                return;
            case ConsoleKey.DownArrow:
                selected++;
                return;
            case ConsoleKey.PageUp:
                selected = Math.Max(0, selected - PageSize());
                return;
            case ConsoleKey.PageDown:
                selected = Math.Min(Math.Max(0, CurrentRows().Count - 1), selected + PageSize());
                return;
            case ConsoleKey.Home:
                selected = 0;
                return;
            case ConsoleKey.End:
                selected = Math.Max(0, CurrentRows().Count - 1);
                return;
            case ConsoleKey.Enter:
                if (drillView == DrillView.Detail)
                    OpenDetailSelection();
                else
                    OpenSelected();
                return;
            case ConsoleKey.Backspace:
            case ConsoleKey.Escape:
                GoBack();
                return;
        }

        if (key.KeyChar == 'j') selected++;
        if (key.KeyChar == 'k') selected = Math.Max(0, selected - 1);
    }

    private int PageSize() => Math.Max(1, Console.WindowHeight - 8);

    private void SetPanel(Panel next)
    {
        panel = next;
        selected = 0;
        drillView = DrillView.Overview;
        selectedHostName = null;
        selectedItemName = null;
        backStack.Clear();
    }

    private void CycleSort(bool reverse)
    {
        var columns = SortColumns().ToArray();
        if (columns.Length == 0) return;

        var key = SortViewKey();
        var current = SortStateFor(key, columns);
        SortState next;
        if (reverse)
        {
            next = current with { Descending = !current.Descending };
        }
        else
        {
            var index = Array.FindIndex(columns, c => c.Key.Equals(current.Column, StringComparison.OrdinalIgnoreCase));
            index = index < 0 ? 0 : (index + 1) % columns.Length;
            next = new SortState(columns[index].Key, DefaultDescending(columns[index].Key));
        }

        sortStates[key] = next;
        selected = 0;
        var label = columns.First(c => c.Key.Equals(next.Column, StringComparison.OrdinalIgnoreCase)).Label;
        state.AddEvent("INFO", $"Sort set to {label} {(next.Descending ? "desc" : "asc")}");
    }

    private string CurrentSortLabel()
    {
        var columns = SortColumns().ToArray();
        if (columns.Length == 0) return "n/a";

        var current = SortStateFor(SortViewKey(), columns);
        var label = columns.First(c => c.Key.Equals(current.Column, StringComparison.OrdinalIgnoreCase)).Label;
        return $"{label} {(current.Descending ? "desc" : "asc")}";
    }

    private void OpenSelected()
    {
        if (panel == Panel.Events) return;
        var rows = CurrentRows();
        if (rows.Count == 0) return;
        selected = Math.Min(selected, rows.Count - 1);
        var row = rows[selected];

        if (panel == Panel.Cluster && row is ClusterRow)
        {
            PushView();
            panel = Panel.Hosts;
            selected = 0;
            drillView = DrillView.Overview;
            selectedHostName = null;
            selectedItemName = null;
            return;
        }

        if (panel == Panel.Hosts && drillView == DrillView.Overview && row is HostRow host)
        {
            PushView();
            selectedHostName = host.Name;
            selectedItemName = host.Name;
            detailPanel = Panel.Hosts;
            selected = 0;
            drillView = DrillView.Detail;
            return;
        }

        if (panel == Panel.Network && drillView == DrillView.Overview && row is NetworkSwitchRow networkSwitch)
        {
            PushView();
            selectedHostName = networkSwitch.HostName;
            selectedItemName = networkSwitch.Name;
            selected = 0;
            drillView = DrillView.NetworkAdapters;
            return;
        }

        PushView();
        selectedItemName = GetRowName(row);
        selectedHostName = row switch
        {
            VmRow vmRow => vmRow.HostName,
            DiskRow diskRow => diskRow.HostName,
            NetworkSwitchRow switchRow => switchRow.HostName,
            NetworkRow networkRow => networkRow.HostName,
            _ => selectedHostName
        };
        detailPanel = row is VmRow ? Panel.Vms : panel;
        drillView = DrillView.Detail;
        selected = 0;
    }

    private void OpenDetailSelection()
    {
        var target = ResolveDetailTarget();
        if (target is HostRow host)
        {
            var hostSnapshot = state.Read();
            var rows = HostDetailRows(hostSnapshot, host.Name);
            if (rows.Length == 0) return;
            selected = Math.Clamp(selected, 0, rows.Length - 1);
            var row = rows[selected];
            PushView();

            switch (row)
            {
                case VmRow vmRow:
                    panel = Panel.Vms;
                    detailPanel = Panel.Vms;
                    drillView = DrillView.Detail;
                    selectedHostName = vmRow.HostName;
                    selectedItemName = vmRow.Name;
                    selected = 0;
                    return;
                case DiskRow diskRow:
                    panel = Panel.Disks;
                    detailPanel = Panel.Disks;
                    drillView = DrillView.Detail;
                    selectedHostName = diskRow.HostName;
                    selectedItemName = diskRow.Name;
                    selected = 0;
                    return;
                case NetworkSwitchRow switchRow:
                    panel = Panel.Network;
                    drillView = DrillView.NetworkAdapters;
                    selectedHostName = switchRow.HostName;
                    selectedItemName = switchRow.Name;
                    selected = 0;
                    return;
            }

            PopView();
            return;
        }

        if (target is not VmRow vm) return;

        var snapshot = state.Read();
        var disks = GetVmDisks(vm, snapshot);
        var adapters = GetVmNetworkAdapters(vm, snapshot);
        var totalRows = disks.Length + adapters.Length;
        if (totalRows == 0) return;

        selected = Math.Clamp(selected, 0, totalRows - 1);
        PushView();

        if (selected < disks.Length)
        {
            panel = Panel.Vms;
            detailPanel = Panel.Disks;
            drillView = DrillView.Detail;
            selectedHostName = vm.HostName;
            selectedItemName = EncodeVmChild(vm.Name, "vdisk", disks[selected].Name);
            selected = 0;
            return;
        }

        var adapter = adapters[selected - disks.Length];
        panel = Panel.Vms;
        detailPanel = Panel.Network;
        drillView = DrillView.Detail;
        selectedHostName = vm.HostName;
        selectedItemName = EncodeVmChild(vm.Name, "vnic", adapter.Name);
        selected = 0;
    }

    private static string EncodeVmChild(string vmName, string childType, string childName)
        => $"{vmName}\0{childType}\0{childName}";

    private static bool TryDecodeVmChild(string? value, out string vmName, out string childType, out string childName)
    {
        vmName = string.Empty;
        childType = string.Empty;
        childName = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Split('\0');
        if (parts.Length != 3)
            return false;

        vmName = parts[0];
        childType = parts[1];
        childName = parts[2];
        return !string.IsNullOrWhiteSpace(vmName) && !string.IsNullOrWhiteSpace(childType) && !string.IsNullOrWhiteSpace(childName);
    }

    private static int FindStorageIndex(Snapshot snapshot, VDiskRow disk, string hostName)
    {
        var exact = Array.FindIndex(snapshot.Disks, d => d.HostName.Equals(hostName, StringComparison.OrdinalIgnoreCase) && d.Name.Equals(disk.StorageName, StringComparison.OrdinalIgnoreCase));
        if (exact >= 0) return exact;

        var root = Path.GetPathRoot(disk.Path)?.TrimEnd('\\') ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(root))
        {
            var byRoot = Array.FindIndex(snapshot.Disks, d => d.HostName.Equals(hostName, StringComparison.OrdinalIgnoreCase) && d.Name.StartsWith(root, StringComparison.OrdinalIgnoreCase));
            if (byRoot >= 0) return byRoot;
        }

        return -1;
    }

    private static int FindNetworkSwitchIndex(Snapshot snapshot, string switchName, string hostName)
        => Array.FindIndex(snapshot.NetworkSwitches, n => n.HostName.Equals(hostName, StringComparison.OrdinalIgnoreCase) && n.Name.Equals(switchName, StringComparison.OrdinalIgnoreCase));

    private void GoBack()
    {
        if (PopView()) return;

        drillView = DrillView.Overview;
        selectedHostName = null;
        selectedItemName = null;
    }

    private void PushView()
    {
        backStack.Push(new ViewState(panel, selected, drillView, detailPanel, selectedHostName, selectedItemName));
    }

    private bool PopView()
    {
        if (backStack.Count == 0) return false;
        var prior = backStack.Pop();
        panel = prior.Panel;
        selected = prior.Selected;
        drillView = prior.DrillView;
        detailPanel = prior.DetailPanel;
        selectedHostName = prior.SelectedHostName;
        selectedItemName = prior.SelectedItemName;
        return true;
    }

    private IReadOnlyList<object> CurrentRows()
    {
        var s = state.Read();
        IReadOnlyList<object> rows;
        if (drillView == DrillView.Detail && ResolveDetailTarget() is HostRow host)
            rows = HostDetailRows(s, host.Name);
        else if (drillView == DrillView.Detail && ResolveDetailTarget() is VmRow vm)
            rows = GetVmDisks(vm, s).Cast<object>().Concat(GetVmNetworkAdapters(vm, s)).ToArray();
        else if (drillView == DrillView.Detail && ResolveDetailTarget() is VDiskDetailRow or VmNetworkDetailRow)
            rows = [];
        else if (panel == Panel.Network && drillView == DrillView.NetworkAdapters)
            rows = GetSwitchUplinkRows(s, selectedHostName, selectedItemName).Cast<object>().ToArray();
        else
        {
            rows = panel switch
            {
                Panel.Cluster => s.Clusters,
                Panel.Hosts => s.Hosts,
                Panel.Vms => s.Vms,
                Panel.Disks => s.Disks,
                Panel.Network => s.NetworkSwitches,
                Panel.Events => s.Events,
                _ => []
            };
        }

        return ApplySort(rows).ToArray();
    }

    private IEnumerable<object> ApplySort(IReadOnlyList<object> rows)
    {
        var columns = SortColumns().ToArray();
        if (columns.Length == 0 || rows.Count <= 1)
            return rows;

        var state = SortStateFor(SortViewKey(), columns);
        var sortable = rows.Select(row => new { Row = row, Value = SortValue(row, state.Column) }).ToArray();
        var valid = sortable.Where(item => !IsMissingSortValue(item.Value)).ToArray();
        var missing = sortable.Where(item => IsMissingSortValue(item.Value)).Select(item => item.Row).OrderBy(GetRowName, StringComparer.OrdinalIgnoreCase);
        return state.Descending
            ? valid.OrderByDescending(item => item.Value, SortValueComparer.Instance)
                .ThenBy(item => GetRowName(item.Row), StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Row)
                .Concat(missing)
            : valid.OrderBy(item => item.Value, SortValueComparer.Instance)
                .ThenBy(item => GetRowName(item.Row), StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Row)
                .Concat(missing);
    }

    private SortState SortStateFor(string key, SortColumn[] columns)
    {
        if (sortStates.TryGetValue(key, out var state) && columns.Any(c => c.Key.Equals(state.Column, StringComparison.OrdinalIgnoreCase)))
            return state;

        var column = columns[0].Key;
        state = new SortState(column, DefaultDescending(column));
        sortStates[key] = state;
        return state;
    }

    private string SortViewKey()
    {
        if (panel == Panel.Hosts && drillView == DrillView.HostVms) return "HostVms";
        if (panel == Panel.Network && drillView == DrillView.NetworkAdapters) return "NetworkAdapters";
        return panel.ToString();
    }

    private IEnumerable<SortColumn> SortColumns()
    {
        if (panel == Panel.Hosts && drillView == DrillView.HostVms)
            return VmSortColumns();
        if (panel == Panel.Network && drillView == DrillView.NetworkAdapters)
            return [new("NAME", "name"), new("LINK", "link"), new("THR", "throughput"), new("RX", "rx"), new("TX", "tx"), new("DROPS", "drops"), new("STA", "status")];

        return panel switch
        {
            Panel.Cluster => [new("NAME", "name"), new("NODES", "nodes"), new("UP", "up"), new("OWNER", "owner"), new("QUORUM", "quorum"), new("STA", "status")],
            Panel.Hosts => [new("NAME", "name"), new("VER", "version"), new("UP", "uptime"), new("CPU", "cpu"), new("MEM", "memory"), new("IO", "i/o"), new("NET", "network"), new("STA", "status")],
            Panel.Vms => VmSortColumns(),
            Panel.Disks => [new("HOST", "host"), new("NAME", "name"), new("SIZE", "size"), new("FREE", "free"), new("IO", "i/o"), new("IOPS", "iops"), new("QD", "queue"), new("LAT", "latency"), new("STA", "status")],
            Panel.Network => [new("HOST", "host"), new("NAME", "name"), new("UPL", "uplinks"), new("LINK", "link"), new("THR", "throughput"), new("RX", "rx"), new("TX", "tx"), new("STA", "status")],
            Panel.Events => [new("DATE", "date"), new("SEV", "severity"), new("WHAT", "message")],
            _ => []
        };
    }

    private static SortColumn[] VmSortColumns()
        => [new("HOST", "host"), new("NAME", "name"), new("VER", "version"), new("UP", "uptime"), new("CPU", "cpu"), new("MEM", "memory"), new("IO", "i/o"), new("NET", "network"), new("IOPS", "iops"), new("LAT", "latency"), new("STA", "status")];

    private static bool DefaultDescending(string column)
        => column is "CPU" or "MEM" or "IO" or "NET" or "IOPS" or "QD" or "LAT" or "SIZE" or "NODES" or "UP" or "THR" or "RX" or "TX" or "DROPS" or "DATE";

    private static object? SortValue(object row, string column) => row switch
    {
        ClusterRow cluster => column switch
        {
            "NAME" => cluster.Name,
            "NODES" => cluster.NodeCount,
            "UP" => cluster.UpNodeCount,
            "OWNER" => cluster.OwnerNode,
            "QUORUM" => cluster.Quorum,
            "STA" => StatusRank(cluster.Status),
            _ => cluster.Name
        },
        HostRow host => column switch
        {
            "NAME" => host.Name,
            "VER" => host.Version,
            "UP" => host.Uptime?.TotalSeconds ?? double.NaN,
            "CPU" => host.Cpu.Current,
            "MEM" => host.Mem.Current,
            "IO" => host.Io.Current,
            "NET" => host.Net.Current,
            "STA" => StatusRank(host.Status),
            _ => host.Name
        },
        VmRow vm => column switch
        {
            "HOST" => vm.HostName,
            "NAME" => vm.Name,
            "VER" => vm.Version,
            "UP" => vm.Uptime.TotalSeconds,
            "CPU" => vm.Cpu.Current,
            "MEM" => vm.Mem.Current,
            "IO" => vm.Io.Current,
            "NET" => vm.Net.Current,
            "IOPS" => vm.Iops.Current,
            "LAT" => vm.Latency.Current,
            "STA" => StatusRank(vm.Status),
            _ => vm.Name
        },
        DiskRow disk => column switch
        {
            "HOST" => disk.HostName,
            "NAME" => disk.Name,
            "SIZE" => ParseCapacity(disk.Size),
            "FREE" => disk.Free.Current,
            "IO" => disk.Io.Current,
            "IOPS" => disk.Iops.Current,
            "QD" => disk.QueueDepth.Current,
            "LAT" => disk.Latency.Current,
            "STA" => StatusRank(disk.Status),
            _ => disk.Name
        },
        NetworkSwitchRow network => column switch
        {
            "HOST" => network.HostName,
            "NAME" => network.Name,
            "UPL" => network.Uplinks.Length,
            "LINK" => ParseLink(network.Link),
            "THR" => network.Throughput.Current,
            "RX" => network.Rx.Current,
            "TX" => network.Tx.Current,
            "STA" => StatusRank(network.Status),
            _ => network.Name
        },
        NetworkRow net => column switch
        {
            "HOST" => net.HostName,
            "NAME" => net.Name,
            "LINK" => ParseLink(net.Link),
            "THR" => net.Throughput.Current,
            "RX" => net.Rx.Current,
            "TX" => net.Tx.Current,
            "DROPS" => net.Drops.Current,
            "STA" => StatusRank(net.Status),
            _ => net.Name
        },
        EventRow evt => column switch
        {
            "DATE" => evt.At,
            "SEV" => EventSeverityRank(evt.Severity),
            "WHAT" => evt.Message,
            _ => evt.At
        },
        _ => null
    };

    private static int StatusRank(string status) => status.ToUpperInvariant() switch
    {
        "HOT" => 4,
        "BUSY" => 3,
        "OK" => 2,
        "IDLE" => 1,
        "OFF" => 0,
        _ => 0
    };

    private static int EventSeverityRank(string severity) => severity.ToUpperInvariant() switch
    {
        "ERR" => 3,
        "WARN" => 2,
        "INFO" => 1,
        _ => 0
    };

    private static bool IsMissingSortValue(object? value)
        => value is null || (value is double number && double.IsNaN(number));

    private static double ParseCapacity(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !double.TryParse(parts[0], out var number))
            return 0;

        var multiplier = parts.Length > 1 ? parts[1].ToUpperInvariant() switch
        {
            "KB" => 1024d,
            "MB" => 1024d * 1024,
            "GB" => 1024d * 1024 * 1024,
            "TB" => 1024d * 1024 * 1024 * 1024,
            _ => 1d
        } : 1d;
        return number * multiplier;
    }

    private static double ParseLink(string value)
    {
        if (value.Equals("DOWN", StringComparison.OrdinalIgnoreCase)) return 0;
        if (value.StartsWith("2x", StringComparison.OrdinalIgnoreCase)) return 2 * ParseLink(value[2..]);
        if (value.EndsWith("100G", StringComparison.OrdinalIgnoreCase)) return 100;
        if (value.EndsWith("40G", StringComparison.OrdinalIgnoreCase)) return 40;
        if (value.EndsWith("25G", StringComparison.OrdinalIgnoreCase)) return 25;
        if (value.EndsWith("10G", StringComparison.OrdinalIgnoreCase)) return 10;
        if (value.Equals("GbE", StringComparison.OrdinalIgnoreCase)) return 1;
        if (value.Equals("FE", StringComparison.OrdinalIgnoreCase)) return 0.1;
        return 0;
    }

    private sealed class SortValueComparer : IComparer<object?>
    {
        public static SortValueComparer Instance { get; } = new();

        public int Compare(object? x, object? y)
        {
            if (x is null && y is null) return 0;
            if (x is null) return 1;
            if (y is null) return -1;

            if (x is double dx)
            {
                var dy = y is double yDouble ? yDouble : 0;
                if (double.IsNaN(dx) && double.IsNaN(dy)) return 0;
                if (double.IsNaN(dx)) return 1;
                if (double.IsNaN(dy)) return -1;
                return dx.CompareTo(dy);
            }

            if (x is int ix && y is int iy) return ix.CompareTo(iy);
            if (x is DateTime tx && y is DateTime ty) return tx.CompareTo(ty);
            return string.Compare(x.ToString(), y.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private void Render()
    {
        var s = state.Read();
        BeginFrame();
        var sort = CurrentSortLabel();
        WriteLine(0, $"{Program.AppName} {Program.DisplayVersion}  Sample: {s.At:HH:mm:ss}  Refresh: {state.Refresh.TotalSeconds:N1}s  History: {options.History.TotalMinutes:N0}m  Sort: {sort}", ConsoleColor.White);
        WriteLine(1, Nav(), ConsoleColor.Yellow);
        WriteLine(2, "Arrows/j/k move  PgUp/PgDn page  Home/End top/bottom  Enter drill  s sort  S dir  f refresh  r rescan  q quit", ConsoleColor.DarkGray);

        if (IsLoading(s))
        {
            RenderLoadingOverlay(s);
            RenderDiscoveryStatus(s);
            EndFrame();
            Console.ResetColor();
            return;
        }

        if (drillView == DrillView.Detail) RenderDetail();
        else RenderTable();
        RenderDiscoveryStatus(s);
        EndFrame();
        Console.ResetColor();
    }

    private string Nav()
    {
        return string.Join("  ", new[]
        {
            panel == Panel.Cluster ? "[C] CLUSTER" : " C  CLUSTER",
            panel == Panel.Hosts ? "[H] HOSTS" : " H  HOSTS",
            panel == Panel.Vms ? "[V] VMS" : " V  VMS",
            panel == Panel.Disks ? "[D] CSV / STORAGE" : " D  CSV / STORAGE",
            panel == Panel.Network ? "[N] NETWORK" : " N  NETWORK",
            panel == Panel.Events ? "[E] EVENTS" : " E  EVENTS"
        });
    }

    private void RenderLoadingOverlay(Snapshot snapshot)
    {
        var discovery = snapshot.Discovery;
        WriteLine(5, "Please wait, discovering inventory and topology....", ConsoleColor.DarkGray);
        WriteLine(7, LoadingStatusLine("Hosts", discovery.HostsReady), LoadingStatusColor(discovery.HostsReady));
        WriteLine(8, LoadingStatusLine("VMs", discovery.VmsReady, discovery.VmsReady ? $"{discovery.VmCount} found" : null), LoadingStatusColor(discovery.VmsReady));
        WriteLine(9, LoadingStatusLine("Storage", discovery.StorageReady, discovery.StorageReady ? $"{discovery.StorageCount} target(s)" : null), LoadingStatusColor(discovery.StorageReady));
        WriteLine(10, LoadingStatusLine("Network", discovery.NetworkReady, discovery.NetworkReady ? $"{discovery.NetworkSwitchCount} switch(es)" : null), LoadingStatusColor(discovery.NetworkReady));
    }

    private static string LoadingStatusLine(string area, bool ready, string? detail = null)
        => $"  {area,-8} {(ready ? "ready" : "working...")}{(string.IsNullOrWhiteSpace(detail) ? string.Empty : "  " + detail)}";

    private static ConsoleColor LoadingStatusColor(bool ready)
        => ready ? ConsoleColor.Green : ConsoleColor.DarkGray;

    private void RenderDiscoveryStatus(Snapshot snapshot)
    {
        if (frameHeight <= 0)
            return;

        var discovery = snapshot.Discovery;
        var text = discovery.Complete
            ? $"Discovery complete: hosts ready, VMs {discovery.VmCount}, storage {discovery.StorageCount}, network {discovery.NetworkSwitchCount} target(s)"
            : "Discovery: "
              + StatusPart("hosts", discovery.HostsReady)
              + "  "
              + StatusPart("VMs", discovery.VmsReady, discovery.VmsReady ? discovery.VmCount.ToString() : null)
              + "  "
              + StatusPart("storage", discovery.StorageReady, discovery.StorageReady ? discovery.StorageCount.ToString() : null)
              + "  "
              + StatusPart("network", discovery.NetworkReady, discovery.NetworkReady ? discovery.NetworkSwitchCount.ToString() : null);

        if (snapshot.InventoryRefreshing || snapshot.TopologyRefreshing)
            text += $"  ({(snapshot.InventoryRefreshing ? "inventory " : string.Empty)}{(snapshot.TopologyRefreshing ? "topology " : string.Empty)}refreshing)";

        WriteLine(frameHeight - 1, text.TrimEnd(), discovery.Complete ? ConsoleColor.DarkGray : ConsoleColor.Yellow);
    }

    private static string StatusPart(string label, bool ready, string? detail = null)
        => ready
            ? $"{label}: ready{(string.IsNullOrWhiteSpace(detail) ? string.Empty : $" {detail}")}"
            : $"{label}: working...";

    private void RenderTable()
    {
        switch (panel)
        {
            case Panel.Cluster:
                {
                    var nameWidth = TableNameWidth(TableKind.ClusterLike);
                    RenderRows(
                        Row(Header("CLUSTER", nameWidth), HeaderRight("NODES", CountWidth), HeaderRight("UP", CountWidth), Header("OWNER", OwnerWidth), Header("QUORUM", QuorumWidth), Header("STA", StatusWidth)),
                        CurrentRows().Cast<ClusterRow>().ToArray(),
                        r => Row(Cell(DisplayName(r.Name, nameWidth), nameWidth), Cell(r.NodeCount.ToString(), CountWidth, true), Cell(r.UpNodeCount.ToString(), CountWidth, true), Cell(DisplayName(r.OwnerNode, OwnerWidth), OwnerWidth), Cell(DisplayName(r.Quorum, QuorumWidth), QuorumWidth), Cell(r.Status, StatusWidth)));
                }
                break;
            case Panel.Hosts:
                if (drillView == DrillView.HostVms)
                {
                    var nameWidth = TableNameWidth(TableKind.VmLike);
                    RenderRows(
                        Row(Header(DisplayName($"HOST {selectedHostName} -> VMS", nameWidth), nameWidth), Header("VER", VmVersionWidth), HeaderRight("UP", UptimeWidth), CapacityMetricGroupHeader("CPU"), CapacityMetricGroupHeader("MEM"), GroupHeader("I/O", MetricWidth), GroupHeader("NET", MetricWidth), Header("STA", StatusWidth)),
                        Row(Header(string.Empty, nameWidth), Header(string.Empty, VmVersionWidth), Header(string.Empty, UptimeWidth), CapacityMetricSubHeader(), CapacityMetricSubHeader(), MetricSubHeader(), MetricSubHeader(), Header(string.Empty, StatusWidth)),
                        CurrentRows().Cast<VmRow>().ToArray(),
                        r => Row(Cell(DisplayName(r.Name, nameWidth), nameWidth), Cell(r.Version, VmVersionWidth), Cell(r.IsRunning ? UptimeFormatter.FormatShort(r.Uptime) : "OFF", UptimeWidth, true), FmtWithCapacity(r.Cpu, r.CpuCapacity), FmtWithCapacity(r.Mem, r.MemCapacity), Fmt(r.Io), Fmt(r.Net), Cell(r.Status, StatusWidth)));
                }
                else
                {
                    var nameWidth = TableNameWidth(TableKind.HostLike);
                    RenderRows(
                        Row(Header("HOSTNAME", nameWidth), Header("VER", HostVersionWidth), HeaderRight("UP", UptimeWidth), CapacityMetricGroupHeader("CPU"), CapacityMetricGroupHeader("MEM"), GroupHeader("I/O", MetricWidth), GroupHeader("NET", MetricWidth), Header("STA", StatusWidth)),
                        Row(Header(string.Empty, nameWidth), Header(string.Empty, HostVersionWidth), Header(string.Empty, UptimeWidth), CapacityMetricSubHeader(), CapacityMetricSubHeader(), MetricSubHeader(), MetricSubHeader(), Header(string.Empty, StatusWidth)),
                        CurrentRows().Cast<HostRow>().ToArray(),
                        r => Row(Cell(DisplayName(r.Name, nameWidth), nameWidth), Cell(r.Version, HostVersionWidth), Cell(UptimeFormatter.FormatShort(r.Uptime), UptimeWidth, true), FmtWithCapacity(r.Cpu, r.CpuCapacity), FmtWithCapacity(r.Mem, r.MemCapacity), Fmt(r.Io), Fmt(r.Net), Cell(r.Status, StatusWidth)));
                }
                break;
            case Panel.Vms:
                {
                    var nameWidth = TableNameWidth(TableKind.VmLike);
                    RenderRows(
                        Row(Header("HOST", HostColumnWidth), Header("NAME", nameWidth), Header("VER", VmVersionWidth), HeaderRight("UP", UptimeWidth), CapacityMetricGroupHeader("CPU"), CapacityMetricGroupHeader("MEM"), GroupHeader("I/O", MetricWidth), GroupHeader("NET", MetricWidth), Header("STA", StatusWidth)),
                        Row(Header(string.Empty, HostColumnWidth), Header(string.Empty, nameWidth), Header(string.Empty, VmVersionWidth), Header(string.Empty, UptimeWidth), CapacityMetricSubHeader(), CapacityMetricSubHeader(), MetricSubHeader(), MetricSubHeader(), Header(string.Empty, StatusWidth)),
                        CurrentRows().Cast<VmRow>().ToArray(),
                        r => Row(Cell(DisplayName(r.HostName, HostColumnWidth), HostColumnWidth), Cell(DisplayName(r.Name, nameWidth), nameWidth), Cell(r.Version, VmVersionWidth), Cell(r.IsRunning ? UptimeFormatter.FormatShort(r.Uptime) : "OFF", UptimeWidth, true), FmtWithCapacity(r.Cpu, r.CpuCapacity), FmtWithCapacity(r.Mem, r.MemCapacity), Fmt(r.Io), Fmt(r.Net), Cell(r.Status, StatusWidth)));
                }
                break;
            case Panel.Disks:
                {
                    var nameWidth = TableNameWidth(TableKind.DiskLike);
                    RenderRows(
                        Row(Header("HOST", HostColumnWidth), Header("NAME", nameWidth), HeaderRight("SIZE", SizeWidth), GroupHeader("FREE", MetricWidth), GroupHeader("I/O", MetricWidth), GroupHeader("IOPS", MetricWidth), GroupHeader("QD", ShortMetricWidth), GroupHeader("LAT", ShortMetricWidth), Header("STA", StatusWidth)),
                        Row(Header(string.Empty, HostColumnWidth), Header(string.Empty, nameWidth), Header(string.Empty, SizeWidth), FreeMetricSubHeader(), MetricSubHeader(), MetricSubHeader(), ShortMetricSubHeader(), ShortMetricSubHeader(), Header(string.Empty, StatusWidth)),
                        CurrentRows().Cast<DiskRow>().ToArray(),
                        r => Row(Cell(DisplayName(r.HostName, HostColumnWidth), HostColumnWidth), Cell(DisplayName(r.Name, nameWidth), nameWidth), Cell(r.Size, SizeWidth, true), Fmt(r.Free), Fmt(r.Io), Fmt(r.Iops), FmtShort(r.QueueDepth), FmtShort(r.Latency), Cell(r.Status, StatusWidth)));
                }
                break;
            case Panel.Network:
                {
                    if (drillView == DrillView.NetworkAdapters)
                    {
                        var nameWidth = TableNameWidth(TableKind.NetworkLike);
                        var switchName = CurrentNetworkSwitchDisplayName();
                        RenderRows(
                            Row(Header(DisplayName($"HOST {selectedHostName} -> VSWITCH {switchName} -> PNICS", nameWidth), nameWidth), Header("LINK", LinkWidth), GroupHeader("THR", MetricWidth), GroupHeader("RX", MetricWidth), GroupHeader("TX", MetricWidth), GroupHeader("DROPS", ShortMetricWidth), Header("STA", StatusWidth)),
                            Row(Header(string.Empty, nameWidth), Header(string.Empty, LinkWidth), MetricSubHeader(), MetricSubHeader(), MetricSubHeader(), ShortMetricSubHeader(), Header(string.Empty, StatusWidth)),
                            CurrentRows().Cast<NetworkRow>().ToArray(),
                            r => Row(Cell(DisplayName(r.Name, nameWidth), nameWidth), Cell(r.Link, LinkWidth), Fmt(r.Throughput), Fmt(r.Rx), Fmt(r.Tx), FmtShort(r.Drops), Cell(r.Status, StatusWidth)));
                    }
                    else
                    {
                        var nameWidth = TableNameWidth(TableKind.NetworkSwitchLike);
                        RenderRows(
                            Row(Header("HOST", HostColumnWidth), Header("VSWITCH", nameWidth), Header("UPL", UplinkWidth), Header("LINK", LinkWidth), GroupHeader("THR", MetricWidth), GroupHeader("RX", MetricWidth), GroupHeader("TX", MetricWidth), Header("STA", StatusWidth)),
                            Row(Header(string.Empty, HostColumnWidth), Header(string.Empty, nameWidth), Header(string.Empty, UplinkWidth), Header(string.Empty, LinkWidth), MetricSubHeader(), MetricSubHeader(), MetricSubHeader(), Header(string.Empty, StatusWidth)),
                            CurrentRows().Cast<NetworkSwitchRow>().ToArray(),
                            r => Row(Cell(DisplayName(r.HostName, HostColumnWidth), HostColumnWidth), Cell(DisplayName(NetworkSwitchDisplayName(r), nameWidth), nameWidth), Cell(r.Uplinks.Length.ToString(), UplinkWidth, true), Cell(r.Link, LinkWidth), Fmt(r.Throughput), Fmt(r.Rx), Fmt(r.Tx), Cell(r.Status, StatusWidth)));
                    }
                }
                break;
            case Panel.Events:
                RenderRows(Row(Header("DATE", 19), Header("SEV", 5), Header("WHAT JUST HAPPENED", 80)),
                    CurrentRows().Cast<EventRow>().ToArray(), r => Row(Cell($"{r.At:yyyy-MM-dd HH:mm:ss}", 19), Cell(r.Severity, 5), r.Message));
                break;
        }
    }

    private static string Row(params string[] cells) => string.Join(ColGap, cells);

    private static string Header(string text, int width) => Cell(text, width);

    private static string HeaderRight(string text, int width) => Cell(text, width, true);

    private static string GroupHeader(string label, int width)
    {
        if (label.Length >= width) return label[..width];
        var padLeft = (width - label.Length) / 2;
        var padRight = width - label.Length - padLeft;
        return new string(' ', padLeft) + label + new string(' ', padRight);
    }

    private static string CapacityMetricGroupHeader(string label)
        => Cell(new string(' ', 7) + Cell(label, 4), CapacityMetricWidth);

    private static string MetricSubHeader() => FixedMetricHeaderCell("cur", "max", valueWidth: 9, width: MetricWidth);

    private static string FreeMetricSubHeader() => FixedMetricHeaderCell("cur", "min", valueWidth: 9, width: MetricWidth);

    private static string ShortMetricSubHeader() => FixedMetricHeaderCell("cur", "max", valueWidth: 5, width: ShortMetricWidth);

    private static string CapacityMetricSubHeader() => FixedMetricHeaderCell("cur", "max", "cfg", currentWidth: 4, maxWidth: 4, configWidth: 11, width: CapacityMetricWidth);

    private static string Cell(string text, int width, bool alignRight = false)
    {
        if (text.Length > width)
            text = text[..width];
        return alignRight ? text.PadLeft(width) : text.PadRight(width);
    }

    private static string DisplayName(string name, int width)
    {
        if (name.Length <= width) return name;
        if (width < 12) return name[..width];
        var edge = Math.Max(4, (width - 4) / 2);
        return $"{name[..edge]}....{name[^edge..]}";
    }

    private static string VmDiskHeaderRow()
        => "  " + string.Join("  ", new[]
        {
            Cell("vDisks", 32),
            Cell("Storage/CSV", 26),
            Cell("Read", 10),
            Cell("Read IOPS", 10),
            Cell("Write", 10),
            Cell("Write IOPS", 10)
        });

    private static string VmDiskDataRow(VDiskRow disk)
        => "  " + string.Join("  ", new[]
        {
            Cell(DisplayName(disk.Name, 32), 32),
            Cell(DisplayName(disk.StorageName, 26), 26),
            Cell(FmtValue(disk.ReadMbps, Unit.Mbps), 10),
            Cell(FmtValue(disk.ReadIops, Unit.Iops), 10),
            Cell(FmtValue(disk.WriteMbps, Unit.Mbps), 10),
            Cell(FmtValue(disk.WriteIops, Unit.Iops), 10)
        });

    private static string VmNetworkHeaderRow()
        => "  " + string.Join("  ", new[]
        {
            Cell("Network", 32),
            Cell("vSwitch", 26),
            Cell("pNIC", 32)
        });

    private static string VmNetworkDataRow(VmNetworkPathRow adapter)
        => "  " + string.Join("  ", new[]
        {
            Cell(DisplayName(adapter.Name, 32), 32),
            Cell(DisplayName(adapter.SwitchName, 26), 26),
            Cell(DisplayName(adapter.PhysicalAdapterName, 32), 32)
        });

    private static string HostVmHeaderRow()
        => "  " + string.Join("  ", new[]
        {
            Cell("VMs", 32),
            Cell("UP", 4),
            Cell("CPU", 10),
            Cell("MEM", 10),
            Cell("I/O", 10),
            Cell("STA", 6)
        });

    private static string HostVmDataRow(VmRow vm)
        => "  " + string.Join("  ", new[]
        {
            Cell(DisplayName(vm.Name, 32), 32),
            Cell(vm.IsRunning ? UptimeFormatter.FormatShort(vm.Uptime) : "OFF", 4, true),
            Cell(FmtValue(vm.Cpu.Current, vm.Cpu.Unit), 10),
            Cell(FmtValue(vm.Mem.Current, vm.Mem.Unit), 10),
            Cell(FmtValue(vm.Io.Current, vm.Io.Unit), 10),
            Cell(vm.Status, 6)
        });

    private static string HostDiskHeaderRow()
        => "  " + string.Join("  ", new[]
        {
            Cell("Storage", 32),
            Cell("FREE", 10),
            Cell("I/O", 10),
            Cell("IOPS", 10),
            Cell("LAT", 10),
            Cell("STA", 6)
        });

    private static string HostDiskDataRow(DiskRow disk)
        => "  " + string.Join("  ", new[]
        {
            Cell(DisplayName(disk.Name, 32), 32),
            Cell(FmtValue(disk.Free.Current, disk.Free.Unit), 10),
            Cell(FmtValue(disk.Io.Current, disk.Io.Unit), 10),
            Cell(FmtValue(disk.Iops.Current, disk.Iops.Unit), 10),
            Cell(FmtValue(disk.Latency.Current, disk.Latency.Unit), 10),
            Cell(disk.Status, 6)
        });

    private static string HostNetworkHeaderRow()
        => "  " + string.Join("  ", new[]
        {
            Cell("Network", 32),
            Cell("LINK", 6),
            Cell("THR", 10),
            Cell("RX", 10),
            Cell("TX", 10),
            Cell("STA", 6)
        });

    private static string HostNetworkDataRow(NetworkSwitchRow network)
        => "  " + string.Join("  ", new[]
        {
            Cell(DisplayName(NetworkSwitchDisplayName(network), 32), 32),
            Cell(network.Link, 6),
            Cell(FmtValue(network.Throughput.Current, network.Throughput.Unit), 10),
            Cell(FmtValue(network.Rx.Current, network.Rx.Unit), 10),
            Cell(FmtValue(network.Tx.Current, network.Tx.Unit), 10),
            Cell(network.Status, 6)
        });

    private string CurrentNetworkSwitchDisplayName()
    {
        var snapshot = state.Read();
        var row = snapshot.NetworkSwitches.FirstOrDefault(n => n.Name.Equals(selectedItemName ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && n.HostName.Equals(selectedHostName ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        return row is null ? selectedItemName ?? string.Empty : NetworkSwitchDisplayName(row);
    }

    private static string NetworkSwitchDisplayName(NetworkSwitchRow network)
    {
        var type = ShortSwitchType(network.SwitchType);
        if (string.IsNullOrWhiteSpace(type))
            return network.Name;

        if (string.IsNullOrWhiteSpace(network.TeamMode))
            return $"{network.Name} ({type})";

        return $"{network.Name} ({type}/{network.TeamMode})";
    }

    private static string ShortSwitchType(string switchType)
        => switchType.Trim().ToUpperInvariant() switch
        {
            "EXTERNAL" => "External",
            "INTERNAL" => "Internal",
            "PRIVATE" => "Private",
            _ => switchType.Trim()
        };

    private void RenderRows<T>(string header, string subHeader, IReadOnlyList<T> rows, Func<T, string> formatter)
    {
        WriteLine(4, header, ConsoleColor.Cyan);
        WriteLine(5, subHeader, ConsoleColor.DarkCyan);
        selected = Math.Min(selected, Math.Max(0, rows.Count - 1));
        var maxRows = Math.Max(0, Console.WindowHeight - 8);
        for (var i = 0; i < Math.Min(rows.Count, maxRows); i++)
        {
            var background = i == selected ? ConsoleColor.DarkCyan : ConsoleColor.Black;
            var row = rows[i];
            var foreground = i == selected ? ConsoleColor.White : (row is null ? ConsoleColor.Gray : RowColor(row));
            WriteLine(6 + i, formatter(row), foreground, background);
        }
    }

    private void RenderRows<T>(string header, IReadOnlyList<T> rows, Func<T, string> formatter)
    {
        WriteLine(4, header, ConsoleColor.Cyan);
        selected = Math.Min(selected, Math.Max(0, rows.Count - 1));
        var maxRows = Math.Max(0, Console.WindowHeight - 7);
        for (var i = 0; i < Math.Min(rows.Count, maxRows); i++)
        {
            var background = i == selected ? ConsoleColor.DarkCyan : ConsoleColor.Black;
            var row = rows[i];
            var foreground = i == selected ? ConsoleColor.White : (row is null ? ConsoleColor.Gray : RowColor(row));
            WriteLine(5 + i, formatter(row), foreground, background);
        }
    }

    private void RenderDetail()
    {
        var detailTarget = ResolveDetailTarget();
        if (detailTarget is null)
        {
            GoBack();
            return;
        }

        WriteLine(4, DetailTitle(detailTarget), ConsoleColor.Cyan);
        WriteLine(5, new string('-', Math.Min(90, Console.WindowWidth)), ConsoleColor.DarkGray);

        switch (detailTarget)
        {
            case ClusterRow cluster:
                Detail(7, "Name", cluster.Name);
                Detail(8, "Nodes", $"{cluster.UpNodeCount}/{cluster.NodeCount} up");
                Detail(9, "Owner", cluster.OwnerNode);
                Detail(10, "Quorum", cluster.Quorum);
                Detail(11, "Functional level", cluster.FunctionalLevel);
                Detail(13, "Status", cluster.Status, StatusColor(cluster.Status));
                break;
            case VmRow vm:
                var vmDisks = GetVmDisks(vm, state.Read());
                var vmAdapters = GetVmNetworkAdapters(vm, state.Read());
                selected = Math.Min(selected, Math.Max(0, vmDisks.Length + vmAdapters.Length - 1));
                Detail(7, "Name", vm.Name);
                Detail(8, "Uptime", vm.IsRunning ? UptimeFormatter.FormatExact(vm.Uptime) : "Powered off");
                Detail(9, "Replication status", vm.Replication, ReplicationColor(vm.ReplicationStatus));
                DetailScalar(10, string.Empty, string.Empty);
                DetailMetricWithCapacity(11, "CPU", vm.Cpu, vm.CpuCapacity);
                DetailMetricWithCapacity(12, "Memory", vm.Mem, vm.MemCapacity);
                DetailScalar(13, string.Empty, string.Empty);
                DetailMetric(14, "Total IO", vm.Io);
                DetailMetricSplit(15, "  Read", vm.Io, 0.25);
                DetailMetricSplit(16, "  Write", vm.Io, 0.75);
                DetailScalar(17, string.Empty, string.Empty);
                DetailMetric(18, "Total IOPS", vm.Iops);
                DetailMetricSplit(19, "  Read IOPS", vm.Iops, 0.25);
                DetailMetricSplit(20, "  Write IOPS", vm.Iops, 0.75);
                DetailScalar(21, string.Empty, string.Empty);
                DetailMetric(22, "Latency", vm.Latency);
                Detail(24, "Status", vm.Status, StatusColor(vm.Status));
                WriteLine(27, VmDiskHeaderRow(), ConsoleColor.Yellow);
                for (var i = 0; i < vmDisks.Length; i++)
                {
                    var disk = vmDisks[i];
                    var row = VmDiskDataRow(disk);
                    var bg = i == selected ? ConsoleColor.DarkCyan : ConsoleColor.Black;
                    var fg = i == selected ? ConsoleColor.White : ConsoleColor.Gray;
                    WriteLine(28 + i, row, fg, bg);
                }
                var networkTop = 30 + vmDisks.Length;
                WriteLine(networkTop, VmNetworkHeaderRow(), ConsoleColor.Yellow);
                for (var i = 0; i < vmAdapters.Length; i++)
                {
                    var adapter = vmAdapters[i];
                    var absoluteIndex = vmDisks.Length + i;
                    var row = VmNetworkDataRow(adapter);
                    var bg = absoluteIndex == selected ? ConsoleColor.DarkCyan : ConsoleColor.Black;
                    var fg = absoluteIndex == selected ? ConsoleColor.White : ConsoleColor.Gray;
                    WriteLine(networkTop + 1 + i, row, fg, bg);
                }
                break;
            case HostRow host:
                var hostSnapshot = state.Read();
                var hostVms = hostSnapshot.Vms.Where(v => v.HostName.Equals(host.Name, StringComparison.OrdinalIgnoreCase)).OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                var hostDisks = hostSnapshot.Disks.Where(d => d.HostName.Equals(host.Name, StringComparison.OrdinalIgnoreCase)).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                var hostNetworks = hostSnapshot.NetworkSwitches.Where(n => n.HostName.Equals(host.Name, StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                var selectableRows = hostVms.Length + hostDisks.Length + hostNetworks.Length;
                selected = Math.Min(selected, Math.Max(0, selectableRows - 1));
                Detail(7, "Name", host.Name);
                Detail(8, "Version", host.Version);
                Detail(9, "Uptime", UptimeFormatter.FormatExact(host.Uptime));
                DetailMetricWithCapacity(10, "CPU", host.Cpu, host.CpuCapacity);
                DetailMetricWithCapacity(11, "Memory", host.Mem, host.MemCapacity);
                DetailMetric(12, "I/O", host.Io);
                DetailMetric(13, "Network", host.Net);
                Detail(15, "Status", host.Status, StatusColor(host.Status));
                var y = 18;
                var absolute = 0;
                WriteLine(y++, HostVmHeaderRow(), ConsoleColor.Yellow);
                foreach (var vmRow in hostVms)
                {
                    WriteSelectableDetailLine(y++, absolute++, HostVmDataRow(vmRow), vmRow);
                }

                y++;
                WriteLine(y++, HostDiskHeaderRow(), ConsoleColor.Yellow);
                foreach (var diskRow in hostDisks)
                {
                    WriteSelectableDetailLine(y++, absolute++, HostDiskDataRow(diskRow), diskRow);
                }

                y++;
                WriteLine(y++, HostNetworkHeaderRow(), ConsoleColor.Yellow);
                foreach (var networkRow in hostNetworks)
                {
                    WriteSelectableDetailLine(y++, absolute++, HostNetworkDataRow(networkRow), networkRow);
                }
                break;
            case VDiskDetailRow vdisk:
                var virtualDisk = vdisk.Disk;
                Detail(7, "Name", virtualDisk.Name);
                Detail(8, "VM", vdisk.VmName);
                Detail(9, "Host", vdisk.HostName);
                Detail(10, "Storage/CSV", virtualDisk.StorageName);
                Detail(11, "Path", virtualDisk.Path);
                DetailScalar(12, string.Empty, string.Empty);
                DetailMetricHeader(13, string.Empty, "cur", "max");
                DetailMetric(14, "Total I/O", Metric.Mbps(virtualDisk.TotalMbps) with { Max = DetailMax(virtualDisk.TotalMbpsMax, virtualDisk.TotalMbps) });
                DetailMetric(15, "    ├ Read I/O", Metric.Mbps(virtualDisk.ReadMbps) with { Max = DetailMax(virtualDisk.ReadMbpsMax, virtualDisk.ReadMbps) });
                DetailMetric(16, "    └ Write I/O", Metric.Mbps(virtualDisk.WriteMbps) with { Max = DetailMax(virtualDisk.WriteMbpsMax, virtualDisk.WriteMbps) });
                DetailScalar(17, string.Empty, string.Empty);
                DetailMetricHeader(18, string.Empty, "cur", "max");
                DetailMetric(19, "Total IOPS", Metric.Iops(virtualDisk.TotalIops) with { Max = DetailMax(virtualDisk.TotalIopsMax, virtualDisk.TotalIops) });
                DetailMetric(20, "    ├ Read IOPS", Metric.Iops(virtualDisk.ReadIops) with { Max = DetailMax(virtualDisk.ReadIopsMax, virtualDisk.ReadIops) });
                DetailMetric(21, "    └ Write IOPS", Metric.Iops(virtualDisk.WriteIops) with { Max = DetailMax(virtualDisk.WriteIopsMax, virtualDisk.WriteIops) });
                Detail(23, "Status", virtualDisk.TotalMbps <= 0.01 && virtualDisk.TotalIops <= 0.01 ? "IDLE" : "OK", virtualDisk.TotalMbps <= 0.01 && virtualDisk.TotalIops <= 0.01 ? ConsoleColor.Green : ConsoleColor.Gray);
                break;
            case VmNetworkDetailRow vnic:
                var virtualNic = vnic.Adapter;
                var linkBits = vnic.Switch is null ? 0 : NetworkLinkFormatter.ParseBitsPerSecond(vnic.Switch.Link);
                var vnicStatus = Status.FromNetwork(virtualNic.ThroughputMbps * 1024d * 1024d, linkBits, true);
                Detail(7, "Name", virtualNic.Name);
                Detail(8, "VM", vnic.VmName);
                Detail(9, "Host", vnic.HostName);
                Detail(10, "vSwitch", virtualNic.SwitchName);
                Detail(11, "pNIC", virtualNic.PhysicalAdapterName);
                Detail(12, "Link", vnic.Switch?.Link ?? "n/a");
                DetailScalar(13, string.Empty, string.Empty);
                DetailMetricHeader(14, string.Empty, "cur", "max");
                DetailMetric(15, "Throughput", Metric.Mbps(virtualNic.ThroughputMbps) with { Max = DetailMax(virtualNic.ThroughputMbpsMax, virtualNic.ThroughputMbps) });
                DetailMetric(16, "    ├ Transmit", Metric.Mbps(virtualNic.TxMbps) with { Max = DetailMax(virtualNic.TxMbpsMax, virtualNic.TxMbps) });
                DetailMetric(17, "    └ Receive", Metric.Mbps(virtualNic.RxMbps) with { Max = DetailMax(virtualNic.RxMbpsMax, virtualNic.RxMbps) });
                Detail(19, "Status", vnicStatus, StatusColor(vnicStatus));
                break;
            case DiskRow disk:
                Detail(7, "Name", disk.Name);
                Detail(8, "Host", disk.HostName);
                Detail(9, "Size", disk.Size);
                DetailScalar(10, "  ├ Used space", disk.UsedSpace);
                DetailScalar(11, "  └ Free space", disk.FreeSpace);
                DetailScalar(12, string.Empty, string.Empty);
                DetailMetricHeader(13, string.Empty, "cur", "min");
                DetailMetric(14, "Free", disk.Free);
                DetailScalar(15, string.Empty, string.Empty);
                DetailMetricHeader(16, string.Empty, "cur", "max");
                DetailMetric(17, "Total I/O", disk.Io);
                DetailMetric(18, "    ├ Read I/O", disk.ReadIo);
                DetailMetric(19, "    └ Write I/O", disk.WriteIo);
                DetailScalar(20, string.Empty, string.Empty);
                DetailMetricHeader(21, string.Empty, "cur", "max");
                DetailMetric(22, "Total IOPS", disk.Iops);
                DetailMetric(23, "    ├ Read IOPS", disk.ReadIops);
                DetailMetric(24, "    └ Write IOPS", disk.WriteIops);
                DetailScalar(25, string.Empty, string.Empty);
                DetailMetricHeader(26, string.Empty, "cur", "max");
                DetailMetric(27, "Queue depth", disk.QueueDepth);
                DetailMetric(28, "Latency", disk.Latency);
                Detail(30, "Status", disk.Status, StatusColor(disk.Status));
                break;
            case NetworkRow net:
                Detail(7, "Name", net.Name);
                Detail(8, "Host", net.HostName);
                Detail(9, "Link", net.Link);
                DetailScalar(10, string.Empty, string.Empty);
                DetailMetricHeader(11, string.Empty, "cur", "max");
                DetailMetric(12, "Throughput", net.Throughput);
                DetailMetric(13, "    ├ Transmit", net.Tx);
                DetailMetric(14, "    └ Receive", net.Rx);
                DetailScalar(15, string.Empty, string.Empty);
                DetailMetricHeader(16, string.Empty, "cur", "max");
                DetailMetric(17, "Drops", net.Drops);
                Detail(19, "Status", net.Status, StatusColor(net.Status));
                break;
        }
    }

    private const int DetailLabelWidth = 19;

    private void Detail(int y, string label, string value, ConsoleColor color = ConsoleColor.Gray)
        => WriteLine(y, $"  {label,-DetailLabelWidth} {value}", color);

    private void DetailMetric(int y, string label, Metric metric)
        => DetailScalar(y, label, DetailMetricValue(metric));

    private void DetailMetricHeader(int y, string label, string currentLabel, string maxLabel)
        => DetailScalar(y, label, DetailMetricHeaderValue(currentLabel, maxLabel), ConsoleColor.DarkCyan);

    private void DetailMetricWithCapacity(int y, string label, Metric metric, string capacity)
        => DetailScalar(y, label, DetailMetricWithCapacityValue(metric, capacity));

    private void DetailMetricSplit(int y, string label, Metric metric, double ratio)
        => DetailScalar(y, label, DetailSplitValue(metric, ratio));

    private static double DetailMax(double max, double current)
        => double.IsNaN(max) ? current : Math.Max(max, current);

    private void DetailScalar(int y, string label, string value, ConsoleColor color = ConsoleColor.Gray)
        => WriteLine(y, $"  {label,-DetailLabelWidth} {value}", color);

    private void WriteSelectableDetailLine(int y, int index, string text, object row)
    {
        var isSelected = index == selected;
        WriteLine(y, text, isSelected ? ConsoleColor.White : RowColor(row), isSelected ? ConsoleColor.DarkCyan : ConsoleColor.Black);
    }

    private static object[] HostDetailRows(Snapshot snapshot, string hostName)
    {
        return snapshot.Vms
            .Where(v => v.HostName.Equals(hostName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Cast<object>()
            .Concat(snapshot.Disks
                .Where(d => d.HostName.Equals(hostName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            .Concat(snapshot.NetworkSwitches
                .Where(n => n.HostName.Equals(hostName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
            .ToArray();
    }

    private object? ResolveDetailTarget()
    {
        if (string.IsNullOrEmpty(selectedItemName)) return null;
        var s = state.Read();
        if (TryDecodeVmChild(selectedItemName, out var vmName, out var childType, out var childName))
        {
            var vm = s.Vms.FirstOrDefault(r => r.Name.Equals(vmName, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrEmpty(selectedHostName) || r.HostName.Equals(selectedHostName, StringComparison.OrdinalIgnoreCase)));
            if (vm is null)
                return null;

            if (childType.Equals("vdisk", StringComparison.OrdinalIgnoreCase))
            {
                var disk = GetVmDisks(vm, s).FirstOrDefault(r => r.Name.Equals(childName, StringComparison.OrdinalIgnoreCase));
                return disk is null ? null : new VDiskDetailRow(vm.HostName, vm.Name, disk);
            }

            if (childType.Equals("vnic", StringComparison.OrdinalIgnoreCase))
            {
                var adapter = GetVmNetworkAdapters(vm, s).FirstOrDefault(r => r.Name.Equals(childName, StringComparison.OrdinalIgnoreCase));
                if (adapter is null)
                    return null;

                var switchRow = s.NetworkSwitches.FirstOrDefault(n => n.HostName.Equals(vm.HostName, StringComparison.OrdinalIgnoreCase)
                    && n.Name.Equals(adapter.SwitchName, StringComparison.OrdinalIgnoreCase));
                return new VmNetworkDetailRow(vm.HostName, vm.Name, adapter, switchRow);
            }
        }

        return detailPanel switch
        {
            Panel.Cluster => s.Clusters.FirstOrDefault(r => r.Name == selectedItemName),
            Panel.Hosts => s.Hosts.FirstOrDefault(r => r.Name == selectedItemName),
            Panel.Vms => s.Vms.FirstOrDefault(r => r.Name == selectedItemName && (string.IsNullOrEmpty(selectedHostName) || r.HostName == selectedHostName)),
            Panel.Disks => s.Disks.FirstOrDefault(r => r.Name == selectedItemName && (string.IsNullOrEmpty(selectedHostName) || r.HostName == selectedHostName)),
            Panel.Network => s.Networks.FirstOrDefault(r => r.Name == selectedItemName && (string.IsNullOrEmpty(selectedHostName) || r.HostName == selectedHostName)),
            _ => null
        };
    }

    private string DetailTitle(object row)
    {
        if (row is VmRow vm)
            return $"HOST {vm.HostName} -> VM {vm.Name}";
        if (panel == Panel.Cluster && row is HostRow host)
            return $"CLUSTER -> HOST {host.Name}";
        if (panel == Panel.Network && row is NetworkRow net && !string.IsNullOrWhiteSpace(selectedHostName))
            return $"HOST {net.HostName} -> pNIC {net.Name}";
        if (row is VDiskDetailRow vdisk)
            return $"HOST {vdisk.HostName} -> VM {vdisk.VmName} -> vDisk {vdisk.Disk.Name}";
        if (row is VmNetworkDetailRow vnic)
            return $"HOST {vnic.HostName} -> VM {vnic.VmName} -> vNIC {vnic.Adapter.Name}";
        if (row is DiskRow disk)
            return $"HOST {disk.HostName} -> storage {disk.Name}";
        return $"{detailPanel} detail: {GetRowName(row)}";
    }

    private static string GetRowName(object row) => row switch
    {
        ClusterRow cluster => cluster.Name,
        HostRow host => host.Name,
        VmRow vm => vm.Name,
        DiskRow disk => disk.Name,
        NetworkSwitchRow networkSwitch => networkSwitch.Name,
        NetworkRow net => net.Name,
        VDiskRow disk => disk.Name,
        VmNetworkPathRow adapter => adapter.Name,
        VDiskDetailRow disk => disk.Disk.Name,
        VmNetworkDetailRow adapter => adapter.Adapter.Name,
        EventRow evt => evt.Message,
        _ => string.Empty
    };

    private static VDiskRow[] GetVmDisks(VmRow vm, Snapshot snapshot)
    {
        return snapshot.VmTopology.FirstOrDefault(t => t.VmName == vm.Name && (string.IsNullOrWhiteSpace(t.HostName) || t.HostName.Equals(vm.HostName, StringComparison.OrdinalIgnoreCase)))?.Disks ?? [];
    }

    private static VmNetworkPathRow[] GetVmNetworkAdapters(VmRow vm, Snapshot snapshot)
    {
        return snapshot.VmTopology.FirstOrDefault(t => t.VmName == vm.Name && (string.IsNullOrWhiteSpace(t.HostName) || t.HostName.Equals(vm.HostName, StringComparison.OrdinalIgnoreCase)))?.Networks ?? [];
    }

    private static NetworkRow[] GetSwitchUplinkRows(Snapshot snapshot, string? hostName, string? switchName)
    {
        if (string.IsNullOrWhiteSpace(switchName))
            return [];

        var switchRow = snapshot.NetworkSwitches.FirstOrDefault(n => n.Name.Equals(switchName, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(hostName) || n.HostName.Equals(hostName, StringComparison.OrdinalIgnoreCase)));
        if (switchRow is null)
            return [];

        return switchRow.Uplinks
            .Select(uplink => NetworkTopologyMatcher.MergeWithLive(snapshot.Networks.Where(n => n.HostName.Equals(switchRow.HostName, StringComparison.OrdinalIgnoreCase)).ToArray(), uplink, switchRow.HostName))
            .DistinctBy(adapter => $"{adapter.HostName}\0{adapter.Name}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string Fmt(Metric metric) => FixedMetricCell(FmtValue(metric.Current, metric.Unit), FmtValue(metric.Max, metric.Unit), valueWidth: 9, width: MetricWidth);

    private static string FmtShort(Metric metric) => FixedMetricCell(FmtValue(metric.Current, metric.Unit), FmtValue(metric.Max, metric.Unit), valueWidth: 5, width: ShortMetricWidth);

    private static string FmtWithCapacity(Metric metric, string capacity) => FixedMetricCell(FmtValue(metric.Current, metric.Unit), FmtValue(metric.Max, metric.Unit), $"({capacity})", valueWidth: 4, configWidth: 11, width: CapacityMetricWidth);

    private static string DetailMetricValue(Metric metric)
        => $"{Cell(FmtValue(metric.Current, metric.Unit), 9, true)} | {Cell(FmtValue(metric.Max, metric.Unit), 9)}";

    private static string DetailMetricHeaderValue(string currentLabel, string maxLabel)
        => $"{Cell(currentLabel, 9, true)} | {Cell(maxLabel, 9)}";

    private static string DetailMetricWithCapacityValue(Metric metric, string capacity)
        => $"{Cell(FmtValue(metric.Current, metric.Unit), 4, true)} | {Cell(FmtValue(metric.Max, metric.Unit), 4, true)} | {Cell($"({capacity})", 12)}";

    private static string DetailSplitValue(Metric metric, double ratio)
        => $"{Cell(FmtValue(metric.Current * ratio, metric.Unit), 9, true)} | {Cell(FmtValue(metric.Max * ratio, metric.Unit), 9)}";

    private static string FixedMetricCell(string current, string max, int valueWidth, int width)
        => Cell($"{Cell(current, valueWidth, true)} | {Cell(max, valueWidth)}", width, true);

    private static string FixedMetricCell(string current, string max, string config, int valueWidth, int configWidth, int width)
        => Cell($"{Cell(current, valueWidth, true)} | {Cell(max, valueWidth, true)} | {Cell(config, configWidth)}", width, true);

    private static string FixedMetricHeaderCell(string current, string max, int valueWidth, int width)
        => Cell($"{Cell(current, valueWidth, true)} | {Cell(max, valueWidth)}", width);

    private static string FixedMetricHeaderCell(string current, string max, string config, int currentWidth, int maxWidth, int configWidth, int width)
        => Cell($"{Cell(current, currentWidth, true)} | {Cell(max, maxWidth)} | {Cell(config, configWidth)}", width);

    private static string FmtValue(double value, Unit unit)
    {
        if (double.IsNaN(value)) return "n/a";
        return unit switch
        {
            Unit.Percent => $"{value,3:N0}%",
            Unit.Mbps => FormatRate(value),
            Unit.Iops => FormatCompact(value, suffix: string.Empty, kiloSuffix: "k"),
            Unit.Milliseconds => $"{FormatNumber4(value)} ms",
            _ => FormatNumber4(value)
        };
    }

    private static string FormatRate(double megabytesPerSecond)
    {
        var kb = megabytesPerSecond * 1024;
        if (Math.Abs(kb) < 1000)
            return $"{FormatNumber4(kb)} KB/s";

        if (Math.Abs(megabytesPerSecond) < 1000)
            return $"{FormatNumber4(megabytesPerSecond)} MB/s";

        return $"{FormatNumber4(megabytesPerSecond / 1024)} GB/s";
    }

    private static string FormatCompact(double value, string suffix, string kiloSuffix)
    {
        if (Math.Abs(value) >= 1000)
            return $"{FormatNumber4(value / 1000)}{kiloSuffix}";
        return $"{FormatNumber4(value)}{suffix}";
    }

    private static string FormatNumber4(double value)
    {
        var abs = Math.Abs(value);
        string text;
        if (abs >= 100) text = value.ToString("0");
        else if (abs >= 10) text = value.ToString("0.0");
        else text = value.ToString("0.00");
        return text.Length > 4 ? text[..4] : text.PadLeft(4);
    }

    private static ConsoleColor StatusColor(string status)
    {
        return status.Trim().ToUpperInvariant() switch
        {
            "HOT" => ConsoleColor.Red,
            "OFF" => ConsoleColor.DarkGray,
            "N/A" => ConsoleColor.DarkGray,
            "BUSY" => ConsoleColor.Yellow,
            "IDLE" => ConsoleColor.Green,
            _ => ConsoleColor.Green
        };
    }

    private static ConsoleColor ReplicationColor(string status) => StatusColor(status);

    private static ConsoleColor RowColor(object row) => row switch
    {
        ClusterRow cluster => StatusColor(cluster.Status),
        HostRow host => StatusColor(host.Status),
        VmRow vm => StatusColor(vm.Status),
        DiskRow disk => StatusColor(disk.Status),
        NetworkSwitchRow networkSwitch => StatusColor(networkSwitch.Status),
        NetworkRow net => StatusColor(net.Status),
        EventRow evt => evt.Severity switch
        {
            "ERR" => ConsoleColor.Red,
            "WARN" => ConsoleColor.Yellow,
            _ => ConsoleColor.Gray
        },
        _ => ConsoleColor.Gray
    };

    private static bool IsLoading(Snapshot snapshot)
        => snapshot.Hosts.Length == 0
           && snapshot.Vms.Length == 0
           && snapshot.Disks.Length == 0
           && snapshot.Networks.Length == 0
           && snapshot.Events.Length == 0;

    private int TableNameWidth(TableKind kind)
    {
        var fixedWidths = kind switch
        {
            TableKind.ClusterLike => CountWidth + CountWidth + OwnerWidth + QuorumWidth + StatusWidth,
            TableKind.HostLike => HostVersionWidth + UptimeWidth + CapacityMetricWidth + CapacityMetricWidth + MetricWidth + MetricWidth + StatusWidth,
            TableKind.VmLike => HostColumnWidth + VmVersionWidth + UptimeWidth + CapacityMetricWidth + CapacityMetricWidth + MetricWidth + MetricWidth + StatusWidth,
            TableKind.DiskLike => HostColumnWidth + SizeWidth + MetricWidth + MetricWidth + MetricWidth + ShortMetricWidth + ShortMetricWidth + StatusWidth,
            TableKind.NetworkSwitchLike => HostColumnWidth + UplinkWidth + LinkWidth + MetricWidth + MetricWidth + MetricWidth + StatusWidth,
            TableKind.NetworkLike => LinkWidth + MetricWidth + MetricWidth + MetricWidth + ShortMetricWidth + StatusWidth,
            _ => 0
        };

        var columns = kind switch
        {
            TableKind.ClusterLike => 6,
            TableKind.HostLike => 8,
            TableKind.VmLike => 9,
            TableKind.DiskLike => 9,
            TableKind.NetworkLike => 6,
            TableKind.NetworkSwitchLike => 8,
            _ => 2
        };

        var gaps = ColGap.Length * (columns - 1);
        var width = Console.WindowWidth - fixedWidths - gaps - 1;
        return Math.Clamp(width, MinNameWidth, MaxNameWidth);
    }

    private void BeginFrame()
    {
        var width = Math.Max(0, Console.WindowWidth);
        var height = Math.Max(0, Console.WindowHeight);
        frameWidth = width;
        frameHeight = height;

        if (width != previousWidth || height != previousHeight)
        {
            TryClearConsole();
            previousLines = new string[height];
            previousForegrounds = Enumerable.Repeat(ConsoleColor.Gray, height).ToArray();
            previousBackgrounds = Enumerable.Repeat(ConsoleColor.Black, height).ToArray();
            previousWidth = width;
            previousHeight = height;
        }

        if (touchedLines.Length != height)
            touchedLines = new bool[height];
        else
            Array.Clear(touchedLines);
    }

    private void EndFrame()
    {
        var lines = Math.Min(previousLines.Length, touchedLines.Length);
        for (var y = 0; y < lines; y++)
        {
            if (!touchedLines[y] && !string.IsNullOrEmpty(previousLines[y]))
                WriteLine(y, string.Empty);
        }
    }

    private void WriteLine(int y, string text, ConsoleColor foreground = ConsoleColor.Gray, ConsoleColor background = ConsoleColor.Black)
    {
        if (y < 0 || y >= frameHeight || y >= touchedLines.Length || y >= previousLines.Length) return;
        touchedLines[y] = true;

        var width = Math.Min(frameWidth, Math.Max(0, Console.WindowWidth));
        if (width <= 1) return;
        width--;
        if (text.Length > width)
            text = text[..width];
        else if (text.Length < width)
            text = text.PadRight(width);

        if (previousLines[y] == text && previousForegrounds[y] == foreground && previousBackgrounds[y] == background)
            return;

        try
        {
            if (y >= Console.WindowHeight) return;
            Console.SetCursorPosition(0, y);
            Console.ForegroundColor = foreground;
            Console.BackgroundColor = background;
            Console.Write(text);
        }
        catch (ArgumentOutOfRangeException)
        {
            previousWidth = -1;
            previousHeight = -1;
            return;
        }
        catch (IOException)
        {
            previousWidth = -1;
            previousHeight = -1;
            return;
        }

        previousLines[y] = text;
        previousForegrounds[y] = foreground;
        previousBackgrounds[y] = background;
    }

    private static void TryClearConsole()
    {
        try
        {
            Console.Clear();
        }
        catch (ArgumentOutOfRangeException)
        {
        }
        catch (IOException)
        {
        }
    }
}

internal enum Panel { Cluster, Hosts, Vms, Disks, Network, Events }

internal enum DrillView { Overview, HostVms, NetworkAdapters, Detail }

internal enum TableKind { ClusterLike, HostLike, VmLike, DiskLike, NetworkLike, NetworkSwitchLike }

internal sealed record ViewState(
    Panel Panel,
    int Selected,
    DrillView DrillView,
    Panel DetailPanel,
    string? SelectedHostName,
    string? SelectedItemName);

internal sealed record SortState(string Column, bool Descending);

internal sealed record SortColumn(string Key, string Label);
