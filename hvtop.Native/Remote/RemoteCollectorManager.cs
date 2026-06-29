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
    private readonly HttpClient http;
    private readonly RdcTarget[] configTargets;
    private readonly string token;
    private bool clusterDetectedLogged;
    private bool explicitTargetLogged;
    private bool configTargetLogged;
    private string lastTargetSummary = string.Empty;

    public RemoteCollectorManager(Options options)
    {
        this.options = options;
        http = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = options.RdcTimeout
        })
        {
            Timeout = options.RdcTimeout
        };
        token = string.IsNullOrWhiteSpace(options.RdcToken)
            ? Guid.NewGuid().ToString("N")
            : options.RdcToken;
        var config = RdcConfigLoader.Load(options.RdcConfig);
        configTargets = config.Targets
            .Select(t => t with
            {
                Token = string.IsNullOrWhiteSpace(t.Token) ? options.RdcToken : t.Token,
                SkipDeploy = t.SkipDeploy ?? options.RdcSkipDeploy
            })
            .ToArray();
        foreach (var evt in config.Events)
            Enqueue(evt.Severity, evt.Message);
    }

    public void UpdateTargets(ClusterNodeRow[] nodes, string localHost)
    {
        if (!options.RemoteCollectors)
            return;

        var clusterWanted = nodes
            .Where(n => n.Status != "HOT")
            .Select(n => n.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name)
                           && !name.Equals(localHost, StringComparison.OrdinalIgnoreCase)
                           && !name.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            .Select(name => new RdcTarget(name, options.RdcUser, options.RdcPassword, "cluster", null, options.RdcPort, options.RdcToken, options.RdcSkipDeploy))
            .ToArray();

        var explicitWanted = string.IsNullOrWhiteSpace(options.RdcHost)
            ? Array.Empty<RdcTarget>()
            : new[] { new RdcTarget(options.RdcHost.Trim(), options.RdcUser, options.RdcPassword, "explicit", null, options.RdcPort, options.RdcToken, options.RdcSkipDeploy) };
        var wanted = explicitWanted
            .Concat(configTargets)
            .Concat(clusterWanted)
            .Where(t => !string.IsNullOrWhiteSpace(t.Host))
            .GroupBy(t => t.Host, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();

        if (explicitWanted.Length > 0 && !explicitTargetLogged)
        {
            explicitTargetLogged = true;
            Enqueue("INFO", $"Explicit RDC host configured, deploying hvtop remote data collection agent to {explicitWanted[0].Host} on TCP/{PortFor(explicitWanted[0])}");
            Enqueue("INFO", $"RDC {explicitWanted[0].Host}: using {explicitWanted[0].CredentialMode} for ADMIN$ and CIM access");
        }

        if (configTargets.Length > 0 && !configTargetLogged)
        {
            configTargetLogged = true;
            Enqueue("INFO", $"RDC config target(s) configured: {string.Join(", ", configTargets.Select(t => t.Host))}");
        }

        if (nodes.Length > 1 && !clusterDetectedLogged)
        {
            clusterDetectedLogged = true;
            Enqueue("INFO", $"Cluster detected, deploying hvtop remote data collection agent to peer node(s) on TCP/{options.RdcPort}");
        }

        var targetSummary = wanted.Length == 0 ? "(none)" : string.Join(", ", wanted.Select(t => t.Host));
        if (!targetSummary.Equals(lastTargetSummary, StringComparison.OrdinalIgnoreCase))
        {
            lastTargetSummary = targetSummary;
            Enqueue("INFO", $"RDC target nodes: {targetSummary}");
        }

        lock (gate)
        {
            foreach (var target in wanted)
            {
                if (sessions.ContainsKey(target.Host))
                    continue;

                var session = new RemoteNodeSession(target);
                sessions[target.Host] = session;
                Enqueue("INFO", $"RDC {target.Host}: scheduling remote collector deployment");
                if (SkipDeployFor(target))
                    Enqueue("INFO", $"RDC {target.Host}: skip deploy enabled; polling manually deployed hvtop-rdc");
                session.Worker = Task.Run(() => RunNodeAsync(session));
            }

            var wantedNames = wanted.Select(t => t.Host).ToArray();
            foreach (var node in sessions.Keys.Except(wantedNames, StringComparer.OrdinalIgnoreCase).ToArray())
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

    public bool HasTerminalFailure
    {
        get
        {
            lock (gate)
                return sessions.Count > 0 && sessions.Values.All(s => s.Cts.IsCancellationRequested && s.Latest is null);
        }
    }

    public string TerminalFailureSummary
    {
        get
        {
            lock (gate)
            {
                var failed = sessions.Values
                    .Where(s => s.Cts.IsCancellationRequested && s.Latest is null)
                    .Select(s => string.IsNullOrWhiteSpace(s.LastError) ? s.NodeName : $"{s.NodeName}: {s.LastError}")
                    .ToArray();
                return failed.Length == 0 ? "remote data collection failed" : string.Join("; ", failed);
            }
        }
    }

    public string StatusSummary
    {
        get
        {
            if (!options.RemoteCollectors)
                return "disabled";

            lock (gate)
            {
                if (sessions.Count == 0)
                    return "idle";

                var total = sessions.Count;
                var connected = sessions.Values.Count(s => s.Latest is not null && !s.Cts.IsCancellationRequested);
                var failed = sessions.Values.Count(s => s.Cts.IsCancellationRequested && s.Latest is null);
                var polling = sessions.Values.Count(s => s.PollFailures > 0 && !s.Cts.IsCancellationRequested);
                if (connected == total)
                    return total == 1 ? "connected" : $"connected {connected}/{total}";
                if (connected > 0)
                    return $"partial {connected}/{total}";
                if (failed == total)
                    return total == 1 ? "failed" : $"failed {failed}/{total}";
                if (polling > 0)
                    return $"polling issue {polling}/{total}";

                var state = sessions.Values.Select(s => s.LastState).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
                return string.IsNullOrWhiteSpace(state) ? "starting" : state;
            }
        }
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
                rows[host.Name] = rows.TryGetValue(host.Name, out var previous)
                    ? PreserveFiniteHostCurrents(previous, host)
                    : host;
        }

        return rows.Values.OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static HostRow PreserveFiniteHostCurrents(HostRow previous, HostRow next)
        => next with
        {
            Cpu = PreserveFiniteCurrent(previous.Cpu, next.Cpu),
            Mem = PreserveFiniteCurrent(previous.Mem, next.Mem),
            Ram = next.Ram with
            {
                InUse = PreserveFiniteCurrent(previous.Ram.InUse, next.Ram.InUse),
                Processes = PreserveFiniteCurrent(previous.Ram.Processes, next.Ram.Processes),
                Kernel = PreserveFiniteCurrent(previous.Ram.Kernel, next.Ram.Kernel),
                Modified = PreserveFiniteCurrent(previous.Ram.Modified, next.Ram.Modified),
                StandbyCache = PreserveFiniteCurrent(previous.Ram.StandbyCache, next.Ram.StandbyCache),
                Free = PreserveFiniteCurrent(previous.Ram.Free, next.Ram.Free)
            },
            Io = PreserveFiniteCurrent(previous.Io, next.Io),
            Net = PreserveFiniteCurrent(previous.Net, next.Net)
        };

    private static Metric PreserveFiniteCurrent(Metric previous, Metric next)
        => IsFinite(next.Current) || !IsFinite(previous.Current)
            ? next
            : next with { Current = previous.Current };

    private static bool IsFinite(double value)
        => !double.IsNaN(value) && !double.IsInfinity(value);

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

    public static PhysicalDiskRow[] MergePhysicalDisks(PhysicalDiskRow[] local, RemoteSnapshot[] remote)
    {
        return local
            .Concat(remote.SelectMany(r => r.Snapshot.PhysicalDisks.Select(d => string.IsNullOrWhiteSpace(d.HostName) ? d with { HostName = r.NodeName } : d)))
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
                    if (SkipDeployFor(session.Target))
                    {
                        if (string.IsNullOrWhiteSpace(session.Target.Token) && string.IsNullOrWhiteSpace(options.RdcToken))
                            throw new InvalidOperationException("skip deploy requires --rdc-token, TOKEN in RDC config, or per-host config token");
                        SetState(session, "skip-deploy");
                    }
                    else
                    {
                        SetState(session, "deploying");
                        DeployAndStart(session);
                    }
                    session.Deployed = true;
                    session.DeployedAt = DateTime.UtcNow;
                    session.RedeployRequested = false;
                    if (!SkipDeployFor(session.Target))
                        SetState(session, "started");
                }

                var snapshot = await PollAsync(session).ConfigureAwait(false);
                CheckRemoteVersion(session, snapshot);
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
                var tcpProbe = await TcpProbeAsync(session.PollHost ?? session.NodeName, PortFor(session), options.RdcTimeout, session.Cts.Token).ConfigureAwait(false);
                SetState(session, "poll-error", $"HTTP poll failed ({session.PollFailures}): {Trim(ex.Message, 100)}; TCP probe {tcpProbe}");
                MaybeScheduleRedeploy(session);
            }
            catch (TaskCanceledException ex) when (!session.Cts.IsCancellationRequested)
            {
                session.PollFailures++;
                session.PollEndpoint = null;
                var tcpProbe = await TcpProbeAsync(session.PollHost ?? session.NodeName, PortFor(session), options.RdcTimeout, session.Cts.Token).ConfigureAwait(false);
                SetState(session, "poll-error", $"HTTP poll timed out ({session.PollFailures}): {Trim(ex.Message, 100)}; TCP probe {tcpProbe}");
                MaybeScheduleRedeploy(session);
            }
            catch (Exception ex) when (IsCredentialFailure(ex))
            {
                SetState(session, "auth-error", Trim(ex.Message, 160));
                session.Cancel();
            }
            catch (Exception ex) when (IsRemoteSetupFailure(ex))
            {
                SetState(session, "remote-error", Trim(ex.Message, 180));
                session.Cancel();
            }
            catch (Exception ex)
            {
                SetState(session, "error", $"deploy/start failed: {Trim(ex.Message, 120)}");
                if (session.FirstDataAt is null)
                    session.Cancel();
                else
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
        if (SkipDeployFor(session.Target) || session.FirstDataAt is not null || session.RedeployRequested)
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
        SetState(session, "checking");
        StopRemoteProcess(session);

        SetState(session, "copying");
        if (!session.Target.HasCredentials)
            Directory.CreateDirectory(remoteShareDir);
        CopyRemoteCollector(localExe, Path.Combine(remoteShareDir, "hvtop-rdc.exe"), session);

        var refresh = options.RemoteRefresh.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var history = options.History.TotalMinutes.ToString("0.###", CultureInfo.InvariantCulture);
        var logging = options.DebugLog ? " --debug-log" : string.Empty;
        var counterDebug = options.DebugCounters ? " --debug-counters" : string.Empty;
        var commandLine = $"\"{RemoteExePath}\" --port {PortFor(session)} --refresh {refresh} --history {history} --token {WinArg(TokenFor(session))}{logging}{counterDebug}";
        var cimScript =
            "$ErrorActionPreference='Stop'; " +
            $"$node={PsSingle(session.NodeName)}; $cmd={PsSingle(commandLine)}; " +
            CimSessionScript(session.Target) +
            "try { " +
            "Invoke-CimMethod -CimSession $hvtopCimSession -ClassName Win32_Process -MethodName Create -Arguments @{CommandLine=$cmd} | Out-Null; " +
            "} finally { if ($hvtopCimSession) { Remove-CimSession -CimSession $hvtopCimSession -ErrorAction SilentlyContinue } }";

        SetState(session, "starting");
        if (!PowerShellRunner.TryRun(cimScript, TimeoutMs, out _, out var error, out var exitCode, out var timedOut))
        {
            var cimError = timedOut
                ? $"remote CIM process start timed out after {TimeoutSecondsText} using {CredentialMode(session.Target)}"
                : $"remote CIM process start failed using {CredentialMode(session.Target)} exit={exitCode}: {Trim(error, 160)}";
            if (!IsWinRmFailure(error))
                throw new InvalidOperationException(cimError);

            Enqueue("WARN", $"RDC {session.NodeName}: CIM/WinRM start failed; trying legacy WMI/DCOM fallback");
            var wmiScript =
                "$ErrorActionPreference='Stop'; " +
                $"$node={PsSingle(session.NodeName)}; $cmd={PsSingle(commandLine)}; " +
                WmiArgsScript(session.Target) +
                "Invoke-WmiMethod @hvtopWmi -Class Win32_Process -Name Create -ArgumentList $cmd | Out-Null";

            if (!PowerShellRunner.TryRun(wmiScript, TimeoutMs, out _, out error, out exitCode, out timedOut))
                throw new InvalidOperationException(timedOut
                    ? $"remote WMI/DCOM process start timed out after {TimeoutSecondsText} using {CredentialMode(session.Target)} after CIM/WinRM failed"
                    : $"remote WMI/DCOM process start failed using {CredentialMode(session.Target)} after CIM/WinRM failed exit={exitCode}: {Trim(error, 160)}");

            Enqueue("INFO", $"RDC {session.NodeName}: remote WMI/DCOM process start requested using {CredentialMode(session.Target)}");
            return;
        }

        Enqueue("INFO", $"RDC {session.NodeName}: remote CIM process start requested using {CredentialMode(session.Target)}");
    }

    private void StopRemoteProcess(RemoteNodeSession session)
    {
        var cimScript =
            "$ErrorActionPreference='Stop'; " +
            $"$node={PsSingle(session.NodeName)}; " +
            CimSessionScript(session.Target) +
            "try { " +
            "$procs=Get-CimInstance -CimSession $hvtopCimSession -ClassName Win32_Process -Filter \"Name='hvtop-rdc.exe'\"; " +
            "if ($procs) { $procs | Invoke-CimMethod -MethodName Terminate | Out-Null }; " +
            "} finally { if ($hvtopCimSession) { Remove-CimSession -CimSession $hvtopCimSession -ErrorAction SilentlyContinue } }";
        if (!PowerShellRunner.TryRun(cimScript, TimeoutMs, out _, out var error, out var exitCode, out var timedOut))
        {
            var cimError = timedOut
                ? $"remote CIM process stop timed out after {TimeoutSecondsText} using {CredentialMode(session.Target)}"
                : $"remote CIM process stop failed using {CredentialMode(session.Target)} exit={exitCode}: {Trim(error, 160)}";
            if (!IsWinRmFailure(error))
                throw new InvalidOperationException(cimError);

            Enqueue("WARN", $"RDC {session.NodeName}: CIM/WinRM stop failed; trying legacy WMI/DCOM fallback");
            var wmiScript =
                "$ErrorActionPreference='Stop'; " +
                $"$node={PsSingle(session.NodeName)}; " +
                WmiArgsScript(session.Target) +
                "$procs=Get-WmiObject @hvtopWmi -Class Win32_Process -Filter \"Name='hvtop-rdc.exe'\"; " +
                "foreach ($proc in @($procs)) { $null=$proc.Terminate() }";

            if (!PowerShellRunner.TryRun(wmiScript, TimeoutMs, out _, out error, out exitCode, out timedOut))
                throw new InvalidOperationException(timedOut
                    ? $"remote WMI/DCOM process stop timed out after {TimeoutSecondsText} using {CredentialMode(session.Target)} after CIM/WinRM failed"
                    : $"remote WMI/DCOM process stop failed using {CredentialMode(session.Target)} after CIM/WinRM failed exit={exitCode}: {Trim(error, 160)}");
        }
        Thread.Sleep(500);
    }

    private void CopyRemoteCollector(string source, string destination, RemoteNodeSession session)
    {
        if (session.Target.HasCredentials)
        {
            CopyRemoteCollectorWithCredential(source, session);
            return;
        }

        try
        {
            File.Copy(source, destination, overwrite: true);
            Enqueue("INFO", $"RDC {session.NodeName}: ADMIN$ copy succeeded using {CredentialMode(session.Target)}");
            return;
        }
        catch (IOException ex)
        {
            Enqueue("WARN", $"RDC {session.NodeName}: remote collector exe was locked, stopping old agent and retrying copy");
            StopRemoteProcess(session);
            Thread.Sleep(1000);
            try
            {
                File.Copy(source, destination, overwrite: true);
                Enqueue("INFO", $"RDC {session.NodeName}: ADMIN$ copy succeeded after retry using {CredentialMode(session.Target)}");
                return;
            }
            catch (IOException)
            {
                throw new IOException($"copy failed after stopping old agent: {ex.Message}");
            }
        }
    }

    private void CopyRemoteCollectorWithCredential(string source, RemoteNodeSession session)
    {
        var script =
            "$ErrorActionPreference='Stop'; " +
            $"$node={PsSingle(session.NodeName)}; $source={PsSingle(source)}; " +
            CredentialScript(session.Target) +
            "$drive='HVTOPRDC' + [guid]::NewGuid().ToString('N').Substring(0,8); " +
            "try { " +
            "New-PSDrive -Name $drive -PSProvider FileSystem -Root \"\\\\$node\\ADMIN$\" -Credential $hvtopCred -ErrorAction Stop | Out-Null; " +
            "$target=\"$drive`:\\Temp\\hvtop-rdc\"; " +
            "New-Item -ItemType Directory -Path $target -Force | Out-Null; " +
            "Copy-Item -LiteralPath $source -Destination (Join-Path $target 'hvtop-rdc.exe') -Force; " +
            "} finally { Remove-PSDrive -Name $drive -Force -ErrorAction SilentlyContinue }";

        if (!PowerShellRunner.TryRun(script, CopyTimeoutMs, out _, out var error, out var exitCode, out var timedOut))
            throw new InvalidOperationException(timedOut
                ? $"remote ADMIN$ copy timed out after {CopyTimeoutSecondsText} using {CredentialMode(session.Target)}"
                : $"remote ADMIN$ copy failed using {CredentialMode(session.Target)} exit={exitCode}: {Trim(error, 140)}");

        Enqueue("INFO", $"RDC {session.NodeName}: ADMIN$ copy succeeded using {CredentialMode(session.Target)}");
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
        request.Headers.TryAddWithoutValidation("X-Hvtop-Token", TokenFor(session));
        using var response = await http.SendAsync(request, session.Cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(session.Cts.Token).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, HvtopJsonContext.Default.Snapshot, session.Cts.Token).ConfigureAwait(false)
               ?? throw new InvalidOperationException("remote snapshot was empty");
    }

    private void CheckRemoteVersion(RemoteNodeSession session, Snapshot snapshot)
    {
        if (session.VersionWarningLogged)
            return;

        session.VersionWarningLogged = true;
        var local = VersionBase(Program.DisplayVersion);
        var remoteRaw = snapshot.RdcCollectorVersion;
        if (string.IsNullOrWhiteSpace(remoteRaw))
        {
            Enqueue("WARN", $"RDC {session.NodeName}: remote version not reported. Local: v{local}. Remote: unknown; collector may be older than local.");
            return;
        }

        var remote = VersionBase(remoteRaw);
        if (!string.Equals(local, remote, StringComparison.OrdinalIgnoreCase))
            Enqueue("WARN", $"RDC {session.NodeName}: RDC version mismatch detected. Local: v{local}. Remote: v{remote}");
    }

    private static string VersionBase(string version)
    {
        var text = (version ?? string.Empty).Trim();
        var space = text.LastIndexOf(' ');
        if (space >= 0)
            text = text[(space + 1)..].Trim();

        var plus = text.IndexOf('+');
        if (plus >= 0)
            text = text[..plus];

        text = text.Replace("-rdc", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return string.IsNullOrWhiteSpace(text) ? "unknown" : text;
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
            var port = PortFor(session);
            var probe = await TcpProbeAsync(probeHost, port, options.RdcTimeout, session.Cts.Token).ConfigureAwait(false);
            Enqueue("INFO", $"RDC {session.NodeName}: TCP probe {uriHost}:{port} {probe}");
            if (!probe.Equals("connect OK", StringComparison.OrdinalIgnoreCase))
                continue;

            session.PollHost = probeHost;
            session.PollEndpoint = $"http://{uriHost}:{port}/snapshot";
            session.PollingLogged = false;
            return session.PollEndpoint;
        }

        session.PollHost = session.NodeName;
        session.PollEndpoint = $"http://{session.NodeName}:{PortFor(session)}/snapshot";
        session.PollingLogged = false;
        return session.PollEndpoint;
    }

    private static string FormatUriHost(IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();

    private static async Task<string> TcpProbeAsync(string host, int port, TimeSpan timeoutValue, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(timeoutValue);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return "connect OK";
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return $"connect timeout after {timeoutValue.TotalSeconds:N0}s";
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
        if (state is "error" or "auth-error" or "remote-error")
            session.LastError = detail;
        var severity = state is "error" or "auth-error" or "remote-error" ? "WARN" : "INFO";
        var message = state switch
        {
            "deploying" => $"RDC {session.NodeName}: deploying hvtop-rdc",
            "skip-deploy" => $"RDC {session.NodeName}: skip deploy enabled, polling existing hvtop-rdc on TCP/{PortFor(session)}",
            "checking" => $"RDC {session.NodeName}: checking CIM access and stopping old hvtop-rdc process if present using {CredentialMode(session.Target)}",
            "copying" => $"RDC {session.NodeName}: testing ADMIN$ access and copying hvtop-rdc.exe using {CredentialMode(session.Target)}",
            "starting" => $"RDC {session.NodeName}: starting remote collector through CIM using {CredentialMode(session.Target)}",
            "started" => $"RDC {session.NodeName}: deployed hvtop-rdc on TCP/{PortFor(session)}",
            "connected" => $"RDC {session.NodeName}: connected, polling every {options.RemoteRefresh.TotalSeconds:N0}s",
            "poll-error" => $"RDC {session.NodeName}: {detail}; keeping remote collector running",
            "auth-error" => AuthFailureMessage(session),
            "remote-error" => RemoteFailureMessage(session, detail),
            _ => $"RDC {session.NodeName}: {detail}"
        };
        Enqueue(severity, message);
    }

    private string AuthFailureMessage(RemoteNodeSession session)
        => session.Target.Source.Equals("config", StringComparison.OrdinalIgnoreCase) && session.Target.LineNumber is { } line
            ? $"RDC config line {line} skipped: Invalid credentials."
            : $"RDC {session.NodeName}: invalid credentials; target skipped.";

    private string RemoteFailureMessage(RemoteNodeSession session, string detail)
        => detail.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            ? $"RDC {session.NodeName}: timed out after {TimeoutSecondsText}; target skipped."
            : $"RDC {session.NodeName}: remote setup failed ({detail}); target skipped.";

    private void Enqueue(string severity, string message)
    {
        RdcLog.Info($"{severity} {message}");
        events.Enqueue(new EventRow(DateTime.Now, severity, message));
    }

    private static string PsSingle(string value) => "'" + value.Replace("'", "''") + "'";

    private static string WinArg(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private string TokenFor(RemoteNodeSession session)
        => string.IsNullOrWhiteSpace(session.Target.Token) ? token : session.Target.Token;

    private int PortFor(RemoteNodeSession session)
        => PortFor(session.Target);

    private int PortFor(RdcTarget target)
        => target.Port ?? options.RdcPort;

    private static bool SkipDeployFor(RdcTarget target)
        => target.SkipDeploy == true;

    private string CredentialScript(RdcTarget target)
    {
        if (!target.HasCredentials)
            return "$hvtopCred=$null; ";

        var password = target.Password ?? string.Empty;
        return "$hvtopPassword=ConvertTo-SecureString " + PsSingle(password) + " -AsPlainText -Force; " +
               "$hvtopCred=[pscredential]::new(" + PsSingle(target.User!) + ", $hvtopPassword); ";
    }

    private string CimSessionScript(RdcTarget target)
        => CredentialScript(target) +
           (!target.HasCredentials
               ? "$hvtopCimSession=New-CimSession -ComputerName $node; "
               : "$hvtopCimSession=New-CimSession -ComputerName $node -Credential $hvtopCred; ");

    private string WmiArgsScript(RdcTarget target)
        => CredentialScript(target) + "$hvtopWmi=@{ComputerName=$node}; if ($hvtopCred) { $hvtopWmi['Credential']=$hvtopCred }; ";

    private static string CredentialMode(RdcTarget target)
        => target.CredentialMode;

    private int TimeoutMs
        => Math.Max(1000, (int)Math.Ceiling(options.RdcTimeout.TotalMilliseconds));

    private string TimeoutSecondsText
        => $"{options.RdcTimeout.TotalSeconds:N0}s";

    private int CopyTimeoutMs
        => Math.Max(1000, (int)Math.Ceiling(options.RdcCopyTimeout.TotalMilliseconds));

    private string CopyTimeoutSecondsText
        => $"{options.RdcCopyTimeout.TotalSeconds:N0}s";

    private static TimeSpan ShutdownStopTimeout
        => TimeSpan.FromSeconds(2);

    private static string Trim(string value, int max)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= max ? value : value[..max];
    }

    private static bool IsCredentialFailure(Exception ex)
    {
        var text = ex.ToString();
        return ContainsAny(text,
            "access is denied",
            "logon failure",
            "unknown user name or bad password",
            "the user name or password is incorrect",
            "the referenced account is currently locked out",
            "the account is disabled",
            "the password has expired",
            "unauthorized",
            "401");
    }

    private static bool IsRemoteSetupFailure(Exception ex)
    {
        var text = ex.ToString();
        return IsWinRmFailure(text) || ContainsAny(text,
            "the rpc server is unavailable",
            "the network path was not found",
            "a network-related error occurred",
            "the specified network name is no longer available",
            "could not connect",
            "actively refused");
    }

    private static bool IsWinRmFailure(string text)
        => ContainsAny(text,
            "winrm",
            "wsman",
            "new-cimsession",
            "the client cannot connect to the destination specified in the request",
            "cannot process the request");

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public void Dispose()
    {
        RemoteNodeSession[] copy;
        lock (gate)
            copy = sessions.Values.ToArray();

        var stopTasks = copy
            .Select(session => Task.Run(() => StopRemoteCollectorOnShutdown(session)))
            .ToArray();
        try
        {
            Task.WaitAll(stopTasks);
        }
        catch
        {
        }

        http.Dispose();
    }

    private void StopRemoteCollectorOnShutdown(RemoteNodeSession session)
    {
        session.Cancel();
        if (SkipDeployFor(session.Target))
        {
            Enqueue("INFO", $"RDC {session.NodeName}: skip deploy target left running");
            return;
        }

        try
        {
            var stopHost = session.PollHost ?? session.NodeName;
            using var stop = new HttpRequestMessage(HttpMethod.Get, $"http://{stopHost}:{PortFor(session)}/stop");
            stop.Headers.TryAddWithoutValidation("X-Hvtop-Token", TokenFor(session));
            using var timeout = new CancellationTokenSource(ShutdownStopTimeout);
            http.Send(stop, timeout.Token);
            Enqueue("INFO", $"RDC {session.NodeName}: stop requested, preserving remote log at \\\\{session.NodeName}\\ADMIN$\\{RemoteInstallRelative}\\hvtop-rdc.log");
        }
        catch
        {
            Enqueue("WARN", $"RDC {session.NodeName}: stop request failed, preserving remote log at \\\\{session.NodeName}\\ADMIN$\\{RemoteInstallRelative}\\hvtop-rdc.log");
        }
    }

    private void DeleteRemoteCollector(RemoteNodeSession session)
    {
        if (!session.Target.HasCredentials)
        {
            File.Delete($@"\\{session.NodeName}\ADMIN$\{RemoteInstallRelative}\hvtop-rdc.exe");
            return;
        }

        var script =
            "$ErrorActionPreference='SilentlyContinue'; " +
            $"$node={PsSingle(session.NodeName)}; " +
            CredentialScript(session.Target) +
            "$drive='HVTOPRDC' + [guid]::NewGuid().ToString('N').Substring(0,8); " +
            "try { " +
            "New-PSDrive -Name $drive -PSProvider FileSystem -Root \"\\\\$node\\ADMIN$\" -Credential $hvtopCred -ErrorAction Stop | Out-Null; " +
            "Remove-Item -LiteralPath \"$drive`:\\Temp\\hvtop-rdc\\hvtop-rdc.exe\" -Force -ErrorAction SilentlyContinue; " +
            "} finally { Remove-PSDrive -Name $drive -Force -ErrorAction SilentlyContinue }";
        PowerShellRunner.TryRun(script, TimeoutMs, out _);
    }

    private sealed class RemoteNodeSession
    {
        public RemoteNodeSession(RdcTarget target)
        {
            Target = target;
            NodeName = target.Host;
        }
        public RdcTarget Target { get; }
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
        public bool VersionWarningLogged { get; set; }
        public int PollFailures { get; set; }
        public string? PollHost { get; set; }
        public string? PollEndpoint { get; set; }
        public string LastState { get; set; } = string.Empty;
        public string LastError { get; set; } = string.Empty;
        public void Cancel()
        {
            try { Cts.Cancel(); } catch { }
        }
    }
}

