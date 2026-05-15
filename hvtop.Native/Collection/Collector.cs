namespace hvtop.Native;

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
        var sampleStarted = Stopwatch.GetTimestamp();
        var timings = options.DebugCounters ? new List<(string Name, double Ms)>() : null;
        void Mark(string name, long started)
        {
            timings?.Add((name, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        }

        var stepStarted = Stopwatch.GetTimestamp();
        pdh.Collect();
        var hostCpu = cpu.Read();
        var hostMem = memory.Read();
        var hostIo = diskBytes.Read() / 1024 / 1024;
        Mark("host-pdh", stepStarted);

        stepStarted = Stopwatch.GetTimestamp();
        var adapterRates = network.Sample();
        var visibleAdapterRates = adapterRates.Where(a => a.IsVisibleAdapter).ToArray();
        var hostNet = visibleAdapterRates.Sum(a => a.TotalBytesPerSecond + a.RdmaTotalBytesPerSecond) / 1024 / 1024;
        Mark("network", stepStarted);

        stepStarted = Stopwatch.GetTimestamp();
        var iops = diskIops.Read();
        var queue = diskQueue.Read();
        var latency = diskLatencySeconds.Read() * 1000;
        Mark("host-disk-pdh", stepStarted);

        var topologyRefreshRequested = false;
        if (refreshRequested)
        {
            stepStarted = Stopwatch.GetTimestamp();
            inventory.RequestRefresh();
            clusterInventory.RequestRefresh();
            topologyRefreshRequested = true;
            networkDiagnosticsLogged = false;
            Mark("refresh-request", stepStarted);
        }

        stepStarted = Stopwatch.GetTimestamp();
        var inventoryResult = inventory.TryRead();
        var clusterResult = clusterInventory.TryRead();
        var inventoryVms = inventoryResult.Vms;
        Mark("inventory-read", stepStarted);

        stepStarted = Stopwatch.GetTimestamp();
        LogicalDiskSampler.Refresh();
        Mark("logical-disk-refresh", stepStarted);

        stepStarted = Stopwatch.GetTimestamp();
        foreach (var evt in inventoryResult.Events)
            AddEvent(evt.Severity, evt.Message);
        if (!string.IsNullOrWhiteSpace(clusterResult.EventMessage))
            AddEvent(clusterResult.EventSeverity, clusterResult.EventMessage);
        Mark("event-merge", stepStarted);

        stepStarted = Stopwatch.GetTimestamp();
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
        Mark("host-row", stepStarted);

        stepStarted = Stopwatch.GetTimestamp();
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
                    Metric.QueueDepth(diskQueueDepth),
                    Metric.Milliseconds(diskLatencyMs),
                    Status.From(10, 100 - free, diskLatencyMs, diskQueueDepth));
                return row;
            })
            .ToArray();
        Mark("storage", stepStarted);

        stepStarted = Stopwatch.GetTimestamp();
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
                    Metric.Mbps((a.TotalBytesPerSecond + a.RdmaTotalBytesPerSecond) / 1024 / 1024),
                    Metric.Mbps(a.ReceivedBytesPerSecond / 1024 / 1024),
                    Metric.Mbps(a.SentBytesPerSecond / 1024 / 1024),
                    Metric.Mbps(a.RdmaTotalBytesPerSecond / 1024 / 1024),
                    Metric.Mbps(a.RdmaReceivedBytesPerSecond / 1024 / 1024),
                    Metric.Mbps(a.RdmaSentBytesPerSecond / 1024 / 1024),
                    Metric.Plain(a.DropsPerSecond),
                    Status.FromNetwork(a.TotalBytesPerSecond + a.RdmaTotalBytesPerSecond, a.LinkSpeedBitsPerSecond, a.IsUp),
                    a.PdhInstance,
                    a.RawReceivedBytesPerSecond,
                    a.RawSentBytesPerSecond,
                    a.PdhReceivedBytesPerSecond,
                    a.PdhSentBytesPerSecond,
                    a.RdmaInstance,
                    a.RdmaReceivedBytesPerSecond,
                    a.RdmaSentBytesPerSecond);
                return row;
            })
            .ToArray();
        Mark("adapter-rows", stepStarted);

        stepStarted = Stopwatch.GetTimestamp();
        var vms = hyperV.Collect(history, host.Name, inventoryVms);
        Mark("vm-counters", stepStarted);
        if (vms.Length == 0 && !inventory.IsRefreshing && !emptyVmInventoryLogged)
        {
            emptyVmInventoryLogged = true;
            AddEvent("INFO", "No Hyper-V VMs detected; VM pane is empty on this host.");
        }

        stepStarted = Stopwatch.GetTimestamp();
        var vmTopologyResult = topology.TryRead(disks, adapters);
        if (topologyRefreshRequested)
            topology.RequestRefresh();
        topology.RequestCheckpointRefreshIfDue(TimeSpan.FromSeconds(30));
        if (!string.IsNullOrWhiteSpace(vmTopologyResult.EventMessage))
            AddEvent(vmTopologyResult.EventSeverity, vmTopologyResult.EventMessage);
        Mark("topology-read", stepStarted);

        stepStarted = Stopwatch.GetTimestamp();
        var networkSwitches = topology.IsRefreshing && vmTopologyResult.Switches.Length == 0
            ? []
            : BuildNetworkSwitches(host.Name, adapters, vmTopologyResult.Switches);
        Mark("switches", stepStarted);

        stepStarted = Stopwatch.GetTimestamp();
        MaybeLogNetworkDiagnostics(refreshRequested, adapterRates, adapters, vmTopologyResult.Switches);
        var discovery = BuildDiscoveryProgress(host, vms, disks, adapters, networkSwitches);
        MaybeLogDiscoveryProgress(discovery, host, vms, disks, adapters, networkSwitches);
        Mark("diagnostics-discovery", stepStarted);

        stepStarted = Stopwatch.GetTimestamp();
        var liveTopology = EnrichTopologyWithLiveStats(vmTopologyResult.Topology)
            .Select(t => t with { HostName = host.Name })
            .ToArray();
        Mark("topology-live-stats", stepStarted);
        if (liveTopology.Length > 0)
        {
            stepStarted = Stopwatch.GetTimestamp();
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
            Mark("history-topology", stepStarted);

            stepStarted = Stopwatch.GetTimestamp();
            MergeRemoteTelemetry(ref hosts, ref vms, ref disks, ref networkSwitches, ref adapters, ref mergedTopology);
            MaybeAddSpikeEvent(host, disks);
            Mark("remote-events", stepStarted);
            MaybeAddCounterTiming(timings, sampleStarted);
            return new Snapshot(DateTime.Now, clusterResult.Clusters, hosts, vms, disks, networkSwitches, adapters, events, mergedTopology, !initialDiscoveryComplete, inventory.IsRefreshing, topology.IsRefreshing, discovery);
        }

        stepStarted = Stopwatch.GetTimestamp();
        host = history.Apply("host:" + host.Name, host);
        hosts = BuildHosts(host, clusterResult.Nodes);
        liveTopology = liveTopology.Select(t => history.Apply("vmtopo:" + t.HostName + ":" + t.VmName, t)).ToArray();
        disks = disks.Select(d => history.Apply("disk:" + d.HostName + ":" + d.Name, d)).ToArray();
        networkSwitches = networkSwitches.Select(n => history.Apply("vswitch:" + n.HostName + ":" + n.Name, n)).ToArray();
        adapters = adapters.Select(n => history.Apply("net:" + n.HostName + ":" + n.Name, n)).ToArray();
        Mark("history", stepStarted);

        stepStarted = Stopwatch.GetTimestamp();
        MergeRemoteTelemetry(ref hosts, ref vms, ref disks, ref networkSwitches, ref adapters, ref liveTopology);
        MaybeAddSpikeEvent(host, disks);
        Mark("remote-events", stepStarted);
        MaybeAddCounterTiming(timings, sampleStarted);
        return new Snapshot(DateTime.Now, clusterResult.Clusters, hosts, vms, disks, networkSwitches, adapters, events, liveTopology, !initialDiscoveryComplete, inventory.IsRefreshing, topology.IsRefreshing, discovery);
    }

    private void MaybeAddCounterTiming(List<(string Name, double Ms)>? timings, long sampleStarted)
    {
        if (timings is null)
            return;

        var total = Stopwatch.GetElapsedTime(sampleStarted).TotalMilliseconds;
        var slow = timings
            .Where(t => t.Ms >= 25)
            .OrderByDescending(t => t.Ms)
            .Take(8)
            .Select(t => $"{t.Name}={t.Ms:N0}ms");
        AddEvent("INFO", $"COUNTERS total={total:N0}ms {string.Join(" ", slow)}");
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
        => nodeStatus.Equals("HOT", StringComparison.OrdinalIgnoreCase) ? "HOT" : metricStatus;

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
                var rdmaThroughput = uplinks.Sum(a => a.RdmaThroughput.Current);
                var rdmaRx = uplinks.Sum(a => a.RdmaRx.Current);
                var rdmaTx = uplinks.Sum(a => a.RdmaTx.Current);
                if (hyperVSwitchRates.TryGetValue(switchRow.Name, out var switchRate))
                {
                    var switchRx = switchRate.ReceivedBytesPerSecond / 1024d / 1024d;
                    var switchTx = switchRate.SentBytesPerSecond / 1024d / 1024d;
                    rx = Math.Max(rx, switchRx);
                    tx = Math.Max(tx, switchTx);
                    throughput = Math.Max(throughput, switchRx + switchTx + rdmaThroughput);
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
                    Metric.Mbps(rdmaThroughput),
                    Metric.Mbps(rdmaRx),
                    Metric.Mbps(rdmaTx),
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
            }).ToArray(),
            Checkpoints = EnsureActiveCheckpointRows(vm.Checkpoints, vm.Disks).Select(checkpoint => checkpoint with
            {
                SizeMb = ReadCheckpointSizeMb(checkpoint.Path)
            }).ToArray()
        }).ToArray();
    }

    private static VmCheckpointRow[] EnsureActiveCheckpointRows(VmCheckpointRow[] checkpoints, VDiskRow[] disks)
    {
        var activeRows = disks
            .Where(disk => disk.Path.EndsWith(".avhdx", StringComparison.OrdinalIgnoreCase))
            .Select(disk => new VmCheckpointRow("Now / Active differencing disk", string.Empty, disk.Path, string.Empty, DateTime.MinValue));

        return checkpoints
            .Concat(activeRows)
            .Where(row => !string.IsNullOrWhiteSpace(row.Name) || !string.IsNullOrWhiteSpace(row.Path))
            .DistinctBy(row => string.IsNullOrWhiteSpace(row.Path) ? $"checkpoint:{row.Name}:{row.Created:O}" : $"path:{row.Path}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static double ReadCheckpointSizeMb(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return 0;

            return new FileInfo(path).Length / 1024d / 1024d;
        }
        catch
        {
            return 0;
        }
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
        var totalRdmaHardwareMbps = hardware.Sum(a => a.RdmaTotalBytesPerSecond) / 1024d / 1024d;
        var pdhRates = NetworkPdhSampler.LastRates;
        var pdhMatched = adapterRates.Count(a => !string.IsNullOrWhiteSpace(a.PdhInstance));
        var rdmaRates = RdmaPdhSampler.LastRates;
        var rdmaMatched = adapterRates.Count(a => !string.IsNullOrWhiteSpace(a.RdmaInstance));
        AddEvent("INFO", $"NETDIAG live={adapterRates.Length} visible={hardware.Length} up={upHardware} sw={switches.Length} pdh={pdhRates.Length} matched={pdhMatched} rdma={rdmaRates.Length} rdmaMatched={rdmaMatched} throughput={totalHardwareMbps:0.00} MB/s rdmaThroughput={totalRdmaHardwareMbps:0.00} MB/s");

        foreach (var pdh in pdhRates
                     .OrderByDescending(p => p.ReceivedBytesPerSecond + p.SentBytesPerSecond)
                     .Take(8))
        {
            AddEvent(
                "INFO",
                $"NETPDH inst='{TrimForEvent(pdh.Instance, 48)}' rx={pdh.ReceivedBytesPerSecond / 1024d / 1024d:0.00} tx={pdh.SentBytesPerSecond / 1024d / 1024d:0.00} MB/s");
        }

        foreach (var rdma in rdmaRates
                     .OrderByDescending(p => p.ReceivedBytesPerSecond + p.SentBytesPerSecond)
                     .Take(8))
        {
            AddEvent(
                "INFO",
                $"NETRDMA inst='{TrimForEvent(rdma.Instance, 48)}' rx={rdma.ReceivedBytesPerSecond / 1024d / 1024d:0.00} tx={rdma.SentBytesPerSecond / 1024d / 1024d:0.00} MB/s");
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
                $"NETIF name='{TrimForEvent(adapter.Name, 28)}' desc='{TrimForEvent(adapter.Description, 42)}' guid='{adapter.InterfaceId}' hw={adapter.IsHardwareInterface} visible={adapter.IsVisibleAdapter} up={adapter.IsUp} link={NetworkLinkFormatter.Format(adapter.LinkSpeedBitsPerSecond, adapter.IsUp)} rx={adapter.ReceivedBytesPerSecond / 1024d / 1024d:0.00} tx={adapter.SentBytesPerSecond / 1024d / 1024d:0.00} rawRx={adapter.RawReceivedBytesPerSecond / 1024d / 1024d:0.00} rawTx={adapter.RawSentBytesPerSecond / 1024d / 1024d:0.00} pdh='{TrimForEvent(adapter.PdhInstance, 32)}' pdhRx={adapter.PdhReceivedBytesPerSecond / 1024d / 1024d:0.00} pdhTx={adapter.PdhSentBytesPerSecond / 1024d / 1024d:0.00} rdma='{TrimForEvent(adapter.RdmaInstance, 32)}' rdmaRx={adapter.RdmaReceivedBytesPerSecond / 1024d / 1024d:0.00} rdmaTx={adapter.RdmaSentBytesPerSecond / 1024d / 1024d:0.00} MB/s");
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

