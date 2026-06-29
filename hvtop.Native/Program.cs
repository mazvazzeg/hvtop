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
    public const string DisplayVersion = "0.9.0-rdc+20260629.0202";
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
        DebugCounterLog.Configure(options.DebugCounters, "hvtop-debug.log");
        RdcLog.Info($"{AppName} {DisplayVersion} starting base='{AppContext.BaseDirectory}' process='{Environment.ProcessPath}' args='{RdcLog.SafeArgs(args)}'");
        try
        {
            RdcLog.Info($"parsed options listen='{options.ListenPrefix}' port={options.Port} refresh={options.Refresh.TotalSeconds:N1}s history={options.History.TotalMinutes:N0}m token={(string.IsNullOrWhiteSpace(options.Token) ? "none" : "set")}");
            using var cts = new CancellationTokenSource();
            using var firewallRule = RdcFirewallRule.Ensure(options.Port);
            using var collector = new Collector(new Options(options.Refresh, options.History, false, true, false, options.Port, options.Refresh, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60), null, null, null, null, null, false, options.DebugLog, options.DebugCounters, false, false, null));
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
                        RdcLog.Info($"sample complete in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:N0} ms hosts={current.Hosts.Length} vms={current.Vms.Length} disks={current.Disks.Length} physicalDisks={current.PhysicalDisks.Length} networks={current.Networks.Length} switches={current.NetworkSwitches.Length}");
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
                var snapshot = readSnapshot() with { RdcCollectorVersion = DisplayVersion };
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
    public const string DisplayVersion = "0.9.0+20260629.0202";
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
        DebugCounterLog.Configure(options.DebugCounters, "hvtop-debug.log");
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
            foreach (var disk in snapshot.PhysicalDisks.Take(5))
                Console.WriteLine($"PDISK {disk.HostName} PDID {disk.PhysicalDiskId} TYPE {disk.Type} SIZE {disk.Size} MAP {disk.Mapping} FRIENDLY {disk.FriendlyName} MODEL {disk.Model} FW {disk.FirmwareVersion} SN {disk.SerialNumber} NAME {disk.Name} IO {FormatSmoke(disk.Io)} IOPS {FormatSmoke(disk.Iops)} QD {FormatSmoke(disk.QueueDepth)} LAT {FormatSmoke(disk.Latency)} STA {disk.Status}");
            foreach (var net in snapshot.Networks.Take(5))
                Console.WriteLine($"NET  {net.HostName} {net.Name} THR {FormatSmoke(net.Throughput)} RX {FormatSmoke(net.Rx)} TX {FormatSmoke(net.Tx)} RDMA {FormatSmoke(net.RdmaThroughput)} STA {net.Status}");
            return 0;
        }

        var state = new AppState(options.Refresh);
        var sampler = Task.Run(() => RunSamplerAsync(collector, state, options, cts));

        try
        {
            var ui = new Tui(state, options);
            ui.Run(cts);
        }
        finally
        {
            if (options.RemoteCollectors)
            {
                state.SetRdcStatus("stopping");
                try { new Tui(state, options).RenderOnce(); } catch { }
            }

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
            Unit.QueueDepth => FormatInteger(metric.Current),
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

    private static string FormatInteger(double value)
        => double.IsNaN(value) ? "n/a" : Math.Max(0, (int)Math.Round(value, MidpointRounding.AwayFromZero)).ToString(CultureInfo.CurrentCulture);
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

    private static async Task RunSamplerAsync(Collector collector, AppState state, Options options, CancellationTokenSource cts)
    {
        var token = cts.Token;
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
                var message = $"Collector failed: {ex.Message}";
                state.AddEvent("ERR", message);
                if (!options.LocalCollectors)
                {
                    state.SetFatalError(message);
                    Console.Error.WriteLine($"hvtop fatal: {ex.Message}");
                    break;
                }
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            var delay = TimeSpan.FromMilliseconds(Math.Max(50, state.Refresh.TotalMilliseconds - elapsed.TotalMilliseconds));
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
    }
}

