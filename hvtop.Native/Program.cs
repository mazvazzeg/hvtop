using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace hvtop.Native;

internal static class Program
{
    public const string DisplayVersion = "0.2.0+20260504.1200";
    public const string AppName = "hvtop";

    public static async Task<int> Main(string[] args)
    {
        var options = Options.Parse(args);
        using var cts = new CancellationTokenSource();
        using var collector = new Collector(options);

        if (options.Smoke)
        {
            var snapshot = collector.Collect();
            Console.WriteLine($"{AppName} {DisplayVersion} smoke sample at {snapshot.At:yyyy-MM-dd HH:mm:ss}");
            foreach (var host in snapshot.Hosts)
                Console.WriteLine($"HOST {host.Name} VER {host.Version} CPU {FormatSmoke(host.Cpu)} | {FormatSmokeMax(host.Cpu)} | ({host.CpuCapacity}) MEM {FormatSmoke(host.Mem)} | {FormatSmokeMax(host.Mem)} | ({host.MemCapacity}) IO {FormatSmoke(host.Io)} NET {FormatSmoke(host.Net)} STA {host.Status}");
            foreach (var disk in snapshot.Disks.Take(5))
                Console.WriteLine($"DISK {disk.Name} SIZE {disk.Size} FRE {FormatSmoke(disk.Free)} IO {FormatSmoke(disk.Io)} IOPS {FormatSmoke(disk.Iops)} QD {FormatSmoke(disk.QueueDepth)} LAT {FormatSmoke(disk.Latency)} STA {disk.Status}");
            foreach (var net in snapshot.Networks.Take(5))
                Console.WriteLine($"NET  {net.Name} THR {FormatSmoke(net.Throughput)} RX {FormatSmoke(net.Rx)} TX {FormatSmoke(net.Tx)} STA {net.Status}");
            return 0;
        }

        var state = new AppState();
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
            var delay = TimeSpan.FromMilliseconds(Math.Max(50, options.Refresh.TotalMilliseconds - elapsed.TotalMilliseconds));
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
    }
}

internal sealed record Options(TimeSpan Refresh, TimeSpan History, bool DemoVms, bool Smoke)
{
    public static Options Parse(string[] args)
    {
        var refresh = TimeSpan.FromSeconds(1);
        var history = TimeSpan.FromMinutes(15);
        var demoVms = false;
        var smoke = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i].Trim();
            if (arg.Equals("--refresh", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                refresh = TimeSpan.FromSeconds(Math.Max(0.2, double.Parse(args[++i])));
            else if (arg.Equals("--history", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                history = TimeSpan.FromMinutes(Math.Max(1, double.Parse(args[++i])));
            else if (arg.Equals("--demo-vms", StringComparison.OrdinalIgnoreCase))
                demoVms = true;
            else if (arg.Equals("--no-demo-vms", StringComparison.OrdinalIgnoreCase))
                demoVms = false;
            else if (arg.Equals("--smoke", StringComparison.OrdinalIgnoreCase))
                smoke = true;
        }

        return new Options(refresh, history, demoVms, smoke);
    }
}

internal sealed class AppState
{
    private readonly object gate = new();
    private Snapshot snapshot = Snapshot.Empty;
    private bool refreshRequested = true;

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
    private readonly DemoVmSampler demoVms = new();
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

    public Collector(Options options)
    {
        this.options = options;
        history = new RollingHistory(options.History);
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
        var hostNet = adapterRates.Where(a => a.IsHardwareInterface).Sum(a => a.TotalBytesPerSecond) / 1024 / 1024;
        var iops = diskIops.Read();
        var queue = diskQueue.Read();
        var latency = diskLatencySeconds.Read() * 1000;
        var topologyRefreshRequested = false;
        if (refreshRequested)
        {
            inventory.RequestRefresh();
            topologyRefreshRequested = true;
            networkDiagnosticsLogged = false;
        }

        var inventoryResult = inventory.TryRead();
        var inventoryVms = inventoryResult.Vms;
        LogicalDiskSampler.Refresh();

        if (!string.IsNullOrWhiteSpace(inventoryResult.EventMessage))
            AddEvent(inventoryResult.EventSeverity, inventoryResult.EventMessage);

        var host = new HostRow(
            Environment.MachineName,
            HostVersionDetector.Detect(),
            Metric.Percent(hostCpu),
            $"{Environment.ProcessorCount} CPU",
            Metric.Percent(hostMem),
            CapacityFormatter.FormatConfigCapacity(Native.GetPhysicalMemoryBytes()),
            Metric.Mbps(hostIo),
            Metric.Mbps(hostNet),
            Status.From(hostCpu, hostMem, latency, queue));

        var disks = StorageInventory.Enumerate()
            .Select(storage =>
            {
                var free = 100.0 * storage.FreeBytes / storage.TotalBytes;
                var diskIo = LogicalDiskSampler.TotalMbps(storage.CounterKey);
                var diskIopsValue = LogicalDiskSampler.TotalIops(storage.CounterKey);
                var diskQueueDepth = LogicalDiskSampler.QueueDepth(storage.CounterKey);
                var diskLatencyMs = LogicalDiskSampler.LatencyMs(storage.CounterKey);
                var row = new DiskRow(
                    storage.DisplayName,
                    CapacityFormatter.FormatCapacity(storage.TotalBytes),
                    Metric.Percent(free),
                    Metric.Mbps(diskIo),
                    Metric.Iops(diskIopsValue),
                    Metric.Plain(diskQueueDepth),
                    Metric.Milliseconds(diskLatencyMs),
                    Status.From(10, 100 - free, diskLatencyMs, diskQueueDepth));
                return row;
            })
            .ToArray();

        var adapters = adapterRates
            .Select(a =>
            {
                var row = new NetworkRow(
                    a.Name,
                    a.Description,
                    NetworkLinkFormatter.Format(a.LinkSpeedBitsPerSecond, a.IsUp),
                    a.IsUp,
                    a.LinkSpeedBitsPerSecond,
                    Metric.Mbps(a.TotalBytesPerSecond / 1024 / 1024),
                    Metric.Mbps(a.ReceivedBytesPerSecond / 1024 / 1024),
                    Metric.Mbps(a.SentBytesPerSecond / 1024 / 1024),
                    Metric.Plain(a.DropsPerSecond),
                    Status.FromNetwork(a.TotalBytesPerSecond, a.LinkSpeedBitsPerSecond, a.IsUp));
                return row;
            })
            .ToArray();

        var vms = options.DemoVms ? demoVms.Collect(history, host.Name) : hyperV.Collect(history, host.Name, inventoryVms);
        if (!options.DemoVms && vms.Length == 0)
            AddEvent("INFO", "No Hyper-V VM counters found. Run with --demo-vms to show demo VM rows.");

        var vmTopologyResult = options.DemoVms ? HyperVTopologyResult.Empty : topology.TryRead(disks, adapters);
        if (topologyRefreshRequested)
            topology.RequestRefresh();
        if (!string.IsNullOrWhiteSpace(vmTopologyResult.EventMessage))
            AddEvent(vmTopologyResult.EventSeverity, vmTopologyResult.EventMessage);

        var networkSwitches = !options.DemoVms && topology.IsRefreshing && vmTopologyResult.Switches.Length == 0
            ? []
            : BuildNetworkSwitches(adapters, vmTopologyResult.Switches);
        MaybeLogNetworkDiagnostics(refreshRequested, adapterRates, adapters, vmTopologyResult.Switches);
        var discovery = BuildDiscoveryProgress(host, vms, disks, adapters, networkSwitches);
        MaybeLogDiscoveryProgress(discovery, host, vms, disks, adapters, networkSwitches);
        var liveTopology = EnrichTopologyWithLiveStats(vmTopologyResult.Topology);
        if (liveTopology.Length > 0)
        {
            var mergedTopology = MergeVmTotalsIntoTopology(vms, liveTopology);
            disks = ApplyVmDiskLoadToStorage(disks, mergedTopology);
            vms = ApplyTopologyFallback(vms, mergedTopology);
            host = host with
            {
                Io = Metric.Mbps(Math.Max(host.Io.Current, Math.Max(vms.Sum(v => v.Io.Current), disks.Sum(d => d.Io.Current))))
            };
            host = history.Apply("host:" + host.Name, host);
            disks = disks.Select(d => history.Apply("disk:" + d.Name, d)).ToArray();
            networkSwitches = networkSwitches.Select(n => history.Apply("vswitch:" + n.Name, n)).ToArray();
            adapters = adapters.Select(n => history.Apply("net:" + n.Name, n)).ToArray();
            MaybeAddSpikeEvent(host, disks);
            return new Snapshot(DateTime.Now, [host], vms, disks, networkSwitches, adapters, events, mergedTopology, !initialDiscoveryComplete, inventory.IsRefreshing, topology.IsRefreshing, discovery);
        }

        host = history.Apply("host:" + host.Name, host);
        disks = disks.Select(d => history.Apply("disk:" + d.Name, d)).ToArray();
        networkSwitches = networkSwitches.Select(n => history.Apply("vswitch:" + n.Name, n)).ToArray();
        adapters = adapters.Select(n => history.Apply("net:" + n.Name, n)).ToArray();
        MaybeAddSpikeEvent(host, disks);
        return new Snapshot(DateTime.Now, [host], vms, disks, networkSwitches, adapters, events, liveTopology, !initialDiscoveryComplete, inventory.IsRefreshing, topology.IsRefreshing, discovery);
    }

    private static NetworkSwitchRow[] BuildNetworkSwitches(NetworkRow[] adapters, NetworkSwitchTopologyRow[] switches)
    {
        var switchRows = switches.Length > 0
            ? switches
            : adapters.Select(adapter => new NetworkSwitchTopologyRow(adapter.Name, "Adapter", [new NetworkUplinkInfo(adapter.Name, adapter.Description, adapter.Link, adapter.IsUp, adapter.LinkSpeedBitsPerSecond)])).ToArray();

        return switchRows
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(switchRow =>
            {
                var uplinks = switchRow.Uplinks
                    .Select(uplink => NetworkTopologyMatcher.MergeWithLive(adapters, uplink))
                    .Where(adapter => adapter is not null)
                    .Cast<NetworkRow>()
                    .DistinctBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var throughput = uplinks.Sum(a => a.Throughput.Current);
                var rx = uplinks.Sum(a => a.Rx.Current);
                var tx = uplinks.Sum(a => a.Tx.Current);
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
                    switchRow.Name,
                    switchRow.SwitchType,
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
        var topologyMap = topology.ToDictionary(t => t.VmName, StringComparer.OrdinalIgnoreCase);
        return vms.Select(vm =>
        {
            if (!topologyMap.TryGetValue(vm.Name, out var topo) || topo.Disks.Length == 0)
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
        var vmMap = vms.ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
        return topology.Select(vm =>
        {
            if (!vmMap.TryGetValue(vm.VmName, out var liveVm) || vm.Disks.Length == 0)
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
        var live = VirtualDiskCounterSampler.Read();
        return topology.Select(vm => vm with
        {
            Disks = vm.Disks.Select(disk =>
            {
                var stats = HyperVNaming.ResolveDiskStats(live, disk.Path, disk.Name) ?? new VirtualDiskStats(0, 0, 0, 0);
                return disk with
                {
                    ReadMbps = stats.ReadMbps,
                    ReadIops = stats.ReadIops,
                    WriteMbps = stats.WriteMbps,
                    WriteIops = stats.WriteIops
                };
            }).ToArray()
        }).ToArray();
    }

    private DiskRow[] ApplyVmDiskLoadToStorage(DiskRow[] disks, VmTopologyRow[] topology)
    {
        var byStorage = topology
            .SelectMany(vm => vm.Disks)
            .GroupBy(d => d.StorageName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    Io = g.Sum(d => d.ReadMbps + d.WriteMbps),
                    Iops = g.Sum(d => d.ReadIops + d.WriteIops)
                },
                StringComparer.OrdinalIgnoreCase);

        return disks.Select(disk =>
        {
            if (!byStorage.TryGetValue(disk.Name, out var totals))
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
        var vmsReady = options.DemoVms || !inventory.IsRefreshing;
        var storageReady = disks.Length > 0;
        var networkReady = options.DemoVms || !topology.IsRefreshing;
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
            AddEvent("INFO", $"Discovery Network: {networkSwitches.Length} vSwitch(es), {adapters.Length} interface row(s)");
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
        var hardware = adapterRates.Where(a => a.IsHardwareInterface).ToArray();
        var upHardware = hardware.Count(a => a.IsUp);
        var totalHardwareMbps = hardware.Sum(a => a.TotalBytesPerSecond) / 1024d / 1024d;
        AddEvent("INFO", $"NETDIAG live={adapterRates.Length} hw={hardware.Length} up={upHardware} sw={switches.Length} hw-throughput={totalHardwareMbps:0.00} MB/s");

        foreach (var adapter in adapterRates
                     .OrderByDescending(a => a.IsHardwareInterface)
                     .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                     .Take(10))
        {
            AddEvent(
                "INFO",
                $"NETIF name='{TrimForEvent(adapter.Name, 28)}' desc='{TrimForEvent(adapter.Description, 42)}' guid='{adapter.InterfaceId}' hw={adapter.IsHardwareInterface} up={adapter.IsUp} link={NetworkLinkFormatter.Format(adapter.LinkSpeedBitsPerSecond, adapter.IsUp)} rx={adapter.ReceivedBytesPerSecond / 1024d / 1024d:0.00} tx={adapter.SentBytesPerSecond / 1024d / 1024d:0.00} MB/s");
        }

        foreach (var switchRow in switches.Take(4))
        {
            AddEvent("INFO", $"NETSW '{switchRow.Name}' type={switchRow.SwitchType} uplinks={switchRow.Uplinks.Length}");
            foreach (var uplink in switchRow.Uplinks.Take(6))
            {
                var match = NetworkTopologyMatcher.MatchAdapter(adapters, uplink.Name, uplink.Description);
                AddEvent(
                    match is null ? "WARN" : "INFO",
                    $"NETMAP sw='{TrimForEvent(switchRow.Name, 18)}' uplink='{TrimForEvent(uplink.Name, 28)}' desc='{TrimForEvent(uplink.Description, 36)}' -> {(match is null ? "NO MATCH" : $"'{TrimForEvent(match.Name, 28)}'")}");
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

        events = events.Prepend(new EventRow(DateTime.Now, severity, message)).Take(200).ToArray();
    }

    public void Dispose() => pdh.Dispose();
}

internal sealed class DemoVmSampler
{
    private int tick;

    public VmRow[] Collect(RollingHistory history, string hostName)
    {
        tick++;
        string[] names = ["SQL01", "WEB02", "FS01", "DC01", "BUILD03", "MON01"];
        int[] vcpus = [4, 2, 4, 2, 8, 2];
        double[] memoryGb = [32, 8, 16, 4, 64, 8];
        return names.Select((name, i) =>
        {
            var cpu = Math.Max(0, 10 + i * 8 + 24 * Math.Abs(Math.Sin((tick + i) / (4.0 + i))));
            if (name == "SQL01" && tick % 18 > 11) cpu += 35;
            var mem = Math.Min(98, 20 + i * 9 + 12 * Math.Abs(Math.Cos((tick + i) / 8.0)));
            var io = 80 + i * 65 + 760 * Math.Abs(Math.Sin((tick + i) / 10.0));
            var net = 15 + i * 8 + 80 * Math.Abs(Math.Cos((tick + i) / 7.0));
            var iops = 800 + i * 1100 + 15000 * Math.Abs(Math.Sin((tick + i) / 12.0));
            var latency = name == "SQL01" ? 8 + 11 * Math.Abs(Math.Sin(tick / 9.0)) : 2 + i;
            var row = new VmRow(
                name,
                hostName,
                "demo",
                Metric.Percent(cpu),
                $"{vcpus[i]} vCPU",
                Metric.Percent(mem),
                CapacityFormatter.FormatConfigCapacity(memoryGb[i] * 1024 * 1024 * 1024),
                Metric.Mbps(io),
                Metric.Mbps(net),
                Metric.Iops(iops),
                Metric.Milliseconds(latency),
                Status.From(cpu, mem, latency, 0));
            return history.Apply("vm:" + row.Name, row);
        }).ToArray();
    }
}

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
                var row = new VmRow(
                    name,
                    hostName,
                    FormatVersion(vm.Version),
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
    private string? pendingEventMessage;
    private string pendingEventSeverity = "INFO";
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
        string? eventMessage;
        string eventSeverity;
        lock (gate)
        {
            eventMessage = pendingEventMessage;
            eventSeverity = pendingEventSeverity;
            pendingEventMessage = null;
            return new HyperVInventoryResult(cache, "PowerShell", eventMessage, eventSeverity);
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
                    pendingEventMessage = DedupEvent("Hyper-V native WMI inventory disabled in single-file build, using PowerShell fallback.");
                    pendingEventSeverity = "WARN";
                }
                else
                {
                    pendingEventMessage = DedupEvent("Hyper-V inventory unavailable via native API and PowerShell fallback.");
                    pendingEventSeverity = "ERR";
                }
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
            "$rows=@(); " +
            "try { " +
            "Import-Module Hyper-V -ErrorAction Stop | Out-Null; " +
            "$rows = @(Get-VM | Select-Object Name,@{N='Version';E={[string]$_.Version}},@{N='IsRunning';E={[bool]($_.State -eq 'Running')}},MemoryAssigned,MemoryDemand,MemoryStatus,DynamicMemoryEnabled) " +
            "} catch { } ; " +
            "if (-not $rows -or $rows.Count -eq 0) { " +
            "$rows = @(Get-CimInstance -Namespace root/virtualization/v2 -ClassName Msvm_ComputerSystem " +
            "| Where-Object { $_.Caption -eq 'Virtual Machine' } " +
            "| Select-Object @{N='Name';E={$_.ElementName}},@{N='Version';E={''}},@{N='IsRunning';E={[bool]($_.EnabledState -eq 2)}},@{N='MemoryAssigned';E={0}},@{N='MemoryDemand';E={0}},@{N='MemoryStatus';E={''}},@{N='DynamicMemoryEnabled';E={$false}}) " +
            "} ; " +
            "[pscustomobject]@{Vms=@($rows)} | ConvertTo-Json -Compress -Depth 4";

        if (!PowerShellRunner.TryRun(script, 15000, out var output))
            return HyperVInventoryData.Empty;

        return ParseInventoryJson(output);
    }

    private static HyperVInventoryData ParseInventoryJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return HyperVInventoryData.Empty;

        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement[] rows;
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("Vms", out var vmsElement))
            {
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
                    var memoryAssignedBytes = ReadJsonDouble(element, "MemoryAssigned");
                    var memoryDemandBytes = ReadJsonDouble(element, "MemoryDemand");
                    var memoryStatus = ReadJsonString(element, "MemoryStatus");
                    var dynamicMemoryEnabled = ReadJsonBool(element, "DynamicMemoryEnabled");
                    return new HyperVInventoryVm(name, version, isRunning, memoryAssignedBytes, memoryDemandBytes, memoryStatus, dynamicMemoryEnabled);
                })
                .Where(vm => !string.IsNullOrWhiteSpace(vm.Name))
                .DistinctBy(vm => vm.Name, StringComparer.OrdinalIgnoreCase)
                .OrderBy(vm => vm.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new HyperVInventoryData(vms, true);
        }
        catch
        {
            return HyperVInventoryData.Empty;
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

}

internal sealed record HyperVInventoryVm(string Name, string Version, bool IsRunning, double MemoryAssignedBytes, double MemoryDemandBytes, string MemoryStatus, bool DynamicMemoryEnabled);
internal sealed record HyperVInventoryData(HyperVInventoryVm[] Vms, bool Available)
{
    public static HyperVInventoryData Empty { get; } = new([], false);
}
internal sealed record HyperVInventoryResult(HyperVInventoryVm[] Vms, string Source, string? EventMessage, string EventSeverity);

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
                pendingEventMessage = DedupEvent("Hyper-V native WMI topology disabled in single-file build, using PowerShell fallback.");
                pendingEventSeverity = "WARN";
            }
        }
        catch
        {
            lock (gate)
            {
                pendingEventMessage = DedupEvent("Hyper-V topology unavailable via native API and PowerShell fallback.");
                pendingEventSeverity = "ERR";
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
            "$sw = $_; $members=@(); " +
            "try { if (Get-Command Get-NetSwitchTeamMember -ErrorAction SilentlyContinue) { $members = @(Get-NetSwitchTeamMember -Team $sw.Name -ErrorAction Stop | Select-Object -ExpandProperty Name) } } catch { } ; " +
            "$descs=@(); " +
            "if ($sw.PSObject.Properties.Match('NetAdapterInterfaceDescriptions').Count -gt 0) { $descs = @($sw.NetAdapterInterfaceDescriptions) } " +
            "elseif ($sw.PSObject.Properties.Match('NetAdapterInterfaceDescription').Count -gt 0 -and $sw.NetAdapterInterfaceDescription) { $descs = @($sw.NetAdapterInterfaceDescription) } " +
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
            "[pscustomobject]@{ Name=$sw.Name; SwitchType=[string]$sw.SwitchType; Uplinks=@($uplinks) } " +
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
                    ReadJsonUplinks(element, "Uplinks")))
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

    public static NetworkRow MergeWithLive(NetworkRow[] adapters, NetworkUplinkInfo uplink)
    {
        var live = MatchAdapter(adapters, uplink.Name, uplink.Description);
        if (live is not null)
            return live;

        return new NetworkRow(
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
    {
        using var process = new Process();
        process.StartInfo.FileName = "powershell.exe";
        process.StartInfo.Arguments = $"-NoProfile -NonInteractive -Command \"{command}\"";
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
            return false;
        }

        Task.WaitAll([stdoutTask, stderrTask], 500);
        output = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
        return process.ExitCode == 0;
    }
}

internal static class LogicalDiskSampler
{
    private static readonly Dictionary<string, double> DiskBytes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, double> DiskIops = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, double> Queue = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, double> Latency = new(StringComparer.OrdinalIgnoreCase);

    public static void Refresh()
    {
        Replace(DiskBytes, PdhWildcardReader.Read(@"\LogicalDisk(*)\Disk Bytes/sec", NormalizeInstance));
        Replace(DiskIops, PdhWildcardReader.Read(@"\LogicalDisk(*)\Disk Transfers/sec", NormalizeInstance));
        Replace(Queue, PdhWildcardReader.Read(@"\LogicalDisk(*)\Current Disk Queue Length", NormalizeInstance));
        Replace(Latency, PdhWildcardReader.Read(@"\LogicalDisk(*)\Avg. Disk sec/Transfer", NormalizeInstance)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value * 1000, StringComparer.OrdinalIgnoreCase));
    }

    public static double TotalMbps(string drive) => Read(DiskBytes, drive) / 1024 / 1024;
    public static double TotalIops(string drive) => Read(DiskIops, drive);
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
        return ReadInterfaceRows()
            .Where(row => row.Type != Native.IF_TYPE_SOFTWARE_LOOPBACK)
            .Select(row => TryRead(row, now, out var rate) ? rate : null)
            .Where(rate => rate is not null)
            .Cast<AdapterRate>()
            .ToArray();
    }

    private bool TryRead(MibIfRow2 row, DateTime now, out AdapterRate rate)
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
        var drops = isUp && current.Discards >= prior.Discards ? (current.Discards - prior.Discards) / seconds : 0;

        previous[key] = current;
        string alias;
        string description;
        unsafe
        {
            alias = ReadRowString(row.Alias, $"if-{row.InterfaceIndex}");
            description = ReadRowString(row.Description, alias);
        }

        rate = new AdapterRate(
            alias,
            description,
            key,
            unchecked((long)Math.Max(row.ReceiveLinkSpeed, row.TransmitLinkSpeed)),
            isUp,
            (row.InterfaceAndOperStatusFlags & Native.IF_HARDWARE_INTERFACE) != 0,
            rx,
            tx,
            drops);
        return true;
    }

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

internal sealed record AdapterRate(string Name, string Description, string InterfaceId, long LinkSpeedBitsPerSecond, bool IsUp, bool IsHardwareInterface, double ReceivedBytesPerSecond, double SentBytesPerSecond, double DropsPerSecond)
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
    public const uint IF_TYPE_SOFTWARE_LOOPBACK = 24;
    public const byte IF_HARDWARE_INTERFACE = 0x01;
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
        return row with
        {
            Free = row.Free with { Max = values[nameof(row.Free)] },
            Io = row.Io with { Max = values[nameof(row.Io)] },
            Iops = row.Iops with { Max = values[nameof(row.Iops)] },
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
    public static Snapshot Empty { get; } = new(DateTime.Now, [], [], [], [], [], [], [], true, false, false, DiscoveryProgress.Empty);
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

internal sealed record HostRow(string Name, string Version, Metric Cpu, string CpuCapacity, Metric Mem, string MemCapacity, Metric Io, Metric Net, string Status)
{
    public IReadOnlyDictionary<string, double> Metrics => new Dictionary<string, double>
    {
        [nameof(Cpu)] = Cpu.Current,
        [nameof(Mem)] = Mem.Current,
        [nameof(Io)] = Io.Current,
        [nameof(Net)] = Net.Current
    };
}

internal sealed record VmRow(string Name, string HostName, string Version, Metric Cpu, string CpuCapacity, Metric Mem, string MemCapacity, Metric Io, Metric Net, Metric Iops, Metric Latency, string Status)
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

internal sealed record DiskRow(string Name, string Size, Metric Free, Metric Io, Metric Iops, Metric QueueDepth, Metric Latency, string Status)
{
    public IReadOnlyDictionary<string, double> Metrics => new Dictionary<string, double>
    {
        [nameof(Free)] = Free.Current,
        [nameof(Io)] = Io.Current,
        [nameof(Iops)] = Iops.Current,
        [nameof(QueueDepth)] = QueueDepth.Current,
        [nameof(Latency)] = Latency.Current
    };
}

internal sealed record NetworkRow(string Name, string Description, string Link, bool IsUp, long LinkSpeedBitsPerSecond, Metric Throughput, Metric Rx, Metric Tx, Metric Drops, string Status)
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

internal sealed record NetworkSwitchRow(string Name, string SwitchType, NetworkUplinkInfo[] Uplinks, string Link, Metric Throughput, Metric Rx, Metric Tx, Metric Drops, string Status)
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
    double WriteIops);

internal sealed record VmNetworkPathRow(
    string Name,
    string SwitchName,
    string PhysicalAdapterName);

internal sealed record NetworkSwitchTopologyRow(
    string Name,
    string SwitchType,
    NetworkUplinkInfo[] Uplinks);

internal sealed record VmTopologyRow(
    string VmName,
    VDiskRow[] Disks,
    VmNetworkPathRow[] Networks);

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
    private const int SizeWidth = 10;
    private const int SwitchTypeWidth = 8;
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
    private string[] previousLines = [];
    private ConsoleColor[] previousForegrounds = [];
    private ConsoleColor[] previousBackgrounds = [];
    private bool[] touchedLines = [];
    private int previousWidth;
    private int previousHeight;

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
            case ConsoleKey.UpArrow:
                selected = Math.Max(0, selected - 1);
                return;
            case ConsoleKey.DownArrow:
                selected++;
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

    private void SetPanel(Panel next)
    {
        panel = next;
        selected = 0;
        drillView = DrillView.Overview;
        selectedHostName = null;
        selectedItemName = null;
        backStack.Clear();
    }

    private void OpenSelected()
    {
        if (panel == Panel.Events) return;
        var rows = CurrentRows();
        if (rows.Count == 0) return;
        selected = Math.Min(selected, rows.Count - 1);
        var row = rows[selected];

        if (panel == Panel.Hosts && drillView == DrillView.Overview && row is HostRow host)
        {
            PushView();
            selectedHostName = host.Name;
            selectedItemName = null;
            selected = 0;
            drillView = DrillView.HostVms;
            return;
        }

        if (panel == Panel.Network && drillView == DrillView.Overview && row is NetworkSwitchRow networkSwitch)
        {
            PushView();
            selectedHostName = networkSwitch.Name;
            selectedItemName = null;
            selected = 0;
            drillView = DrillView.NetworkAdapters;
            return;
        }

        PushView();
        selectedItemName = GetRowName(row);
        detailPanel = row is VmRow ? Panel.Vms : panel;
        drillView = DrillView.Detail;
        selected = 0;
    }

    private void OpenDetailSelection()
    {
        var target = ResolveDetailTarget();
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
            var storageIndex = FindStorageIndex(snapshot, disks[selected]);
            if (storageIndex < 0)
            {
                PopView();
                return;
            }

            panel = Panel.Disks;
            drillView = DrillView.Overview;
            selectedHostName = null;
            selectedItemName = null;
            selected = storageIndex;
            return;
        }

        var adapter = adapters[selected - disks.Length];
        var networkIndex = FindNetworkSwitchIndex(snapshot, adapter.SwitchName);
        if (networkIndex < 0)
        {
            PopView();
            return;
        }

        panel = Panel.Network;
        drillView = DrillView.Overview;
        selectedHostName = null;
        selectedItemName = null;
        selected = networkIndex;
    }

    private static int FindStorageIndex(Snapshot snapshot, VDiskRow disk)
    {
        var exact = Array.FindIndex(snapshot.Disks, d => d.Name.Equals(disk.StorageName, StringComparison.OrdinalIgnoreCase));
        if (exact >= 0) return exact;

        var root = Path.GetPathRoot(disk.Path)?.TrimEnd('\\') ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(root))
        {
            var byRoot = Array.FindIndex(snapshot.Disks, d => d.Name.StartsWith(root, StringComparison.OrdinalIgnoreCase));
            if (byRoot >= 0) return byRoot;
        }

        return -1;
    }

    private static int FindNetworkSwitchIndex(Snapshot snapshot, string switchName)
        => Array.FindIndex(snapshot.NetworkSwitches, n => n.Name.Equals(switchName, StringComparison.OrdinalIgnoreCase));

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
        if (panel == Panel.Hosts && drillView == DrillView.HostVms)
            return s.Vms.Where(v => v.HostName == selectedHostName).Cast<object>().ToArray();
        if (panel == Panel.Network && drillView == DrillView.NetworkAdapters)
            return GetSwitchUplinkRows(s, selectedHostName).Cast<object>().ToArray();

        return panel switch
        {
            Panel.Hosts => s.Hosts,
            Panel.Vms => s.Vms,
            Panel.Disks => s.Disks,
            Panel.Network => s.NetworkSwitches,
            Panel.Events => s.Events,
            _ => []
        };
    }

    private void Render()
    {
        var s = state.Read();
        BeginFrame();
        WriteLine(0, $"{Program.AppName} {Program.DisplayVersion}  Sample: {s.At:HH:mm:ss}  Refresh: {options.Refresh.TotalSeconds:N1}s  History: {options.History.TotalMinutes:N0}m", ConsoleColor.White);
        WriteLine(1, Nav(), ConsoleColor.Yellow);
        WriteLine(2, "Arrows/j/k move  Enter drill down  Backspace/Esc back  r rescan  q quit", ConsoleColor.DarkGray);

        if (ShouldShowLoadingOverlay(s))
        {
            RenderLoadingOverlay(s);
            EndFrame();
            Console.ResetColor();
            return;
        }

        if (drillView == DrillView.Detail) RenderDetail();
        else RenderTable();
        EndFrame();
        Console.ResetColor();
    }

    private string Nav()
    {
        return string.Join("  ", new[]
        {
            panel == Panel.Hosts ? "[H] HOSTS" : " H  HOSTS",
            panel == Panel.Vms ? "[V] VMS" : " V  VMS",
            panel == Panel.Disks ? "[D] CSV / STORAGE" : " D  CSV / STORAGE",
            panel == Panel.Network ? "[N] NETWORK" : " N  NETWORK",
            panel == Panel.Events ? "[E] EVENTS" : " E  EVENTS"
        });
    }

    private bool ShouldShowLoadingOverlay(Snapshot snapshot)
    {
        if (snapshot.Loading || !snapshot.Discovery.Complete || IsLoading(snapshot))
            return true;

        return panel switch
        {
            Panel.Vms => snapshot.InventoryRefreshing && snapshot.Vms.Length == 0,
            Panel.Hosts when drillView == DrillView.HostVms => snapshot.InventoryRefreshing && CurrentRows().Count == 0,
            Panel.Network when drillView == DrillView.Overview => snapshot.TopologyRefreshing && snapshot.NetworkSwitches.Length == 0,
            Panel.Network when drillView == DrillView.NetworkAdapters => snapshot.TopologyRefreshing && CurrentRows().Count == 0,
            _ => false
        };
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

    private void RenderTable()
    {
        switch (panel)
        {
            case Panel.Hosts:
                if (drillView == DrillView.HostVms)
                {
                    var nameWidth = TableNameWidth(TableKind.VmLike);
                    RenderRows(
                        Row(Header(DisplayName($"HOST {selectedHostName} -> VMS", nameWidth), nameWidth), Header("VER", VmVersionWidth), CapacityMetricGroupHeader("CPU"), CapacityMetricGroupHeader("MEM"), GroupHeader("I/O", MetricWidth), GroupHeader("NET", MetricWidth), Header("STA", StatusWidth)),
                        Row(Header(string.Empty, nameWidth), Header(string.Empty, VmVersionWidth), CapacityMetricSubHeader(), CapacityMetricSubHeader(), MetricSubHeader(), MetricSubHeader(), Header(string.Empty, StatusWidth)),
                        CurrentRows().Cast<VmRow>().ToArray(),
                        r => Row(Cell(DisplayName(r.Name, nameWidth), nameWidth), Cell(r.Version, VmVersionWidth), FmtWithCapacity(r.Cpu, r.CpuCapacity), FmtWithCapacity(r.Mem, r.MemCapacity), Fmt(r.Io), Fmt(r.Net), Cell(r.Status, StatusWidth)));
                }
                else
                {
                    var nameWidth = TableNameWidth(TableKind.HostLike);
                    RenderRows(
                        Row(Header("HOSTNAME", nameWidth), Header("VER", HostVersionWidth), CapacityMetricGroupHeader("CPU"), CapacityMetricGroupHeader("MEM"), GroupHeader("I/O", MetricWidth), GroupHeader("NET", MetricWidth), Header("STA", StatusWidth)),
                        Row(Header(string.Empty, nameWidth), Header(string.Empty, HostVersionWidth), CapacityMetricSubHeader(), CapacityMetricSubHeader(), MetricSubHeader(), MetricSubHeader(), Header(string.Empty, StatusWidth)),
                        state.Read().Hosts,
                        r => Row(Cell(DisplayName(r.Name, nameWidth), nameWidth), Cell(r.Version, HostVersionWidth), FmtWithCapacity(r.Cpu, r.CpuCapacity), FmtWithCapacity(r.Mem, r.MemCapacity), Fmt(r.Io), Fmt(r.Net), Cell(r.Status, StatusWidth)));
                }
                break;
            case Panel.Vms:
                {
                    var nameWidth = TableNameWidth(TableKind.VmLike);
                    RenderRows(
                        Row(Header("NAME", nameWidth), Header("VER", VmVersionWidth), CapacityMetricGroupHeader("CPU"), CapacityMetricGroupHeader("MEM"), GroupHeader("I/O", MetricWidth), GroupHeader("NET", MetricWidth), Header("STA", StatusWidth)),
                        Row(Header(string.Empty, nameWidth), Header(string.Empty, VmVersionWidth), CapacityMetricSubHeader(), CapacityMetricSubHeader(), MetricSubHeader(), MetricSubHeader(), Header(string.Empty, StatusWidth)),
                        state.Read().Vms,
                        r => Row(Cell(DisplayName(r.Name, nameWidth), nameWidth), Cell(r.Version, VmVersionWidth), FmtWithCapacity(r.Cpu, r.CpuCapacity), FmtWithCapacity(r.Mem, r.MemCapacity), Fmt(r.Io), Fmt(r.Net), Cell(r.Status, StatusWidth)));
                }
                break;
            case Panel.Disks:
                {
                    var nameWidth = TableNameWidth(TableKind.DiskLike);
                    RenderRows(
                        Row(Header("NAME", nameWidth), Header("SIZE", SizeWidth), GroupHeader("FRE", MetricWidth), GroupHeader("I/O", MetricWidth), GroupHeader("IOPS", MetricWidth), GroupHeader("QD", ShortMetricWidth), GroupHeader("LAT", ShortMetricWidth), Header("STA", StatusWidth)),
                        Row(Header(string.Empty, nameWidth), Header(string.Empty, SizeWidth), MetricSubHeader(), MetricSubHeader(), MetricSubHeader(), ShortMetricSubHeader(), ShortMetricSubHeader(), Header(string.Empty, StatusWidth)),
                        state.Read().Disks,
                        r => Row(Cell(DisplayName(r.Name, nameWidth), nameWidth), Cell(r.Size, SizeWidth, true), Fmt(r.Free), Fmt(r.Io), Fmt(r.Iops), FmtShort(r.QueueDepth), FmtShort(r.Latency), Cell(r.Status, StatusWidth)));
                }
                break;
            case Panel.Network:
                {
                    if (drillView == DrillView.NetworkAdapters)
                    {
                        var nameWidth = TableNameWidth(TableKind.NetworkLike);
                        RenderRows(
                            Row(Header(DisplayName($"VSWITCH {selectedHostName} -> PNICS", nameWidth), nameWidth), Header("LINK", LinkWidth), GroupHeader("THR", MetricWidth), GroupHeader("RX", MetricWidth), GroupHeader("TX", MetricWidth), GroupHeader("DROPS", ShortMetricWidth), Header("STA", StatusWidth)),
                            Row(Header(string.Empty, nameWidth), Header(string.Empty, LinkWidth), MetricSubHeader(), MetricSubHeader(), MetricSubHeader(), ShortMetricSubHeader(), Header(string.Empty, StatusWidth)),
                            CurrentRows().Cast<NetworkRow>().ToArray(),
                            r => Row(Cell(DisplayName(r.Name, nameWidth), nameWidth), Cell(r.Link, LinkWidth), Fmt(r.Throughput), Fmt(r.Rx), Fmt(r.Tx), FmtShort(r.Drops), Cell(r.Status, StatusWidth)));
                    }
                    else
                    {
                        var nameWidth = TableNameWidth(TableKind.NetworkSwitchLike);
                        RenderRows(
                            Row(Header("VSWITCH", nameWidth), Header("TYPE", SwitchTypeWidth), Header("UPL", UplinkWidth), Header("LINK", LinkWidth), GroupHeader("THR", MetricWidth), GroupHeader("RX", MetricWidth), GroupHeader("TX", MetricWidth), Header("STA", StatusWidth)),
                            Row(Header(string.Empty, nameWidth), Header(string.Empty, SwitchTypeWidth), Header(string.Empty, UplinkWidth), Header(string.Empty, LinkWidth), MetricSubHeader(), MetricSubHeader(), MetricSubHeader(), Header(string.Empty, StatusWidth)),
                            state.Read().NetworkSwitches,
                            r => Row(Cell(DisplayName(r.Name, nameWidth), nameWidth), Cell(r.SwitchType, SwitchTypeWidth), Cell(r.Uplinks.Length.ToString(), UplinkWidth, true), Cell(r.Link, LinkWidth), Fmt(r.Throughput), Fmt(r.Rx), Fmt(r.Tx), Cell(r.Status, StatusWidth)));
                    }
                }
                break;
            case Panel.Events:
                RenderRows(Row(Header("DATE", 19), Header("SEV", 5), Header("WHAT JUST HAPPENED", 80)),
                    state.Read().Events, r => Row(Cell($"{r.At:yyyy-MM-dd HH:mm:ss}", 19), Cell(r.Severity, 5), r.Message));
                break;
        }
    }

    private static string Row(params string[] cells) => string.Join(ColGap, cells);

    private static string Header(string text, int width) => Cell(text, width);

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
            case VmRow vm:
                var vmDisks = GetVmDisks(vm, state.Read());
                var vmAdapters = GetVmNetworkAdapters(vm, state.Read());
                selected = Math.Min(selected, Math.Max(0, vmDisks.Length + vmAdapters.Length - 1));
                Detail(7, "Name", vm.Name);
                DetailMetricWithCapacity(8, "CPU", vm.Cpu, vm.CpuCapacity);
                DetailMetricWithCapacity(9, "Memory", vm.Mem, vm.MemCapacity);
                DetailScalar(10, string.Empty, string.Empty);
                DetailMetric(11, "Total IO", vm.Io);
                DetailMetricSplit(12, "  Read", vm.Io, 0.25);
                DetailMetricSplit(13, "  Write", vm.Io, 0.75);
                DetailScalar(14, string.Empty, string.Empty);
                DetailMetric(15, "Total IOPS", vm.Iops);
                DetailMetricSplit(16, "  Read IOPS", vm.Iops, 0.25);
                DetailMetricSplit(17, "  Write IOPS", vm.Iops, 0.75);
                DetailScalar(18, string.Empty, string.Empty);
                DetailMetric(19, "Latency", vm.Latency);
                Detail(21, "Status", vm.Status, StatusColor(vm.Status));
                WriteLine(24, VmDiskHeaderRow(), ConsoleColor.Yellow);
                for (var i = 0; i < vmDisks.Length; i++)
                {
                    var disk = vmDisks[i];
                    var row = VmDiskDataRow(disk);
                    var bg = i == selected ? ConsoleColor.DarkCyan : ConsoleColor.Black;
                    var fg = i == selected ? ConsoleColor.White : ConsoleColor.Gray;
                    WriteLine(25 + i, row, fg, bg);
                }
                var networkTop = 27 + vmDisks.Length;
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
                Detail(7, "Name", host.Name);
                DetailMetricWithCapacity(8, "CPU", host.Cpu, host.CpuCapacity);
                DetailMetricWithCapacity(9, "Memory", host.Mem, host.MemCapacity);
                DetailMetric(10, "I/O", host.Io);
                DetailMetric(11, "Network", host.Net);
                Detail(13, "Status", host.Status, StatusColor(host.Status));
                break;
            case DiskRow disk:
                Detail(7, "Name", disk.Name);
                Detail(8, "Size", disk.Size);
                DetailMetric(9, "Free", disk.Free);
                DetailMetric(10, "I/O", disk.Io);
                DetailMetric(11, "IOPS", disk.Iops);
                DetailMetric(12, "Queue depth", disk.QueueDepth);
                DetailMetric(13, "Latency", disk.Latency);
                Detail(15, "Status", disk.Status, StatusColor(disk.Status));
                break;
            case NetworkRow net:
                Detail(7, "Name", net.Name);
                Detail(8, "Link", net.Link);
                DetailMetric(9, "Throughput", net.Throughput);
                DetailMetric(10, "Receive", net.Rx);
                DetailMetric(11, "Transmit", net.Tx);
                DetailMetric(12, "Drops", net.Drops);
                Detail(14, "Status", net.Status, StatusColor(net.Status));
                break;
        }
    }

    private void Detail(int y, string label, string value, ConsoleColor color = ConsoleColor.Gray)
        => WriteLine(y, $"  {label,-18} {value}", color);

    private void DetailMetric(int y, string label, Metric metric)
        => DetailScalar(y, label, DetailMetricValue(metric));

    private void DetailMetricWithCapacity(int y, string label, Metric metric, string capacity)
        => DetailScalar(y, label, DetailMetricWithCapacityValue(metric, capacity));

    private void DetailMetricSplit(int y, string label, Metric metric, double ratio)
        => DetailScalar(y, label, DetailSplitValue(metric, ratio));

    private void DetailScalar(int y, string label, string value, ConsoleColor color = ConsoleColor.Gray)
        => WriteLine(y, $"  {label,-18} {value}", color);

    private object? ResolveDetailTarget()
    {
        if (string.IsNullOrEmpty(selectedItemName)) return null;
        var s = state.Read();
        return detailPanel switch
        {
            Panel.Hosts => s.Hosts.FirstOrDefault(r => r.Name == selectedItemName),
            Panel.Vms => s.Vms.FirstOrDefault(r => r.Name == selectedItemName && (string.IsNullOrEmpty(selectedHostName) || r.HostName == selectedHostName)),
            Panel.Disks => s.Disks.FirstOrDefault(r => r.Name == selectedItemName),
            Panel.Network => GetSwitchUplinkRows(s, selectedHostName).FirstOrDefault(r => r.Name == selectedItemName)
                ?? s.Networks.FirstOrDefault(r => r.Name == selectedItemName),
            _ => null
        };
    }

    private string DetailTitle(object row)
    {
        if (panel == Panel.Hosts && row is VmRow vm)
            return $"HOST {vm.HostName} -> VM {vm.Name}";
        if (panel == Panel.Network && row is NetworkRow net && !string.IsNullOrWhiteSpace(selectedHostName))
            return $"VSWITCH {selectedHostName} -> pNIC {net.Name}";
        return $"{detailPanel} detail: {GetRowName(row)}";
    }

    private static string GetRowName(object row) => row switch
    {
        HostRow host => host.Name,
        VmRow vm => vm.Name,
        DiskRow disk => disk.Name,
        NetworkSwitchRow networkSwitch => networkSwitch.Name,
        NetworkRow net => net.Name,
        EventRow evt => evt.Message,
        _ => string.Empty
    };

    private static VDiskRow[] GetVmDisks(VmRow vm, Snapshot snapshot)
    {
        return snapshot.VmTopology.FirstOrDefault(t => t.VmName == vm.Name)?.Disks ?? [];
    }

    private static VmNetworkPathRow[] GetVmNetworkAdapters(VmRow vm, Snapshot snapshot)
    {
        return snapshot.VmTopology.FirstOrDefault(t => t.VmName == vm.Name)?.Networks ?? [];
    }

    private static NetworkRow[] GetSwitchUplinkRows(Snapshot snapshot, string? switchName)
    {
        if (string.IsNullOrWhiteSpace(switchName))
            return [];

        var switchRow = snapshot.NetworkSwitches.FirstOrDefault(n => n.Name.Equals(switchName, StringComparison.OrdinalIgnoreCase));
        if (switchRow is null)
            return [];

        return switchRow.Uplinks
            .Select(uplink => NetworkTopologyMatcher.MergeWithLive(snapshot.Networks, uplink))
            .DistinctBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string Fmt(Metric metric) => FixedMetricCell(FmtValue(metric.Current, metric.Unit), FmtValue(metric.Max, metric.Unit), valueWidth: 9, width: MetricWidth);

    private static string FmtShort(Metric metric) => FixedMetricCell(FmtValue(metric.Current, metric.Unit), FmtValue(metric.Max, metric.Unit), valueWidth: 5, width: ShortMetricWidth);

    private static string FmtWithCapacity(Metric metric, string capacity) => FixedMetricCell(FmtValue(metric.Current, metric.Unit), FmtValue(metric.Max, metric.Unit), $"({capacity})", valueWidth: 4, configWidth: 11, width: CapacityMetricWidth);

    private static string DetailMetricValue(Metric metric)
        => $"{Cell(FmtValue(metric.Current, metric.Unit), 9, true)} | {Cell(FmtValue(metric.Max, metric.Unit), 9, true)}";

    private static string DetailMetricWithCapacityValue(Metric metric, string capacity)
        => $"{Cell(FmtValue(metric.Current, metric.Unit), 4, true)} | {Cell(FmtValue(metric.Max, metric.Unit), 4, true)} | {Cell($"({capacity})", 12)}";

    private static string DetailSplitValue(Metric metric, double ratio)
        => $"{Cell(FmtValue(metric.Current * ratio, metric.Unit), 9, true)} | {Cell(FmtValue(metric.Max * ratio, metric.Unit), 9, true)}";

    private static string FixedMetricCell(string current, string max, int valueWidth, int width)
        => Cell($"{Cell(current, valueWidth, true)} | {Cell(max, valueWidth, true)}", width, true);

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

    private static ConsoleColor StatusColor(string status) => status switch
    {
        "HOT" => ConsoleColor.Red,
        "OFF" => ConsoleColor.DarkGray,
        "BUSY" => ConsoleColor.Yellow,
        "IDLE" => ConsoleColor.Green,
        _ => ConsoleColor.Green
    };

    private static ConsoleColor RowColor(object row) => row switch
    {
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
            TableKind.HostLike => HostVersionWidth + CapacityMetricWidth + CapacityMetricWidth + MetricWidth + MetricWidth + StatusWidth,
            TableKind.VmLike => VmVersionWidth + CapacityMetricWidth + CapacityMetricWidth + MetricWidth + MetricWidth + StatusWidth,
            TableKind.DiskLike => SizeWidth + MetricWidth + MetricWidth + MetricWidth + ShortMetricWidth + ShortMetricWidth + StatusWidth,
            TableKind.NetworkSwitchLike => SwitchTypeWidth + UplinkWidth + LinkWidth + MetricWidth + MetricWidth + MetricWidth + StatusWidth,
            TableKind.NetworkLike => LinkWidth + MetricWidth + MetricWidth + MetricWidth + ShortMetricWidth + StatusWidth,
            _ => 0
        };

        var columns = kind switch
        {
            TableKind.HostLike => 7,
            TableKind.VmLike => 7,
            TableKind.DiskLike => 8,
            TableKind.NetworkLike => 6,
            _ => 2
        };

        var gaps = ColGap.Length * (columns - 1);
        var width = Console.WindowWidth - fixedWidths - gaps - 1;
        return Math.Clamp(width, MinNameWidth, MaxNameWidth);
    }

    private void BeginFrame()
    {
        var width = Console.WindowWidth;
        var height = Console.WindowHeight;

        if (width != previousWidth || height != previousHeight)
        {
            Console.Clear();
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
        for (var y = 0; y < previousLines.Length; y++)
        {
            if (!touchedLines[y] && !string.IsNullOrEmpty(previousLines[y]))
                WriteLine(y, string.Empty);
        }
    }

    private void WriteLine(int y, string text, ConsoleColor foreground = ConsoleColor.Gray, ConsoleColor background = ConsoleColor.Black)
    {
        if (y < 0 || y >= Console.WindowHeight) return;
        touchedLines[y] = true;

        var width = Console.WindowWidth;
        if (text.Length > width)
            text = text[..width];
        else if (text.Length < width)
            text = text.PadRight(width);

        if (previousLines[y] == text && previousForegrounds[y] == foreground && previousBackgrounds[y] == background)
            return;

        Console.SetCursorPosition(0, y);
        Console.ForegroundColor = foreground;
        Console.BackgroundColor = background;
        Console.Write(text);
        previousLines[y] = text;
        previousForegrounds[y] = foreground;
        previousBackgrounds[y] = background;
    }
}

internal enum Panel { Hosts, Vms, Disks, Network, Events }

internal enum DrillView { Overview, HostVms, NetworkAdapters, Detail }

internal enum TableKind { HostLike, VmLike, DiskLike, NetworkLike, NetworkSwitchLike }

internal sealed record ViewState(
    Panel Panel,
    int Selected,
    DrillView DrillView,
    Panel DetailPanel,
    string? SelectedHostName,
    string? SelectedItemName);
