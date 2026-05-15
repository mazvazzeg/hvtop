namespace hvtop.Native;

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
        var counterDebug = options.DebugCounters ? " --debug-counters" : string.Empty;
        var commandLine = $"\"{RemoteExePath}\" --port {options.RdcPort} --refresh {refresh} --history {history} --token {token}{logging}{counterDebug}";
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

