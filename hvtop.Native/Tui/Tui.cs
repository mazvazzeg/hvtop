namespace hvtop.Native;

internal sealed class Tui
{
    private readonly AppState state;
    private readonly Options options;
    private const string ColGap = "    ";
    private const string KeyboardTipsText = "Arrows move  PgUp/PgDn/Home/End page nav  Enter drill  s sort  S dir  w new pane  W close  Tab cycle  f refresh  r rescan  q quit";
    private const int MaxNameWidth = 32;
    private const int MinNameWidth = 20;
    private const int CapacityMetricWidth = 25;
    private const int MetricWidth = 21;
    private const int ShortMetricWidth = 13;
    private const int HostVersionWidth = 28;
    private const int DashboardNameWidth = 20;
    private const int HostDashboardVersionWidth = 7;
    private const int DashboardCapacityMetricWidth = 22;
    private const int DashboardCapacityConfigWidth = 8;
    private const int VmVersionWidth = 7;
    private const int HostColumnWidth = 14;
    private const int UptimeWidth = 4;
    private const int PdidWidth = 4;
    private const int TypeWidth = 9;
    private const int CountWidth = 5;
    private const int OwnerWidth = 18;
    private const int QuorumWidth = 14;
    private const int SizeWidth = 10;
    private const int UplinkWidth = 3;
    private const int LinkWidth = 6;
    private const int StatusWidth = 6;
    private Panel panel = Panel.Hosts;
    private int selected;
    private int tableScrollOffset;
    private DrillView drillView = DrillView.Overview;
    private Panel detailPanel;
    private string? selectedHostName;
    private string? selectedItemName;
    private Stack<ViewState> backStack = new();
    private Dictionary<string, SortState> sortStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PaneState> panes = [];
    private int activePaneIndex;
    private string[] previousLines = [];
    private ConsoleColor[] previousForegrounds = [];
    private ConsoleColor[] previousBackgrounds = [];
    private bool[] touchedLines = [];
    private int previousWidth;
    private int previousHeight;
    private int frameWidth;
    private int frameHeight;
    private bool mapContentLines;
    private int mappedContentTop;
    private int mappedContentHeight;
    private int activePaneContentHeight;

    public Tui(AppState state, Options options)
    {
        this.state = state;
        this.options = options;
        panes.Add(CapturePaneState());
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

    public void RenderOnce()
    {
        Render();
        Console.ResetColor();
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
            case ConsoleKey.P:
                SetPanel(Panel.PhysicalDisks);
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
            case ConsoleKey.Tab:
                SwitchPane((key.Modifiers & ConsoleModifiers.Shift) != 0);
                return;
            case ConsoleKey.W:
                if ((key.Modifiers & ConsoleModifiers.Shift) != 0)
                    ClosePane();
                else
                    OpenPane();
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

    }

    private int PageSize()
    {
        var height = activePaneContentHeight > 0 ? activePaneContentHeight : Math.Max(0, Console.WindowHeight - 6);
        return Math.Max(1, height - 2);
    }

    private void OpenPane()
    {
        EnsurePanes();
        if (panes.Count >= 4)
        {
            state.AddEvent("WARN", "Maximum pane count reached");
            return;
        }

        SaveActivePane();
        panes.Add(CapturePaneState());
        activePaneIndex = panes.Count - 1;
        LoadPane(activePaneIndex);
    }

    private void ClosePane()
    {
        EnsurePanes();
        if (panes.Count <= 1)
            return;

        SaveActivePane();
        panes.RemoveAt(activePaneIndex);
        activePaneIndex = Math.Clamp(activePaneIndex, 0, panes.Count - 1);
        LoadPane(activePaneIndex);
    }

    private void SwitchPane(bool reverse)
    {
        EnsurePanes();
        if (panes.Count <= 1)
            return;

        SaveActivePane();
        activePaneIndex = reverse
            ? (activePaneIndex + panes.Count - 1) % panes.Count
            : (activePaneIndex + 1) % panes.Count;
        LoadPane(activePaneIndex);
    }

    private void SetPanel(Panel next)
    {
        panel = next;
        selected = 0;
        tableScrollOffset = 0;
        drillView = DrillView.Overview;
        selectedHostName = null;
        selectedItemName = null;
        backStack.Clear();
    }

    private void CycleSort(bool reverse)
    {
        if (panel == Panel.Events)
        {
            state.AddEvent("INFO", "Events view is fixed to date desc");
            return;
        }

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
            PhysicalDiskRow physicalDiskRow => physicalDiskRow.HostName,
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
                case PhysicalDiskRow physicalDiskRow:
                    panel = Panel.PhysicalDisks;
                    detailPanel = Panel.PhysicalDisks;
                    drillView = DrillView.Detail;
                    selectedHostName = physicalDiskRow.HostName;
                    selectedItemName = physicalDiskRow.Name;
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
        backStack.Push(new ViewState(panel, selected, tableScrollOffset, drillView, detailPanel, selectedHostName, selectedItemName));
    }

    private bool PopView()
    {
        if (backStack.Count == 0) return false;
        var prior = backStack.Pop();
        panel = prior.Panel;
        selected = prior.Selected;
        tableScrollOffset = prior.TableScrollOffset;
        drillView = prior.DrillView;
        detailPanel = prior.DetailPanel;
        selectedHostName = prior.SelectedHostName;
        selectedItemName = prior.SelectedItemName;
        return true;
    }

    private void EnsurePanes()
    {
        if (panes.Count == 0)
            panes.Add(CapturePaneState());

        activePaneIndex = Math.Clamp(activePaneIndex, 0, panes.Count - 1);
    }

    private void SaveActivePane()
    {
        EnsurePanes();
        panes[activePaneIndex] = CapturePaneState();
    }

    private PaneState CapturePaneState()
        => new(
            panel,
            selected,
            tableScrollOffset,
            drillView,
            detailPanel,
            selectedHostName,
            selectedItemName,
            CloneStack(backStack),
            new Dictionary<string, SortState>(sortStates, StringComparer.OrdinalIgnoreCase));

    private void LoadPane(int index)
    {
        EnsurePanes();
        var pane = panes[Math.Clamp(index, 0, panes.Count - 1)];
        panel = pane.Panel;
        selected = pane.Selected;
        tableScrollOffset = pane.TableScrollOffset;
        drillView = pane.DrillView;
        detailPanel = pane.DetailPanel;
        selectedHostName = pane.SelectedHostName;
        selectedItemName = pane.SelectedItemName;
        backStack = CloneStack(pane.BackStack);
        sortStates = new Dictionary<string, SortState>(pane.SortStates, StringComparer.OrdinalIgnoreCase);
    }

    private static Stack<ViewState> CloneStack(Stack<ViewState> source)
        => new(source.Reverse());

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
                Panel.PhysicalDisks => s.PhysicalDisks,
                Panel.Network => s.NetworkSwitches,
                Panel.Events => s.Events,
                _ => []
            };
        }

        return ApplySort(rows).ToArray();
    }

    private IEnumerable<object> ApplySort(IReadOnlyList<object> rows)
    {
        if (panel == Panel.Events)
            return rows
                .Cast<EventRow>()
                .OrderByDescending(evt => evt.At)
                .Cast<object>();

        var columns = SortColumns().ToArray();
        if (columns.Length == 0 || rows.Count <= 1)
            return rows;

        var state = SortStateFor(SortViewKey(), columns);
        var sortable = rows.Select(row => new { Row = row, Value = SortValue(row, state.Column) }).ToArray();
        var valid = sortable.Where(item => !IsMissingSortValue(item.Value)).ToArray();
        var missing = sortable.Where(item => IsMissingSortValue(item.Value)).Select(item => item.Row).OrderBy(GetRowName, StringComparer.OrdinalIgnoreCase);
        return state.Descending
            ? valid.OrderByDescending(item => item.Value, SortValueComparer.Instance)
                .ThenBy(item => SecondarySortValue(item.Row), SortValueComparer.Instance)
                .Select(item => item.Row)
                .Concat(missing)
            : valid.OrderBy(item => item.Value, SortValueComparer.Instance)
                .ThenBy(item => SecondarySortValue(item.Row), SortValueComparer.Instance)
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
            Panel.PhysicalDisks => [new("HOST", "host"), new("PDID", "pdid"), new("TYPE", "type"), new("SIZE", "size"), new("IO", "i/o"), new("IOPS", "iops"), new("QD", "queue"), new("LAT", "latency"), new("STA", "status")],
            Panel.Network => [new("HOST", "host"), new("NAME", "name"), new("UPL", "uplinks"), new("LINK", "link"), new("THR", "throughput"), new("RX", "rx"), new("TX", "tx"), new("STA", "status")],
            Panel.Events => [new("DATE", "date")],
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
        PhysicalDiskRow disk => column switch
        {
            "HOST" => disk.HostName,
            "PDID" => ParsePhysicalDiskId(disk.PhysicalDiskId),
            "NAME" => disk.Name,
            "TYPE" => disk.Type,
            "SIZE" => ParseCapacity(disk.Size),
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

    private static object? SecondarySortValue(object row)
        => row is PhysicalDiskRow disk ? ParsePhysicalDiskId(disk.PhysicalDiskId) : GetRowName(row);

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

    private static double ParsePhysicalDiskId(string value)
        => double.TryParse(value, out var number) ? number : double.NaN;

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
        EnsurePanes();
        SaveActivePane();
        LoadPane(activePaneIndex);
        var sort = CurrentSortLabel();
        WriteLine(0, $"{Program.AppName} {Program.DisplayVersion}  Sample: {s.At:HH:mm:ss}  Refresh: {state.Refresh.TotalSeconds:N1}s  History: {options.History.TotalMinutes:N0}m  Sort: {sort}", ConsoleColor.White);
        WriteLine(1, Nav(), ConsoleColor.Yellow);
        WriteLine(2, KeyboardTipsText, ConsoleColor.DarkGray);

        var fatalError = state.ReadFatalError();
        if (IsLoading(s) && string.IsNullOrWhiteSpace(fatalError))
        {
            RenderLoadingOverlay(s);
            RenderDiscoveryStatus(s);
            EndFrame();
            Console.ResetColor();
            return;
        }

        RenderPanes();
        LoadPane(activePaneIndex);
        RenderDiscoveryStatus(s);
        EndFrame();
        Console.ResetColor();
    }

    private void RenderPanes()
    {
        EnsurePanes();
        if (frameHeight <= 5 || frameWidth <= 1)
            return;

        const int top = 3;
        const int bottomReserved = 1;
        var height = Math.Max(0, frameHeight - top - bottomReserved);
        if (height <= 0)
            return;

        RenderPaneBorder(top, height);
        var contentHeight = Math.Max(0, height - 2);
        activePaneContentHeight = contentHeight;
        if (contentHeight <= 0)
            return;

        LoadPane(activePaneIndex);
        mapContentLines = true;
        mappedContentTop = top + 1;
        mappedContentHeight = contentHeight;
        try
        {
            if (drillView == DrillView.Detail) RenderDetail();
            else RenderTable();
        }
        finally
        {
            mapContentLines = false;
            panes[activePaneIndex] = CapturePaneState();
        }

        FillPaneInterior(top, height);
    }

    private void RenderPaneBorder(int top, int height)
    {
        var topText = PaneBorderText(topBorder: true);
        WriteLine(top, topText, ConsoleColor.Cyan);
        if (height > 1)
            WriteLine(top + height - 1, PaneBorderText(topBorder: false), ConsoleColor.Cyan);
    }

    private string PaneBorderText(bool topBorder)
    {
        var width = Math.Max(0, frameWidth - 1);
        if (width <= 0)
            return string.Empty;

        if (!topBorder)
            return width == 1
                ? "\u2514"
                : "\u2514" + new string('\u2500', Math.Max(0, width - 2)) + "\u2518";

        var labels = Enumerable.Range(0, panes.Count)
            .Select(i => i == activePaneIndex ? $"[{i + 1}]" : $"{i + 1}");
        var label = $" {string.Join(" ", labels)} ";
        var text = "\u250c\u2500" + label;
        if (text.Length >= width)
            return width == 1 ? "\u250c" : text[..(width - 1)] + "\u2510";

        return text + new string('\u2500', width - text.Length - 1) + "\u2510";
    }

    private void FillPaneInterior(int top, int height)
    {
        if (height <= 2)
            return;

        var bottom = Math.Min(frameHeight - 1, top + height - 1);
        for (var y = top + 1; y < bottom; y++)
        {
            if (!touchedLines[y])
                WritePaneContentLine(y, string.Empty, ConsoleColor.Gray, ConsoleColor.Black);
        }
    }

    private string Nav()
    {
        return string.Join("  ", new[]
        {
            panel == Panel.Cluster ? "[C] CLUSTER" : " C  CLUSTER",
            panel == Panel.Hosts ? "[H] HOSTS" : " H  HOSTS",
            panel == Panel.Vms ? "[V] VMS" : " V  VMS",
            panel == Panel.Disks ? "[D] CSV / STORAGE" : " D  CSV / STORAGE",
            panel == Panel.PhysicalDisks ? "[P] PHYSICAL" : " P  PHYSICAL",
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

        if (!string.IsNullOrWhiteSpace(snapshot.RdcStatus))
            text += $"  RDC: {snapshot.RdcStatus}";

        var fatalError = state.ReadFatalError();
        if (!string.IsNullOrWhiteSpace(fatalError))
            text += $"  ERROR: {DisplayName(fatalError, Math.Max(20, frameWidth / 3))}";

        var activeRdcStatus = snapshot.RdcStatus.Equals("stopping", StringComparison.OrdinalIgnoreCase)
                              || snapshot.RdcStatus.Equals("checking", StringComparison.OrdinalIgnoreCase)
                              || snapshot.RdcStatus.StartsWith("deploy", StringComparison.OrdinalIgnoreCase)
                              || snapshot.RdcStatus.StartsWith("poll", StringComparison.OrdinalIgnoreCase);
        WriteLine(frameHeight - 1, text.TrimEnd(), !string.IsNullOrWhiteSpace(fatalError) ? ConsoleColor.Red : activeRdcStatus || !discovery.Complete ? ConsoleColor.Yellow : ConsoleColor.DarkGray);
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
                    var nameWidth = DashboardNameWidth;
                    RenderRows(
                        Row(Header(DisplayName($"HOST {selectedHostName} -> VMS", nameWidth), nameWidth), Header("VER", VmVersionWidth), HeaderRight("UP", UptimeWidth), DashboardCapacityMetricGroupHeader("CPU"), DashboardCapacityMetricGroupHeader("MEM"), GroupHeader("I/O", MetricWidth), GroupHeader("NET", MetricWidth), Header("STA", StatusWidth)),
                        Row(Header(string.Empty, nameWidth), Header(string.Empty, VmVersionWidth), Header(string.Empty, UptimeWidth), DashboardCapacityMetricSubHeader(), DashboardCapacityMetricSubHeader(), MetricSubHeader(), MetricSubHeader(), Header(string.Empty, StatusWidth)),
                        CurrentRows().Cast<VmRow>().ToArray(),
                        r => Row(Cell(DisplayName(r.Name, nameWidth), nameWidth), Cell(r.Version, VmVersionWidth), Cell(r.IsRunning ? UptimeFormatter.FormatShort(r.Uptime) : "OFF", UptimeWidth, true), FmtDashboardWithCapacity(r.Cpu, r.CpuCapacity), FmtDashboardWithCapacity(r.Mem, r.MemCapacity), Fmt(r.Io), Fmt(r.Net), Cell(r.Status, StatusWidth)));
                }
                else
                {
                    var nameWidth = DashboardNameWidth;
                    RenderRows(
                        Row(Header("HOSTNAME", nameWidth), Header("VER", HostDashboardVersionWidth), HeaderRight("UP", UptimeWidth), DashboardCapacityMetricGroupHeader("CPU"), DashboardCapacityMetricGroupHeader("MEM"), GroupHeader("I/O", MetricWidth), GroupHeader("NET", MetricWidth), Header("STA", StatusWidth)),
                        Row(Header(string.Empty, nameWidth), Header(string.Empty, HostDashboardVersionWidth), Header(string.Empty, UptimeWidth), DashboardCapacityMetricSubHeader(), DashboardCapacityMetricSubHeader(), MetricSubHeader(), MetricSubHeader(), Header(string.Empty, StatusWidth)),
                        CurrentRows().Cast<HostRow>().ToArray(),
                        r => Row(Cell(DisplayName(r.Name, nameWidth), nameWidth), Cell(ShortHostVersion(r.Version), HostDashboardVersionWidth), Cell(UptimeFormatter.FormatShort(r.Uptime), UptimeWidth, true), FmtDashboardWithCapacity(r.Cpu, r.CpuCapacity), FmtDashboardWithCapacity(r.Mem, r.MemCapacity), Fmt(r.Io), Fmt(r.Net), Cell(r.Status, StatusWidth)));
                }
                break;
            case Panel.Vms:
                {
                    var nameWidth = DashboardNameWidth;
                    RenderRows(
                        Row(Header("HOST", HostColumnWidth), Header("NAME", nameWidth), Header("VER", VmVersionWidth), HeaderRight("UP", UptimeWidth), DashboardCapacityMetricGroupHeader("CPU"), DashboardCapacityMetricGroupHeader("MEM"), GroupHeader("I/O", MetricWidth), GroupHeader("NET", MetricWidth), Header("STA", StatusWidth)),
                        Row(Header(string.Empty, HostColumnWidth), Header(string.Empty, nameWidth), Header(string.Empty, VmVersionWidth), Header(string.Empty, UptimeWidth), DashboardCapacityMetricSubHeader(), DashboardCapacityMetricSubHeader(), MetricSubHeader(), MetricSubHeader(), Header(string.Empty, StatusWidth)),
                        CurrentRows().Cast<VmRow>().ToArray(),
                        r => Row(Cell(DisplayName(r.HostName, HostColumnWidth), HostColumnWidth), Cell(DisplayName(r.Name, nameWidth), nameWidth), Cell(r.Version, VmVersionWidth), Cell(r.IsRunning ? UptimeFormatter.FormatShort(r.Uptime) : "OFF", UptimeWidth, true), FmtDashboardWithCapacity(r.Cpu, r.CpuCapacity), FmtDashboardWithCapacity(r.Mem, r.MemCapacity), Fmt(r.Io), Fmt(r.Net), Cell(r.Status, StatusWidth)));
                }
                break;
            case Panel.Disks:
                {
                    var nameWidth = DashboardNameWidth;
                    RenderRows(
                        Row(Header("HOST", HostColumnWidth), Header("NAME", nameWidth), HeaderRight("SIZE", SizeWidth), GroupHeader("FREE", ShortMetricWidth), GroupHeader("I/O", MetricWidth), GroupHeader("IOPS", ShortMetricWidth), GroupHeader("QD", ShortMetricWidth), GroupHeader("LAT", ShortMetricWidth), Header("STA", StatusWidth)),
                        Row(Header(string.Empty, HostColumnWidth), Header(string.Empty, nameWidth), Header(string.Empty, SizeWidth), FreeShortMetricSubHeader(), MetricSubHeader(), ShortMetricSubHeader(), ShortMetricSubHeader(), ShortMetricSubHeader(), Header(string.Empty, StatusWidth)),
                        CurrentRows().Cast<DiskRow>().ToArray(),
                        r => Row(Cell(DisplayName(r.HostName, HostColumnWidth), HostColumnWidth), Cell(DisplayName(r.Name, nameWidth), nameWidth), Cell(r.Size, SizeWidth, true), FmtShort(r.Free), Fmt(r.Io), FmtShort(r.Iops), FmtShort(r.QueueDepth), FmtShort(r.Latency), Cell(r.Status, StatusWidth)));
                }
                break;
            case Panel.PhysicalDisks:
                {
                    RenderRows(
                        Row(Header("HOST", HostColumnWidth), Header("PDID", PdidWidth), Header("TYPE", TypeWidth), HeaderRight("SIZE", SizeWidth), GroupHeader("I/O", MetricWidth), GroupHeader("IOPS", ShortMetricWidth), GroupHeader("QD", ShortMetricWidth), GroupHeader("LAT", ShortMetricWidth), Header("STA", StatusWidth)),
                        Row(Header(string.Empty, HostColumnWidth), Header(string.Empty, PdidWidth), Header(string.Empty, TypeWidth), Header(string.Empty, SizeWidth), MetricSubHeader(), ShortMetricSubHeader(), ShortMetricSubHeader(), ShortMetricSubHeader(), Header(string.Empty, StatusWidth)),
                        CurrentRows().Cast<PhysicalDiskRow>().ToArray(),
                        r => Row(Cell(DisplayName(r.HostName, HostColumnWidth), HostColumnWidth), Cell(r.PhysicalDiskId, PdidWidth, true), Cell(DisplayName(r.Type, TypeWidth), TypeWidth), Cell(PhysicalDiskSizeSummary(r.Size), SizeWidth, true), Fmt(r.Io), FmtShort(r.Iops), FmtShort(r.QueueDepth), FmtShort(r.Latency), Cell(r.Status, StatusWidth)));
                }
                break;
            case Panel.Network:
                {
                    if (drillView == DrillView.NetworkAdapters)
                    {
                        var nameWidth = DashboardNameWidth;
                        var switchName = CurrentNetworkSwitchDisplayName();
                        RenderRows(
                            Row(Header(DisplayName($"HOST {selectedHostName} -> VSWITCH {switchName} -> PNICS", nameWidth), nameWidth), Header("LINK", LinkWidth), GroupHeader("THR", MetricWidth), GroupHeader("RX", MetricWidth), GroupHeader("TX", MetricWidth), GroupHeader("DROPS", ShortMetricWidth), Header("STA", StatusWidth)),
                            Row(Header(string.Empty, nameWidth), Header(string.Empty, LinkWidth), MetricSubHeader(), MetricSubHeader(), MetricSubHeader(), ShortMetricSubHeader(), Header(string.Empty, StatusWidth)),
                            CurrentRows().Cast<NetworkRow>().ToArray(),
                            r => Row(Cell(DisplayName(r.Name, nameWidth), nameWidth), Cell(r.Link, LinkWidth), Fmt(r.Throughput), Fmt(r.Rx), Fmt(r.Tx), FmtShort(r.Drops), Cell(r.Status, StatusWidth)));
                    }
                    else
                    {
                        var nameWidth = DashboardNameWidth;
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

    private static string DashboardCapacityMetricGroupHeader(string label)
        => GroupHeader(label, DashboardCapacityMetricWidth);

    private static string MetricSubHeader() => FixedMetricHeaderCell("cur", "max", valueWidth: 9, width: MetricWidth);

    private static string FreeMetricSubHeader() => FixedMetricHeaderCell("cur", "min", valueWidth: 9, width: MetricWidth);

    private static string FreeShortMetricSubHeader() => FixedMetricHeaderCell("cur", "min", valueWidth: 5, width: ShortMetricWidth);

    private static string ShortMetricSubHeader() => FixedMetricHeaderCell("cur", "max", valueWidth: 5, width: ShortMetricWidth);

    private static string CapacityMetricSubHeader() => FixedMetricHeaderCell("cur", "max", "cfg", currentWidth: 4, maxWidth: 4, configWidth: 11, width: CapacityMetricWidth);

    private static string DashboardCapacityMetricSubHeader()
        => Cell($"{Cell("cur", 4, true)} | {Cell("max", 4)} | {Cell("cfg", DashboardCapacityConfigWidth)}", DashboardCapacityMetricWidth);

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

    private static string PhysicalDiskSizeSummary(string size)
    {
        var paren = size.IndexOf('(');
        return paren > 0 ? size[..paren].TrimEnd() : size;
    }

    private static string ShortHostVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return string.Empty;

        var paren = version.IndexOf('(');
        var text = paren > 0 ? version[..paren] : version;
        return text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
    }

    private static string VmDiskHeaderRow()
        => "  " + string.Join("  ", new[]
        {
            Cell("vDisks", 32),
            Cell("Storage/CSV", 26),
            Cell("Read", 10),
            Cell("Read IOPS", 10),
            Cell("Write", 10),
            Cell("Write IOPS", 10),
            Cell("QD", 6),
            Cell("LAT", 10)
        });

    private static string VmDiskDataRow(VDiskRow disk, Snapshot snapshot, string hostName, bool vmIsRunning)
    {
        var storage = FindStorageRow(snapshot, hostName, disk.StorageName);
        return "  " + string.Join("  ", new[]
        {
            Cell(DisplayName(disk.Name, 32), 32),
            Cell(DisplayName(disk.StorageName, 26), 26),
            Cell(vmIsRunning ? FmtValue(disk.ReadMbps, Unit.Mbps) : "n/a", 10),
            Cell(vmIsRunning ? FmtValue(disk.ReadIops, Unit.Iops) : "n/a", 10),
            Cell(vmIsRunning ? FmtValue(disk.WriteMbps, Unit.Mbps) : "n/a", 10),
            Cell(vmIsRunning ? FmtValue(disk.WriteIops, Unit.Iops) : "n/a", 10),
            Cell(vmIsRunning && storage is not null ? FmtValue(storage.QueueDepth.Current, storage.QueueDepth.Unit) : "n/a", 6),
            Cell(vmIsRunning && storage is not null ? FmtValue(storage.Latency.Current, storage.Latency.Unit) : "n/a", 10)
        });
    }

    private static DiskRow? FindStorageRow(Snapshot snapshot, string hostName, string storageName)
        => snapshot.Disks.FirstOrDefault(d => d.HostName.Equals(hostName, StringComparison.OrdinalIgnoreCase)
            && d.Name.Equals(storageName, StringComparison.OrdinalIgnoreCase));

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
            Cell("UP", 4, true),
            Cell("CPU", 10, true),
            Cell("MEM", 10, true),
            Cell("I/O", 10, true),
            Cell("STA", 6)
        });

    private static string HostVmDataRow(VmRow vm)
        => "  " + string.Join("  ", new[]
        {
            Cell(DisplayName(vm.Name, 32), 32),
            Cell(vm.IsRunning ? UptimeFormatter.FormatShort(vm.Uptime) : "OFF", 4, true),
            Cell(FmtValue(vm.Cpu.Current, vm.Cpu.Unit), 10, true),
            Cell(FmtValue(vm.Mem.Current, vm.Mem.Unit), 10, true),
            Cell(FmtValue(vm.Io.Current, vm.Io.Unit), 10, true),
            Cell(vm.Status, 6)
        });

    private static string HostDiskHeaderRow()
        => "  " + string.Join("  ", new[]
        {
            Cell("Storage", 32),
            Cell("FREE", 10, true),
            Cell("I/O", 10, true),
            Cell("IOPS", 10, true),
            Cell("LAT", 10, true),
            Cell("STA", 6)
        });

    private static string HostDiskDataRow(DiskRow disk)
        => "  " + string.Join("  ", new[]
        {
            Cell(DisplayName(disk.Name, 32), 32),
            Cell(FmtValue(disk.Free.Current, disk.Free.Unit), 10, true),
            Cell(FmtValue(disk.Io.Current, disk.Io.Unit), 10, true),
            Cell(FmtValue(disk.Iops.Current, disk.Iops.Unit), 10, true),
            Cell(FmtValue(disk.Latency.Current, disk.Latency.Unit), 10, true),
            Cell(disk.Status, 6)
        });

    private static string HostPhysicalDiskHeaderRow()
        => "  " + string.Join("  ", new[]
        {
            Cell("PDID", PdidWidth),
            Cell("TYPE", TypeWidth),
            Cell("SIZE", SizeWidth),
            Cell("Instance", 24),
            Cell("I/O", 10),
            Cell("IOPS", 10),
            Cell("QD", 6),
            Cell("LAT", 10),
            Cell("STA", 6)
        });

    private static string HostPhysicalDiskDataRow(PhysicalDiskRow disk)
        => "  " + string.Join("  ", new[]
        {
            Cell(disk.PhysicalDiskId, PdidWidth, true),
            Cell(DisplayName(disk.Type, TypeWidth), TypeWidth),
            Cell(PhysicalDiskSizeSummary(disk.Size), SizeWidth, true),
            Cell(DisplayName(PhysicalDiskInstanceDisplay(disk), 24), 24),
            Cell(FmtValue(disk.Io.Current, disk.Io.Unit), 10),
            Cell(FmtValue(disk.Iops.Current, disk.Iops.Unit), 10),
            Cell(FmtValue(disk.QueueDepth.Current, disk.QueueDepth.Unit), 6),
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
        var maxRows = Math.Max(0, ContentWindowHeight - 2);
        KeepSelectionVisible(rows.Count, maxRows);
        var visibleRows = rows.Skip(tableScrollOffset).Take(maxRows).ToArray();
        for (var i = 0; i < visibleRows.Length; i++)
        {
            var absoluteIndex = tableScrollOffset + i;
            var background = absoluteIndex == selected ? ConsoleColor.DarkCyan : ConsoleColor.Black;
            var row = visibleRows[i];
            var foreground = absoluteIndex == selected ? ConsoleColor.White : (row is null ? ConsoleColor.Gray : RowColor(row));
            WriteLine(6 + i, formatter(row), foreground, background);
        }
    }

    private void RenderRows<T>(string header, IReadOnlyList<T> rows, Func<T, string> formatter)
    {
        WriteLine(4, header, ConsoleColor.Cyan);
        selected = Math.Min(selected, Math.Max(0, rows.Count - 1));
        var maxRows = Math.Max(0, ContentWindowHeight - 1);
        KeepSelectionVisible(rows.Count, maxRows);
        var visibleRows = rows.Skip(tableScrollOffset).Take(maxRows).ToArray();
        for (var i = 0; i < visibleRows.Length; i++)
        {
            var absoluteIndex = tableScrollOffset + i;
            var background = absoluteIndex == selected ? ConsoleColor.DarkCyan : ConsoleColor.Black;
            var row = visibleRows[i];
            var foreground = absoluteIndex == selected ? ConsoleColor.White : (row is null ? ConsoleColor.Gray : RowColor(row));
            WriteLine(5 + i, formatter(row), foreground, background);
        }
    }

    private void KeepSelectionVisible(int rowCount, int maxRows)
    {
        if (rowCount <= 0 || maxRows <= 0)
        {
            tableScrollOffset = 0;
            return;
        }

        tableScrollOffset = Math.Clamp(tableScrollOffset, 0, Math.Max(0, rowCount - maxRows));
        if (selected < tableScrollOffset)
            tableScrollOffset = selected;
        else if (selected >= tableScrollOffset + maxRows)
            tableScrollOffset = selected - maxRows + 1;
    }

    private int ContentWindowHeight
        => mapContentLines
            ? Math.Max(0, mappedContentHeight)
            : Math.Max(0, frameHeight - 5);

    private void RenderDetail()
    {
        var snapshot = state.Read();
        var detailTarget = ResolveDetailTarget(snapshot);
        if (detailTarget is null)
        {
            GoBack();
            return;
        }

        WriteLine(4, DetailTitle(detailTarget), ConsoleColor.Cyan);
        WriteLine(5, string.Empty);

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
                var vmDisks = GetVmDisks(vm, snapshot);
                var vmAdapters = GetVmNetworkAdapters(vm, snapshot);
                var vmCheckpoints = GetVmCheckpoints(vm, snapshot);
                var vmReadIo = vm.IsRunning ? DetailAggregateMetric(vmDisks, d => d.ReadMbps, d => d.ReadMbpsMax, Unit.Mbps) : Metric.Mbps(double.NaN);
                var vmWriteIo = vm.IsRunning ? DetailAggregateMetric(vmDisks, d => d.WriteMbps, d => d.WriteMbpsMax, Unit.Mbps) : Metric.Mbps(double.NaN);
                var vmReadIops = vm.IsRunning ? DetailAggregateMetric(vmDisks, d => d.ReadIops, d => d.ReadIopsMax, Unit.Iops) : Metric.Iops(double.NaN);
                var vmWriteIops = vm.IsRunning ? DetailAggregateMetric(vmDisks, d => d.WriteIops, d => d.WriteIopsMax, Unit.Iops) : Metric.Iops(double.NaN);
                var vmTotalNet = vmAdapters.Length == 0
                    ? vm.Net
                    : vm.IsRunning ? DetailAggregateNetworkMetric(vmAdapters, d => d.ThroughputMbps, d => d.ThroughputMbpsMax) : Metric.Mbps(double.NaN);
                var vmRxNet = vm.IsRunning ? DetailAggregateNetworkMetric(vmAdapters, d => d.RxMbps, d => d.RxMbpsMax) : Metric.Mbps(double.NaN);
                var vmTxNet = vm.IsRunning ? DetailAggregateNetworkMetric(vmAdapters, d => d.TxMbps, d => d.TxMbpsMax) : Metric.Mbps(double.NaN);
                selected = Math.Min(selected, Math.Max(0, vmDisks.Length + vmAdapters.Length - 1));
                Detail(7, "Name", vm.Name);
                Detail(8, "Uptime", vm.IsRunning ? UptimeFormatter.FormatExact(vm.Uptime) : "Powered off");
                Detail(9, "Status", vm.Status, StatusColor(vm.Status));
                DetailScalar(10, string.Empty, string.Empty);
                DetailCapacityMetricHeader(11, string.Empty);
                DetailMetricWithCapacity(12, "CPU", vm.Cpu, vm.CpuCapacity);
                DetailMetricWithCapacity(13, "Memory", vm.Mem, vm.MemCapacity);
                DetailScalar(14, string.Empty, string.Empty);
                DetailMetricHeader(15, string.Empty, "cur", "max");
                DetailMetric(16, "Total I/O", vm.Io);
                DetailMetric(17, Branch(false, "Read I/O"), vmReadIo);
                DetailMetric(18, Branch(true, "Write I/O"), vmWriteIo);
                DetailScalar(19, string.Empty, string.Empty);
                DetailMetric(20, "Total IOPS", vm.Iops);
                DetailMetric(21, Branch(false, "Read IOPS"), vmReadIops);
                DetailMetric(22, Branch(true, "Write IOPS"), vmWriteIops);
                DetailScalar(23, string.Empty, string.Empty);
                DetailMetricHeader(24, string.Empty, "cur", "max");
                DetailMetric(25, "Total Network", vmTotalNet);
                DetailMetric(26, Branch(false, "Total Receive"), vmRxNet);
                DetailMetric(27, Branch(true, "Total Transmit"), vmTxNet);
                DetailScalar(28, string.Empty, string.Empty);
                Detail(29, "Replication status", vm.Replication, ReplicationColor(vm.ReplicationStatus));
                var checkpointBottom = RenderVmCheckpoints(30, vmCheckpoints);
                var disksTop = checkpointBottom + 3;
                var vmDetailLines = new List<DetailLine>
                {
                    DetailLine.Header(VmDiskHeaderRow())
                };
                for (var i = 0; i < vmDisks.Length; i++)
                {
                    var disk = vmDisks[i];
                    vmDetailLines.Add(DetailLine.Selectable(VmDiskDataRow(disk, snapshot, vm.HostName, vm.IsRunning), disk, i));
                }
                vmDetailLines.Add(DetailLine.Blank());
                vmDetailLines.Add(DetailLine.Header(VmNetworkHeaderRow()));
                for (var i = 0; i < vmAdapters.Length; i++)
                {
                    var adapter = vmAdapters[i];
                    var absoluteIndex = vmDisks.Length + i;
                    vmDetailLines.Add(DetailLine.Selectable(VmNetworkDataRow(adapter), adapter, absoluteIndex));
                }
                RenderDetailLines(disksTop, vmDetailLines);
                break;
            case HostRow host:
                var hostVms = snapshot.Vms.Where(v => v.HostName.Equals(host.Name, StringComparison.OrdinalIgnoreCase)).OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                var hostDisks = snapshot.Disks.Where(d => d.HostName.Equals(host.Name, StringComparison.OrdinalIgnoreCase)).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                var hostPhysicalDisks = snapshot.PhysicalDisks.Where(d => d.HostName.Equals(host.Name, StringComparison.OrdinalIgnoreCase)).OrderBy(d => ParsePhysicalDiskId(d.PhysicalDiskId), SortValueComparer.Instance).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                var hostNetworks = snapshot.NetworkSwitches.Where(n => n.HostName.Equals(host.Name, StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                var selectableRows = hostVms.Length + hostDisks.Length + hostPhysicalDisks.Length + hostNetworks.Length;
                selected = Math.Min(selected, Math.Max(0, selectableRows - 1));
                Detail(7, "Name", host.Name);
                Detail(8, "Version", host.Version);
                Detail(9, "Uptime", UptimeFormatter.FormatExact(host.Uptime));
                DetailScalar(10, string.Empty, string.Empty);
                DetailCapacityMetricHeader(11, string.Empty);
                DetailMetricWithCapacity(12, "CPU", host.Cpu, host.CpuCapacity);
                DetailMetricWithCapacity(13, "Memory", host.Mem, host.MemCapacity);
                DetailScalar(14, string.Empty, string.Empty);
                DetailMetricHeader(15, string.Empty, "cur", "max");
                DetailMetric(16, "I/O", host.Io);
                DetailMetric(17, "Network", host.Net);
                DetailScalar(18, string.Empty, string.Empty);
                Detail(19, "Status", host.Status, StatusColor(host.Status));
                var absolute = 0;
                var hostDetailLines = new List<DetailLine>
                {
                    DetailLine.Header(HostVmHeaderRow())
                };
                foreach (var vmRow in hostVms)
                    hostDetailLines.Add(DetailLine.Selectable(HostVmDataRow(vmRow), vmRow, absolute++));

                hostDetailLines.Add(DetailLine.Blank());
                hostDetailLines.Add(DetailLine.Header(HostDiskHeaderRow()));
                foreach (var diskRow in hostDisks)
                    hostDetailLines.Add(DetailLine.Selectable(HostDiskDataRow(diskRow), diskRow, absolute++));

                hostDetailLines.Add(DetailLine.Blank());
                hostDetailLines.Add(DetailLine.Header(HostPhysicalDiskHeaderRow()));
                foreach (var diskRow in hostPhysicalDisks)
                    hostDetailLines.Add(DetailLine.Selectable(HostPhysicalDiskDataRow(diskRow), diskRow, absolute++));

                hostDetailLines.Add(DetailLine.Blank());
                hostDetailLines.Add(DetailLine.Header(HostNetworkHeaderRow()));
                foreach (var networkRow in hostNetworks)
                    hostDetailLines.Add(DetailLine.Selectable(HostNetworkDataRow(networkRow), networkRow, absolute++));
                RenderDetailLines(22, hostDetailLines);
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
                DetailMetric(15, Branch(false, "Read I/O"), Metric.Mbps(virtualDisk.ReadMbps) with { Max = DetailMax(virtualDisk.ReadMbpsMax, virtualDisk.ReadMbps) });
                DetailMetric(16, Branch(true, "Write I/O"), Metric.Mbps(virtualDisk.WriteMbps) with { Max = DetailMax(virtualDisk.WriteMbpsMax, virtualDisk.WriteMbps) });
                DetailScalar(17, string.Empty, string.Empty);
                DetailMetricHeader(18, string.Empty, "cur", "max");
                DetailMetric(19, "Total IOPS", Metric.Iops(virtualDisk.TotalIops) with { Max = DetailMax(virtualDisk.TotalIopsMax, virtualDisk.TotalIops) });
                DetailMetric(20, Branch(false, "Read IOPS"), Metric.Iops(virtualDisk.ReadIops) with { Max = DetailMax(virtualDisk.ReadIopsMax, virtualDisk.ReadIops) });
                DetailMetric(21, Branch(true, "Write IOPS"), Metric.Iops(virtualDisk.WriteIops) with { Max = DetailMax(virtualDisk.WriteIopsMax, virtualDisk.WriteIops) });
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
                DetailMetric(16, Branch(false, "Receive"), Metric.Mbps(virtualNic.RxMbps) with { Max = DetailMax(virtualNic.RxMbpsMax, virtualNic.RxMbps) });
                DetailMetric(17, Branch(true, "Transmit"), Metric.Mbps(virtualNic.TxMbps) with { Max = DetailMax(virtualNic.TxMbpsMax, virtualNic.TxMbps) });
                Detail(19, "Status", vnicStatus, StatusColor(vnicStatus));
                break;
            case DiskRow disk:
                Detail(7, "Name", disk.Name);
                Detail(8, "Host", disk.HostName);
                Detail(9, "Size", disk.Size);
                DetailScalar(10, Branch(false, "Used space"), disk.UsedSpace);
                DetailScalar(11, Branch(true, "Free space"), disk.FreeSpace);
                DetailScalar(12, string.Empty, string.Empty);
                DetailMetricHeader(13, string.Empty, "cur", "min");
                DetailMetric(14, "Free", disk.Free);
                DetailScalar(15, string.Empty, string.Empty);
                DetailMetricHeader(16, string.Empty, "cur", "max");
                DetailMetric(17, "Total I/O", disk.Io);
                DetailMetric(18, Branch(false, "Read I/O"), disk.ReadIo);
                DetailMetric(19, Branch(true, "Write I/O"), disk.WriteIo);
                DetailScalar(20, string.Empty, string.Empty);
                DetailMetricHeader(21, string.Empty, "cur", "max");
                DetailMetric(22, "Total IOPS", disk.Iops);
                DetailMetric(23, Branch(false, "Read IOPS"), disk.ReadIops);
                DetailMetric(24, Branch(true, "Write IOPS"), disk.WriteIops);
                DetailScalar(25, string.Empty, string.Empty);
                DetailMetricHeader(26, string.Empty, "cur", "max");
                DetailMetric(27, "Queue depth", disk.QueueDepth);
                DetailMetric(28, "Latency", disk.Latency);
                Detail(30, "Status", disk.Status, StatusColor(disk.Status));
                break;
            case PhysicalDiskRow disk:
                Detail(7, "PDID", disk.PhysicalDiskId);
                Detail(8, "Host", disk.HostName);
                Detail(9, "Instance", PhysicalDiskInstanceDisplay(disk));
                Detail(10, "Mapping", DetailValue(disk.Mapping), disk.Mapping.StartsWith("Inferred", StringComparison.OrdinalIgnoreCase) ? ConsoleColor.Yellow : ConsoleColor.Gray);
                Detail(11, "Friendly name", DetailValue(disk.FriendlyName));
                Detail(12, "Manufacturer", DetailValue(disk.Manufacturer));
                Detail(13, "Model", DetailValue(disk.Model));
                Detail(14, "Firmware", DetailValue(disk.FirmwareVersion));
                Detail(15, "Serial", DetailValue(disk.SerialNumber));
                Detail(16, "Type", disk.Type);
                Detail(17, "Size", disk.Size);
                DetailScalar(18, string.Empty, string.Empty);
                DetailMetricHeader(19, string.Empty, "cur", "max");
                DetailMetric(20, "Total I/O", disk.Io);
                DetailMetric(21, Branch(false, "Read I/O"), disk.ReadIo);
                DetailMetric(22, Branch(true, "Write I/O"), disk.WriteIo);
                DetailScalar(23, string.Empty, string.Empty);
                DetailMetricHeader(24, string.Empty, "cur", "max");
                DetailMetric(25, "Total IOPS", disk.Iops);
                DetailMetric(26, Branch(false, "Read IOPS"), disk.ReadIops);
                DetailMetric(27, Branch(true, "Write IOPS"), disk.WriteIops);
                DetailScalar(28, string.Empty, string.Empty);
                DetailMetricHeader(29, string.Empty, "cur", "max");
                DetailMetric(30, "Queue depth", disk.QueueDepth);
                DetailMetric(31, "Latency", disk.Latency);
                Detail(33, "Status", disk.Status, StatusColor(disk.Status));
                break;
            case NetworkRow net:
                Detail(7, "Name", net.Name);
                Detail(8, "Host", net.HostName);
                Detail(9, "Link", net.Link);
                DetailScalar(10, string.Empty, string.Empty);
                DetailMetricHeader(11, string.Empty, "cur", "max");
                DetailMetric(12, "Throughput", net.Throughput);
                DetailMetric(13, Branch(false, "Receive"), net.Rx);
                DetailMetric(14, Branch(false, "Transmit"), net.Tx);
                DetailMetric(15, Branch(false, "RDMA RX"), net.RdmaRx);
                DetailMetric(16, Branch(true, "RDMA TX"), net.RdmaTx);
                DetailScalar(17, string.Empty, string.Empty);
                DetailMetricHeader(18, string.Empty, "cur", "max");
                DetailMetric(19, "Drops", net.Drops);
                Detail(21, "Status", net.Status, StatusColor(net.Status));
                break;
        }
    }

    private const int DetailLabelWidth = 19;
    private const int CheckpointDetailLabelWidth = 22;

    private static string Branch(bool last, string label) => $"  {(last ? "\u2514" : "\u251c")} {label}";

    private static Metric DetailAggregateMetric(VDiskRow[] disks, Func<VDiskRow, double> currentSelector, Func<VDiskRow, double> maxSelector, Unit unit)
    {
        var current = disks.Sum(currentSelector);
        var max = disks.Sum(disk => DetailMax(maxSelector(disk), currentSelector(disk)));
        return new Metric(current, max, unit);
    }

    private static Metric DetailAggregateNetworkMetric(VmNetworkPathRow[] adapters, Func<VmNetworkPathRow, double> currentSelector, Func<VmNetworkPathRow, double> maxSelector)
    {
        var current = adapters.Sum(currentSelector);
        var max = adapters.Sum(adapter => DetailMax(maxSelector(adapter), currentSelector(adapter)));
        return Metric.Mbps(current) with { Max = max };
    }

    private int RenderVmCheckpoints(int y, VmCheckpointRow[] checkpoints)
    {
        if (!HasNamedCheckpointMetadata(checkpoints))
        {
            Detail(y, "Checkpoints", "None", ConsoleColor.DarkGray);
            if (HasActiveDifferencingDisk(checkpoints))
            {
                Detail(y + 1, "Differencing disk", "Active / merging", ConsoleColor.Yellow);
                return y + 1;
            }
            return y;
        }

        Detail(y++, "Checkpoints", "Present", ConsoleColor.Yellow);
        var ordered = OrderCheckpointDisplayRows(checkpoints);
        for (var i = 0; i < ordered.Length; i++)
        {
            var prefix = ordered[i].Depth <= 0
                ? string.Empty
                : new string(' ', ordered[i].Depth * 3 - 1) + "\u2514 ";
            var value = $"{prefix}{DisplayName(ordered[i].Name, Math.Max(32, Console.WindowWidth - 46 - prefix.Length))}";
            DetailScalar(y++, i == 0 ? "Checkpoints" : string.Empty, value);
        }

        DetailScalar(y++, string.Empty, string.Empty);
        CheckpointDetailMetricHeader(y++, string.Empty, "cur", "max");
        CheckpointDetailScalar(y++, "Checkpoint size", DetailCheckpointSizeValue(checkpoints.Sum(c => c.SizeMb), checkpoints.Sum(c => DetailMax(c.SizeMbMax, c.SizeMb))));
        var currentChange = checkpoints.Sum(c => c.ChangeMb);
        var maxChange = checkpoints.Sum(c => DetailMax(c.ChangeMbMax, c.ChangeMb));
        CheckpointDetailScalar(y++, "Checkpoint changes", DetailCheckpointChangeValue(currentChange, maxChange));
        return y - 1;
    }

    private static (string Name, int Depth)[] OrderCheckpointDisplayRows(VmCheckpointRow[] checkpoints)
    {
        var rows = checkpoints
            .Where(c => !string.IsNullOrWhiteSpace(c.Name) || !string.IsNullOrWhiteSpace(c.Path))
            .ToArray();
        var pathToCheckpointName = rows
            .Where(c => !IsNowCheckpoint(c) && !string.IsNullOrWhiteSpace(c.Path) && !string.IsNullOrWhiteSpace(c.Name))
            .GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
        var latestCheckpointName = rows
            .Where(c => !IsNowCheckpoint(c) && !string.IsNullOrWhiteSpace(c.Name))
            .OrderByDescending(c => c.Created == DateTime.MinValue ? DateTime.MinValue : c.Created)
            .Select(c => c.Name)
            .FirstOrDefault() ?? string.Empty;

        var displayRows = rows
            .GroupBy(c => IsNowCheckpoint(c) ? "Now / Active differencing disk" : CheckpointNodeName(c), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var name = group.Key;
                var parentName = group.Select(c => c.ParentName).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? string.Empty;
                if (IsNowCheckpointName(name))
                {
                    parentName = group
                        .Select(c => c.ParentPath)
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Select(p => pathToCheckpointName.TryGetValue(p, out var parent) ? parent : string.Empty)
                        .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? latestCheckpointName;
                }

                var created = group
                    .Where(c => c.Created != DateTime.MinValue)
                    .Select(c => c.Created)
                    .DefaultIfEmpty(DateTime.MaxValue)
                    .Min();
                return new CheckpointDisplayRow(name, parentName, created);
            })
            .Where(row => !string.IsNullOrWhiteSpace(row.Name))
            .DistinctBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var leafCheckpointName = LeafCheckpointName(displayRows);
        if (!string.IsNullOrWhiteSpace(leafCheckpointName))
        {
            displayRows = displayRows
                .Select(row => IsNowCheckpointName(row.Name) && !row.ParentName.Equals(leafCheckpointName, StringComparison.OrdinalIgnoreCase)
                    ? row with { ParentName = leafCheckpointName }
                    : row)
                .ToArray();
        }

        var nameSet = displayRows.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var children = displayRows
            .Where(c => !string.IsNullOrWhiteSpace(c.ParentName) && nameSet.Contains(c.ParentName))
            .GroupBy(c => c.ParentName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Created).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        if (children.Count == 0 && rows.Length > 1)
        {
            return displayRows
                .OrderBy(c => IsNowCheckpointName(c.Name) ? DateTime.MaxValue : c.Created)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select((row, index) => (row.Name, index))
                .ToArray();
        }

        var roots = displayRows
            .Where(c => string.IsNullOrWhiteSpace(c.ParentName) || !nameSet.Contains(c.ParentName))
            .OrderBy(c => IsNowCheckpointName(c.Name) ? DateTime.MaxValue : c.Created)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (roots.Length == 0)
            roots = displayRows.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToArray();

        var ordered = new List<(string Name, int Depth)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
            AddCheckpointChain(root, 0);
        foreach (var row in displayRows.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            AddCheckpointChain(row, 0);
        return ordered.ToArray();

        void AddCheckpointChain(CheckpointDisplayRow checkpoint, int depth)
        {
            if (!visited.Add(checkpoint.Name))
                return;

            ordered.Add((checkpoint.Name, depth));
            if (!children.TryGetValue(checkpoint.Name, out var descendants))
                return;

            foreach (var child in descendants)
                AddCheckpointChain(child, depth + 1);
        }
    }

    private static string LeafCheckpointName(CheckpointDisplayRow[] displayRows)
    {
        var checkpointRows = displayRows
            .Where(row => !IsNowCheckpointName(row.Name))
            .ToArray();
        if (checkpointRows.Length == 0)
            return string.Empty;

        var parentNames = checkpointRows
            .Select(row => row.ParentName)
            .Where(parent => !string.IsNullOrWhiteSpace(parent))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return checkpointRows
            .Where(row => !parentNames.Contains(row.Name))
            .OrderByDescending(row => row.Created == DateTime.MinValue ? DateTime.MinValue : row.Created)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .Select(row => row.Name)
            .FirstOrDefault() ?? string.Empty;
    }

    private static bool HasNamedCheckpointMetadata(VmCheckpointRow[] checkpoints)
        => checkpoints.Any(checkpoint => !IsNowCheckpoint(checkpoint)
            && (!string.IsNullOrWhiteSpace(checkpoint.Name) || !string.IsNullOrWhiteSpace(checkpoint.ParentName)));

    private static bool HasActiveDifferencingDisk(VmCheckpointRow[] checkpoints)
        => checkpoints.Any(checkpoint => IsNowCheckpoint(checkpoint)
            || (!string.IsNullOrWhiteSpace(checkpoint.Path)
                && checkpoint.Path.EndsWith(".avhdx", StringComparison.OrdinalIgnoreCase)));

    private static bool IsNowCheckpoint(VmCheckpointRow checkpoint)
        => IsNowCheckpointName(checkpoint.Name);

    private static bool IsNowCheckpointName(string name)
        => name.Equals("Now", StringComparison.OrdinalIgnoreCase)
           || name.Equals("Now / Active differencing disk", StringComparison.OrdinalIgnoreCase)
           || name.Equals("Active differencing disk", StringComparison.OrdinalIgnoreCase);

    private static string CheckpointNodeName(VmCheckpointRow checkpoint)
        => !string.IsNullOrWhiteSpace(checkpoint.Name)
            ? checkpoint.Name
            : DisplayName(checkpoint.Path, Math.Max(32, Console.WindowWidth - 46));

    private void Detail(int y, string label, string value, ConsoleColor color = ConsoleColor.Gray)
        => WriteLine(y, $"  {label,-DetailLabelWidth} {value}", color);

    private static string DetailValue(string value)
        => string.IsNullOrWhiteSpace(value) ? "n/a" : value;

    private void DetailMetric(int y, string label, Metric metric)
        => DetailScalar(y, label, DetailMetricValue(metric));

    private void DetailMetricHeader(int y, string label, string currentLabel, string maxLabel)
        => DetailScalar(y, label, DetailMetricHeaderValue(currentLabel, maxLabel), ConsoleColor.DarkCyan);

    private void CheckpointDetailMetricHeader(int y, string label, string currentLabel, string maxLabel)
        => CheckpointDetailScalar(y, label, CheckpointChangePrefix(string.Empty) + DetailMetricHeaderValue(currentLabel, maxLabel), ConsoleColor.DarkCyan);

    private void DetailCapacityMetricHeader(int y, string label)
        => DetailScalar(y, label, DetailCapacityMetricHeaderValue(), ConsoleColor.DarkCyan);

    private void DetailMetricWithCapacity(int y, string label, Metric metric, string capacity)
        => DetailScalar(y, label, DetailMetricWithCapacityValue(metric, capacity));

    private void DetailMetricSplit(int y, string label, Metric metric, double ratio)
        => DetailScalar(y, label, DetailSplitValue(metric, ratio));

    private static double DetailMax(double max, double current)
        => double.IsNaN(max) ? current : Math.Max(max, current);

    private void DetailScalar(int y, string label, string value, ConsoleColor color = ConsoleColor.Gray)
        => WriteLine(y, $"  {label,-DetailLabelWidth} {value}", color);

    private void CheckpointDetailScalar(int y, string label, string value, ConsoleColor color = ConsoleColor.Gray)
        => WriteLine(y, $"  {label,-CheckpointDetailLabelWidth} {value}", color);

    private void RenderDetailLines(int top, IReadOnlyList<DetailLine> lines)
    {
        var maxLines = Math.Max(0, ContentWindowHeight - (top - 4));
        if (maxLines <= 0)
        {
            tableScrollOffset = 0;
            return;
        }

        var selectedLine = lines
            .Select((line, index) => new { line, index })
            .FirstOrDefault(item => item.line.SelectionIndex == selected)?.index ?? 0;
        KeepLineVisible(lines.Count, maxLines, selectedLine);
        var visible = lines.Skip(tableScrollOffset).Take(maxLines).ToArray();
        for (var i = 0; i < visible.Length; i++)
        {
            var line = visible[i];
            if (line.Kind == DetailLineKind.Blank)
            {
                WriteLine(top + i, string.Empty);
                continue;
            }

            var selectedLineVisible = line.SelectionIndex == selected;
            var foreground = line.Kind == DetailLineKind.Header
                ? ConsoleColor.Yellow
                : selectedLineVisible ? ConsoleColor.White : RowColor(line.Row!);
            WriteLine(top + i, line.Text, foreground, selectedLineVisible ? ConsoleColor.DarkCyan : ConsoleColor.Black);
        }
    }

    private void KeepLineVisible(int lineCount, int maxLines, int selectedLine)
    {
        if (lineCount <= 0 || maxLines <= 0)
        {
            tableScrollOffset = 0;
            return;
        }

        tableScrollOffset = Math.Clamp(tableScrollOffset, 0, Math.Max(0, lineCount - maxLines));
        if (selectedLine < tableScrollOffset)
            tableScrollOffset = selectedLine;
        else if (selectedLine >= tableScrollOffset + maxLines)
            tableScrollOffset = selectedLine - maxLines + 1;
    }

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
            .Concat(snapshot.PhysicalDisks
                .Where(d => d.HostName.Equals(hostName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            .Concat(snapshot.NetworkSwitches
                .Where(n => n.HostName.Equals(hostName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
            .ToArray();
    }

    private object? ResolveDetailTarget()
        => ResolveDetailTarget(state.Read());

    private object? ResolveDetailTarget(Snapshot s)
    {
        if (string.IsNullOrEmpty(selectedItemName)) return null;
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
            Panel.PhysicalDisks => s.PhysicalDisks.FirstOrDefault(r => r.Name == selectedItemName && (string.IsNullOrEmpty(selectedHostName) || r.HostName == selectedHostName)),
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
        if (row is PhysicalDiskRow physicalDisk)
            return $"HOST {physicalDisk.HostName} -> physical disk {physicalDisk.Name}";
        return $"{detailPanel} detail: {GetRowName(row)}";
    }

    private static string GetRowName(object row) => row switch
    {
        ClusterRow cluster => cluster.Name,
        HostRow host => host.Name,
        VmRow vm => vm.Name,
        DiskRow disk => disk.Name,
        PhysicalDiskRow disk => disk.Name,
        NetworkSwitchRow networkSwitch => networkSwitch.Name,
        NetworkRow net => net.Name,
        VDiskRow disk => disk.Name,
        VmNetworkPathRow adapter => adapter.Name,
        VDiskDetailRow disk => disk.Disk.Name,
        VmNetworkDetailRow adapter => adapter.Adapter.Name,
        EventRow evt => evt.Message,
        _ => string.Empty
    };

    private static string PhysicalDiskInstanceDisplay(PhysicalDiskRow disk)
    {
        if (string.IsNullOrWhiteSpace(disk.SoftwareRaid))
            return disk.Name;

        var drive = disk.SoftwareRaid.Split(' ', 2)[0];
        if (!string.IsNullOrWhiteSpace(drive) && disk.Name.Contains(drive, StringComparison.OrdinalIgnoreCase))
        {
            var suffix = disk.SoftwareRaid[drive.Length..].TrimStart();
            return string.IsNullOrWhiteSpace(suffix) ? disk.Name : $"{disk.Name} {suffix}";
        }

        return $"{disk.Name} {disk.SoftwareRaid}";
    }

    private static VDiskRow[] GetVmDisks(VmRow vm, Snapshot snapshot)
    {
        return snapshot.VmTopology.FirstOrDefault(t => t.VmName == vm.Name && (string.IsNullOrWhiteSpace(t.HostName) || t.HostName.Equals(vm.HostName, StringComparison.OrdinalIgnoreCase)))?.Disks ?? [];
    }

    private static VmNetworkPathRow[] GetVmNetworkAdapters(VmRow vm, Snapshot snapshot)
    {
        return snapshot.VmTopology.FirstOrDefault(t => t.VmName == vm.Name && (string.IsNullOrWhiteSpace(t.HostName) || t.HostName.Equals(vm.HostName, StringComparison.OrdinalIgnoreCase)))?.Networks ?? [];
    }

    private static VmCheckpointRow[] GetVmCheckpoints(VmRow vm, Snapshot snapshot)
    {
        return snapshot.VmTopology.FirstOrDefault(t => t.VmName == vm.Name && (string.IsNullOrWhiteSpace(t.HostName) || t.HostName.Equals(vm.HostName, StringComparison.OrdinalIgnoreCase)))?.Checkpoints ?? [];
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

    private static string FmtDashboardWithCapacity(Metric metric, string capacity)
        => Cell($"{Cell(FmtValue(metric.Current, metric.Unit), 4, true)} | {Cell(FmtValue(metric.Max, metric.Unit), 4, true)} | {Cell($"({capacity})", DashboardCapacityConfigWidth)}", DashboardCapacityMetricWidth);

    private static string DetailMetricValue(Metric metric)
        => $"{Cell(FmtValue(metric.Current, metric.Unit), 9, true)} | {Cell(FmtValue(metric.Max, metric.Unit), 9)}";

    private static string DetailSizeValue(double currentMb, double maxMb, bool signed = false)
        => $"{Cell(FormatSizeMb(currentMb, signed), 9, true)} | {Cell(FormatSizeMb(maxMb, signed), 9)}";

    private static string DetailCheckpointSizeValue(double currentMb, double maxMb)
        => $"{CheckpointChangePrefix(string.Empty)}{Cell(FormatSizeMb(currentMb), 9, true)} | {Cell(FormatSizeMb(maxMb), 9)}";

    private static string DetailCheckpointChangeValue(double currentMb, double maxMb)
    {
        var token = ChangeToken(currentMb);
        if (string.IsNullOrEmpty(token))
            token = ChangeToken(maxMb);
        var value = $"{CheckpointChangePrefix(token)}{Cell(FormatSizeMb(Math.Abs(currentMb)), 9, true)} | {Cell(FormatSizeMb(Math.Abs(maxMb)), 9)}";
        var suffix = ChangeToken(maxMb);
        return string.IsNullOrEmpty(suffix) ? value : $"{value.TrimEnd()}  {suffix}";
    }

    private static string CheckpointChangePrefix(string token)
        => string.IsNullOrEmpty(token) ? "    " : $"{token} ";

    private static string DetailMetricHeaderValue(string currentLabel, string maxLabel)
        => $"{Cell(currentLabel, 9, true)} | {Cell(maxLabel, 9)}";

    private static string DetailMetricWithCapacityValue(Metric metric, string capacity)
        => $"{Cell(FmtValue(metric.Current, metric.Unit), 4, true)} | {Cell(FmtValue(metric.Max, metric.Unit), 4, true)} | {Cell($"({capacity})", 12)}";

    private static string DetailCapacityMetricHeaderValue()
        => $"{Cell("cur", 4, true)} | {Cell("max", 4, true)} | {Cell("cfg", 12)}";

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
            Unit.QueueDepth => FormatInteger(value),
            _ => FormatNumber4(value)
        };
    }

    private static string FormatRate(double megabytesPerSecond)
    {
        var kb = megabytesPerSecond * 1024;
        if (Math.Abs(kb) < 1024)
            return $"{FormatNumber4(kb)} KB/s";

        if (Math.Abs(megabytesPerSecond) < 1024)
            return $"{FormatNumber4(megabytesPerSecond)} MB/s";

        return $"{FormatNumber4(megabytesPerSecond / 1024)} GB/s";
    }

    private static string FormatCompact(double value, string suffix, string kiloSuffix)
    {
        if (Math.Abs(value) >= 1000)
            return $"{FormatNumber4(value / 1000)}{kiloSuffix}";
        return $"{FormatNumber4(value)}{suffix}";
    }

    private static string FormatSizeMb(double megabytes, bool signed = false)
    {
        if (double.IsNaN(megabytes)) return "n/a";
        var sign = signed && megabytes > 0 ? "+" : string.Empty;
        var abs = Math.Abs(megabytes);
        if (abs >= 1024 * 1024)
            return $"{sign}{FormatNumber4(megabytes / 1024 / 1024)} TB";
        if (abs >= 1024)
            return $"{sign}{FormatNumber4(megabytes / 1024)} GB";
        return $"{sign}{FormatNumber4(megabytes)} MB";
    }

    private static string ChangeToken(double value)
    {
        if (value > 0.0001) return "(+)";
        if (value < -0.0001) return "(-)";
        return string.Empty;
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
        PhysicalDiskRow disk => StatusColor(disk.Status),
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
           && snapshot.PhysicalDisks.Length == 0
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
            TableKind.PhysicalDiskLike => HostColumnWidth + PdidWidth + TypeWidth + SizeWidth + MetricWidth + ShortMetricWidth + ShortMetricWidth + ShortMetricWidth + StatusWidth,
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
            TableKind.PhysicalDiskLike => 9,
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
        var priorMapContentLines = mapContentLines;
        mapContentLines = false;
        var lines = Math.Min(previousLines.Length, touchedLines.Length);
        for (var y = 0; y < lines; y++)
        {
            if (!touchedLines[y] && !string.IsNullOrEmpty(previousLines[y]))
                WriteLine(y, string.Empty);
        }
        mapContentLines = priorMapContentLines;
    }

    private void WriteLine(int y, string text, ConsoleColor foreground = ConsoleColor.Gray, ConsoleColor background = ConsoleColor.Black)
    {
        var x = 0;
        if (mapContentLines && y >= 4)
        {
            var mappedY = mappedContentTop + (y - 4);
            if (mappedY < mappedContentTop || mappedY >= mappedContentTop + mappedContentHeight)
                return;
            WritePaneContentLine(mappedY, text, foreground, background);
            return;
        }

        if (y < 0 || y >= frameHeight || y >= touchedLines.Length || y >= previousLines.Length) return;
        touchedLines[y] = true;

        var width = Math.Min(frameWidth, Math.Max(0, Console.WindowWidth));
        if (width <= 1) return;
        width--;
        if (width <= 0) return;
        if (text.Length > width)
            text = text[..width];
        var compareText = text.Length < width ? text.PadRight(width) : text;

        if (previousLines[y] == compareText && previousForegrounds[y] == foreground && previousBackgrounds[y] == background)
            return;

        try
        {
            if (y >= Console.WindowHeight) return;
            Console.SetCursorPosition(x, y);
            Console.ForegroundColor = foreground;
            Console.BackgroundColor = background;
            Console.Write(compareText);
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

        previousLines[y] = compareText;
        previousForegrounds[y] = foreground;
        previousBackgrounds[y] = background;
    }

    private void WritePaneContentLine(int y, string text, ConsoleColor foreground = ConsoleColor.Gray, ConsoleColor background = ConsoleColor.Black)
    {
        if (y < 0 || y >= frameHeight || y >= touchedLines.Length || y >= previousLines.Length) return;
        touchedLines[y] = true;

        var width = Math.Min(frameWidth, Math.Max(0, Console.WindowWidth));
        if (width <= 3) return;
        width--;
        var contentWidth = Math.Max(0, width - 2);
        if (text.Length > contentWidth)
            text = text[..contentWidth];
        var contentText = text.Length < contentWidth ? text.PadRight(contentWidth) : text;
        var compareText = "\u2502" + contentText + "\u2502";

        if (previousLines[y] == compareText && previousForegrounds[y] == foreground && previousBackgrounds[y] == background)
            return;

        try
        {
            if (y >= Console.WindowHeight) return;
            Console.SetCursorPosition(0, y);
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.BackgroundColor = ConsoleColor.Black;
            Console.Write('\u2502');

            if (background != ConsoleColor.Black)
            {
                var highlighted = text.TrimEnd();
                Console.ForegroundColor = foreground;
                Console.BackgroundColor = background;
                Console.Write(highlighted);
                var remaining = contentWidth - highlighted.Length;
                if (remaining > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.BackgroundColor = ConsoleColor.Black;
                    Console.Write(new string(' ', remaining));
                }
            }
            else
            {
                Console.ForegroundColor = foreground;
                Console.BackgroundColor = background;
                Console.Write(contentText);
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.BackgroundColor = ConsoleColor.Black;
            Console.Write('\u2502');
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

        previousLines[y] = compareText;
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

internal enum Panel { Cluster, Hosts, Vms, Disks, PhysicalDisks, Network, Events }

internal enum DrillView { Overview, HostVms, NetworkAdapters, Detail }

internal enum TableKind { ClusterLike, HostLike, VmLike, DiskLike, PhysicalDiskLike, NetworkLike, NetworkSwitchLike }

internal enum DetailLineKind { Header, Selectable, Blank }

internal sealed record CheckpointDisplayRow(string Name, string ParentName, DateTime Created);

internal sealed record DetailLine(string Text, object? Row, int SelectionIndex, DetailLineKind Kind)
{
    public static DetailLine Header(string text) => new(text, null, -1, DetailLineKind.Header);
    public static DetailLine Selectable(string text, object row, int selectionIndex) => new(text, row, selectionIndex, DetailLineKind.Selectable);
    public static DetailLine Blank() => new(string.Empty, null, -1, DetailLineKind.Blank);
}

internal sealed record ViewState(
    Panel Panel,
    int Selected,
    int TableScrollOffset,
    DrillView DrillView,
    Panel DetailPanel,
    string? SelectedHostName,
    string? SelectedItemName);

internal sealed record PaneState(
    Panel Panel,
    int Selected,
    int TableScrollOffset,
    DrillView DrillView,
    Panel DetailPanel,
    string? SelectedHostName,
    string? SelectedItemName,
    Stack<ViewState> BackStack,
    Dictionary<string, SortState> SortStates);

internal sealed record SortState(string Column, bool Descending);

internal sealed record SortColumn(string Key, string Label);
