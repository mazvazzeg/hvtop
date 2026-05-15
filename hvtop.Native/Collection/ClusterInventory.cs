namespace hvtop.Native;

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

