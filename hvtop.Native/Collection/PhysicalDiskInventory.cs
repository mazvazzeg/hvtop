namespace hvtop.Native;

internal static class PhysicalDiskInventory
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private static DateTime lastRefresh = DateTime.MinValue;
    private static Dictionary<string, PhysicalDiskInventoryEntry> entries = new(StringComparer.OrdinalIgnoreCase);
    private static PhysicalDiskInventoryJson[] rawRows = [];
    private static string source = "none";
    private static string lastError = string.Empty;
    private static string virtualStorageType = "n/a";
    private static string virtualStorageEvidence = string.Empty;

    public static PhysicalDiskInventoryEntry Find(string counterInstance)
    {
        EnsureFresh();
        var id = ResolvePhysicalDiskId(counterInstance);
        if (!string.IsNullOrWhiteSpace(id) && entries.TryGetValue(id, out var byId))
            return ApplyVirtualFallback(byId);

        var inferred = InferUnmappedPhysicalDisk(id);
        if (inferred is not null)
            return inferred;

        return VirtualFallbackEntry(id);
    }

    public static InventoryEvent[] BuildDiagnostics(PhysicalDiskRow[] disks)
    {
        EnsureFresh();
        var sources = string.Join(",", rawRows
            .GroupBy(row => string.IsNullOrWhiteSpace(row.Source) ? "unknown" : row.Source.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}:{group.Count()}"));
        var events = new List<InventoryEvent>
        {
            new("INFO", $"PDDIAG sources={sources} raw={rawRows.Length} mapped={entries.Count} counters={disks.Length}{(string.IsNullOrWhiteSpace(lastError) ? string.Empty : $" error='{TrimForEvent(lastError, 70)}'")}")
        };
        if (!virtualStorageType.Equals("n/a", StringComparison.OrdinalIgnoreCase))
            events.Add(new InventoryEvent("INFO", $"PDVIRT type='{virtualStorageType}' evidence='{TrimForEvent(virtualStorageEvidence, 90)}'"));

        foreach (var row in rawRows.Take(12))
        {
            var id = InventoryId(row);
            var mapped = !string.IsNullOrWhiteSpace(id) && entries.TryGetValue(id, out var entry)
                ? entry
                : BuildEntry(row, rawRows);
            events.Add(new InventoryEvent(
                "INFO",
                $"PDINV src={row.Source ?? source} id='{TrimForEvent(id, 12)}' dev='{TrimForEvent(row.DeviceId ?? string.Empty, 12)}' node='{TrimForEvent(row.ConnectedNode ?? string.Empty, 18)}' ndev='{TrimForEvent(row.ConnectedNodeDeviceNumber ?? string.Empty, 8)}' friendly='{TrimForEvent(row.FriendlyName ?? string.Empty, 28)}' bus='{TrimForEvent(row.BusType ?? string.Empty, 10)}' media='{TrimForEvent(row.MediaType ?? string.Empty, 10)}' type='{mapped.Type}' size='{mapped.Size}' map='{TrimForEvent(mapped.Mapping, 18)}' model='{TrimForEvent(mapped.Model, 24)}' fw='{TrimForEvent(mapped.FirmwareVersion, 12)}' sn='{TrimForEvent(mapped.SerialNumber, 18)}'"));
        }

        foreach (var disk in disks.Take(16))
        {
            var missing = disk.Type.Equals("n/a", StringComparison.OrdinalIgnoreCase) || disk.Size.Equals("n/a", StringComparison.OrdinalIgnoreCase);
            events.Add(new InventoryEvent(
                missing ? "WARN" : "INFO",
                $"PDMAP inst='{TrimForEvent(disk.Name, 30)}' id='{TrimForEvent(disk.PhysicalDiskId, 12)}' type='{disk.Type}' size='{disk.Size}'"));
        }

        return events.ToArray();
    }

    private static void EnsureFresh()
    {
        if (DateTime.UtcNow - lastRefresh < CacheDuration)
            return;

        lastRefresh = DateTime.UtcNow;
        var script =
            "$ErrorActionPreference='SilentlyContinue'; " +
            "function _hvprop($o,$n){ if($null -eq $o){ '' } else { $p=$o.PSObject.Properties[$n]; if($null -eq $p -or $null -eq $p.Value){ '' } else { [string]$p.Value } } }; " +
            "function _hvnum($o,$n){ if($null -eq $o){ [uint64]0 } else { $p=$o.PSObject.Properties[$n]; if($null -eq $p -or $null -eq $p.Value){ [uint64]0 } else { [uint64]$p.Value } } }; " +
            "function First-NonEmpty(){ foreach($v in $args){ if(-not [string]::IsNullOrWhiteSpace([string]$v)){ [string]$v; return } }; '' }; " +
            "$cs=@(); try { $c=Get-CimInstance Win32_ComputerSystem; $cs=@([pscustomobject]@{Source='ComputerSystem';DeviceId='';DeviceNumber='';FriendlyName=$c.Model;MediaType='';BusType='';Size=0;Manufacturer=$c.Manufacturer;Model=$c.Model;FirmwareVersion='';SerialNumber=''}) } catch { }; " +
            "$ctrl=@(); try { $ctrl=@(Get-CimInstance Win32_SCSIController | Select-Object @{n='Source';e={'StorageController'}},@{n='DeviceId';e={''}},@{n='DeviceNumber';e={''}},@{n='FriendlyName';e={$_.Caption}},@{n='MediaType';e={''}},@{n='BusType';e={'SCSI'}},@{n='Size';e={[uint64]0}},Manufacturer,@{n='Model';e={$_.Name}},@{n='FirmwareVersion';e={''}},@{n='SerialNumber';e={''}}) } catch { }; " +
            "$disk=@(Get-Disk | Select-Object @{n='Source';e={'Get-Disk'}},@{n='DeviceId';e={[string]$_.Number}},@{n='DeviceNumber';e={[string]$_.Number}},FriendlyName,MediaType,BusType,Size,@{n='Manufacturer';e={_hvprop $_ 'Manufacturer'}},@{n='Model';e={_hvprop $_ 'Model'}},@{n='FirmwareVersion';e={_hvprop $_ 'FirmwareVersion'}},@{n='SerialNumber';e={_hvprop $_ 'SerialNumber'}}); " +
            "$drive=@(Get-CimInstance Win32_DiskDrive | Select-Object @{n='Source';e={'Win32_DiskDrive'}},@{n='DeviceId';e={[string]$_.Index}},@{n='DeviceNumber';e={[string]$_.Index}},@{n='FriendlyName';e={$_.Model}},MediaType,@{n='BusType';e={$_.InterfaceType}},Size,Manufacturer,Model,@{n='FirmwareVersion';e={$_.FirmwareRevision}},SerialNumber); " +
            "$sbc=@(); try { $sbc=@(Get-StorageBusClientDevice | Select-Object @{n='Source';e={'StorageBusClientDevice'}},@{n='DeviceId';e={First-NonEmpty (_hvprop $_ 'ConnectedNodeDeviceNumber') (_hvprop $_ 'DiskNumber') (_hvprop $_ 'Number') (_hvprop $_ 'Id')}},@{n='DeviceNumber';e={First-NonEmpty (_hvprop $_ 'ConnectedNodeDeviceNumber') (_hvprop $_ 'DiskNumber') (_hvprop $_ 'Number')}},@{n='FriendlyName';e={First-NonEmpty (_hvprop $_ 'ProductId') (_hvprop $_ 'Model') (_hvprop $_ 'Id')}},@{n='MediaType';e={_hvprop $_ 'DeviceType'}},@{n='BusType';e={'S2D'}},@{n='Size';e={_hvnum $_ 'Size'}},@{n='Manufacturer';e={_hvprop $_ 'VendorId'}},@{n='Model';e={First-NonEmpty (_hvprop $_ 'ProductId') (_hvprop $_ 'Model')}},@{n='FirmwareVersion';e={_hvprop $_ 'FirmwareVersion'}},@{n='SerialNumber';e={_hvprop $_ 'SerialNumber'}},@{n='ConnectedNode';e={_hvprop $_ 'ConnectedNode'}},@{n='ConnectedNodeDeviceNumber';e={_hvprop $_ 'ConnectedNodeDeviceNumber'}},@{n='Flags';e={(_hvprop $_ 'Flags')}}) } catch { }; " +
            "$nodeView=@(); try { $node=Get-StorageNode -Name $env:COMPUTERNAME -ErrorAction SilentlyContinue; if($node){ $nodeView=@(Get-PhysicalDisk | Get-PhysicalDiskStorageNodeView -StorageNode $node | Where-Object { $_.IsPhysicallyConnected } | Select-Object @{n='Source';e={'StorageNodeView'}},@{n='DeviceId';e={[string]$_.DiskNumber}},@{n='DeviceNumber';e={_hvprop $_.PhysicalDisk 'DeviceNumber'}},@{n='FriendlyName';e={_hvprop $_.PhysicalDisk 'FriendlyName'}},@{n='MediaType';e={_hvprop $_.PhysicalDisk 'MediaType'}},@{n='BusType';e={_hvprop $_.PhysicalDisk 'BusType'}},@{n='Size';e={_hvnum $_.PhysicalDisk 'Size'}},@{n='Manufacturer';e={_hvprop $_.PhysicalDisk 'Manufacturer'}},@{n='Model';e={_hvprop $_.PhysicalDisk 'Model'}},@{n='FirmwareVersion';e={_hvprop $_.PhysicalDisk 'FirmwareVersion'}},@{n='SerialNumber';e={_hvprop $_.PhysicalDisk 'SerialNumber'}}) } } catch { }; " +
            "$physicalLocal=@(); try { $physicalLocal=@(Get-PhysicalDisk -PhysicallyConnected | Select-Object @{n='Source';e={'Get-PhysicalDiskLocal'}},@{n='DeviceId';e={[string]$_.DeviceId}},@{n='DeviceNumber';e={_hvprop $_ 'DeviceNumber'}},FriendlyName,MediaType,BusType,Size,@{n='Manufacturer';e={_hvprop $_ 'Manufacturer'}},@{n='Model';e={_hvprop $_ 'Model'}},@{n='FirmwareVersion';e={_hvprop $_ 'FirmwareVersion'}},@{n='SerialNumber';e={_hvprop $_ 'SerialNumber'}}) } catch { }; " +
            "$physical=@(Get-PhysicalDisk | Select-Object @{n='Source';e={'Get-PhysicalDisk'}},@{n='DeviceId';e={[string]$_.DeviceId}},@{n='DeviceNumber';e={_hvprop $_ 'DeviceNumber'}},FriendlyName,MediaType,BusType,Size,@{n='Manufacturer';e={_hvprop $_ 'Manufacturer'}},@{n='Model';e={_hvprop $_ 'Model'}},@{n='FirmwareVersion';e={_hvprop $_ 'FirmwareVersion'}},@{n='SerialNumber';e={_hvprop $_ 'SerialNumber'}}); " +
            "$rows=@($cs + $ctrl + $disk + $drive + $sbc + $nodeView + $physicalLocal + $physical); " +
            "$rows | ConvertTo-Json -Compress";

        if (!PowerShellRunner.TryRun(script, 5000, out var output) || string.IsNullOrWhiteSpace(output))
        {
            entries = new Dictionary<string, PhysicalDiskInventoryEntry>(StringComparer.OrdinalIgnoreCase);
            rawRows = [];
            source = "failed";
            lastError = string.IsNullOrWhiteSpace(output) ? "PowerShell failed or timed out" : output;
            virtualStorageType = "n/a";
            virtualStorageEvidence = string.Empty;
            return;
        }

        try
        {
            var rows = output.TrimStart().StartsWith("[", StringComparison.Ordinal)
                ? JsonSerializer.Deserialize(output, HvtopJsonContext.Default.PhysicalDiskInventoryJsonArray) ?? []
                : [JsonSerializer.Deserialize(output, HvtopJsonContext.Default.PhysicalDiskInventoryJson)!];

            rawRows = rows
                .Where(row => row is not null)
                .Where(IsLocalInventoryRow)
                .ToArray();
            virtualStorageEvidence = BuildVirtualStorageEvidence(rawRows);
            virtualStorageType = DetectVirtualStorageType(virtualStorageEvidence);
            source = string.Join("+", rawRows
                .Select(row => row.Source)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(source))
                source = "none";
            lastError = string.Empty;
            entries = rawRows
                .Where(row => !string.IsNullOrWhiteSpace(InventoryId(row)))
                .Select(row => BuildEntry(row, rawRows))
                .GroupBy(row => row.PhysicalDiskId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, ChooseBestEntry, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            entries = new Dictionary<string, PhysicalDiskInventoryEntry>(StringComparer.OrdinalIgnoreCase);
            rawRows = [];
            source = "parse-failed";
            lastError = "Physical disk inventory JSON parse failed";
            virtualStorageType = "n/a";
            virtualStorageEvidence = string.Empty;
        }
    }

    public static string ResolvePhysicalDiskId(string counterInstance)
    {
        var value = counterInstance.Trim();
        var digits = new string(value.TakeWhile(char.IsDigit).ToArray());
        return string.IsNullOrWhiteSpace(digits) ? value : digits;
    }

    private static bool IsLocalInventoryRow(PhysicalDiskInventoryJson row)
    {
        if (row.Source?.Equals("StorageBusClientDevice", StringComparison.OrdinalIgnoreCase) != true)
            return true;

        var connectedNode = row.ConnectedNode?.Trim();
        return string.IsNullOrWhiteSpace(connectedNode) || IsSameNode(connectedNode, Environment.MachineName);
    }

    private static bool IsSameNode(string left, string right)
    {
        static string NormalizeNode(string value)
        {
            value = value.Trim();
            var dot = value.IndexOf('.');
            return dot >= 0 ? value[..dot] : value;
        }

        return NormalizeNode(left).Equals(NormalizeNode(right), StringComparison.OrdinalIgnoreCase);
    }

    private static PhysicalDiskInventoryEntry BuildEntry(PhysicalDiskInventoryJson row, PhysicalDiskInventoryJson[] allRows)
    {
        var id = InventoryId(row);
        var friendlyName = row.FriendlyName?.Trim() ?? string.Empty;
        var busType = row.BusType;
        var mediaType = row.MediaType;
        var manufacturer = row.Manufacturer?.Trim() ?? string.Empty;
        var model = row.Model?.Trim() ?? string.Empty;
        var firmware = row.FirmwareVersion?.Trim() ?? string.Empty;
        var serial = row.SerialNumber?.Trim() ?? string.Empty;
        var size = row.Size;

        if (row.Source?.Equals("Win32_DiskDrive", StringComparison.OrdinalIgnoreCase) == true
            || row.Source?.Equals("StorageBusClientDevice", StringComparison.OrdinalIgnoreCase) == true)
        {
            var storageRow = row.Source.Equals("StorageBusClientDevice", StringComparison.OrdinalIgnoreCase)
                ? FindStoragePhysicalDiskBySerial(row, allRows) ?? FindStoragePhysicalDisk(row, allRows)
                : FindStoragePhysicalDisk(row, allRows);
            if (storageRow is not null)
            {
                busType = storageRow.BusType;
                mediaType = storageRow.MediaType;
                manufacturer = FirstNonEmpty(manufacturer, storageRow.Manufacturer);
                model = FirstNonEmpty(model, storageRow.Model, storageRow.FriendlyName);
                firmware = FirstNonEmpty(firmware, storageRow.FirmwareVersion);
                serial = FirstNonEmpty(serial, storageRow.SerialNumber);
                if (size == 0)
                    size = storageRow.Size;
            }
        }

        return ApplyVirtualFallback(new PhysicalDiskInventoryEntry(
            id,
            friendlyName,
            FormatType(busType, mediaType),
            FormatSize(size),
            manufacturer,
            model,
            firmware,
            serial,
            MappingLabel(row)));
    }

    private static PhysicalDiskInventoryEntry ChooseBestEntry(IEnumerable<PhysicalDiskInventoryEntry> entries)
        => entries
            .OrderByDescending(Completeness)
            .First();

    private static int Completeness(PhysicalDiskInventoryEntry entry)
    {
        var score = 0;
        if (!entry.Type.Equals("n/a", StringComparison.OrdinalIgnoreCase))
            score += 2;
        if (!entry.Size.Equals("n/a", StringComparison.OrdinalIgnoreCase))
            score += 2;
        if (!string.IsNullOrWhiteSpace(entry.FriendlyName))
            score += 1;
        if (!string.IsNullOrWhiteSpace(entry.Model))
            score += 1;
        if (!string.IsNullOrWhiteSpace(entry.SerialNumber))
            score += 1;
        if (entry.Mapping.StartsWith("Direct (StorageBusClientDevice", StringComparison.OrdinalIgnoreCase))
            score += 3;
        return score;
    }

    private static PhysicalDiskInventoryJson? FindStoragePhysicalDisk(PhysicalDiskInventoryJson row, PhysicalDiskInventoryJson[] allRows)
    {
        var name = row.FriendlyName?.Trim();
        if (string.IsNullOrWhiteSpace(name) || row.Size == 0)
            return null;

        return allRows
            .Where(candidate => candidate.Source?.Equals("Get-PhysicalDisk", StringComparison.OrdinalIgnoreCase) == true)
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.FriendlyName)
                && candidate.FriendlyName.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)
                && IsCloseSize(candidate.Size, row.Size))
            .OrderBy(candidate => Math.Abs((double)candidate.Size - row.Size))
            .FirstOrDefault();
    }

    private static PhysicalDiskInventoryJson? FindStoragePhysicalDiskBySerial(PhysicalDiskInventoryJson row, PhysicalDiskInventoryJson[] allRows)
    {
        var serial = row.SerialNumber?.Trim();
        if (string.IsNullOrWhiteSpace(serial))
            return null;

        return allRows
            .Where(candidate => candidate.Source?.Equals("Get-PhysicalDisk", StringComparison.OrdinalIgnoreCase) == true
                || candidate.Source?.Equals("Get-PhysicalDiskLocal", StringComparison.OrdinalIgnoreCase) == true
                || candidate.Source?.Equals("StorageNodeView", StringComparison.OrdinalIgnoreCase) == true)
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.SerialNumber)
                && candidate.SerialNumber.Trim().Equals(serial, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.Size)
            .FirstOrDefault();
    }

    private static PhysicalDiskInventoryEntry? InferUnmappedPhysicalDisk(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var candidates = PhysicalRowsForInference()
            .Select(row => BuildEntry(row, rawRows))
            .Where(row => !string.IsNullOrWhiteSpace(row.Type)
                && !row.Type.Equals("n/a", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(row.Size)
                && !row.Size.Equals("n/a", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (candidates.Length == 0)
            return null;

        var groups = candidates
            .GroupBy(row => (row.Type, row.Size))
            .OrderByDescending(group => group.Count())
            .ToArray();

        var best = groups.First();
        if (best.Count() < 2)
            return null;

        return new PhysicalDiskInventoryEntry(
            id,
            string.Empty,
            best.Key.Type,
            best.Key.Size,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            $"Inferred from {best.Count()} matching physical disk(s)");
    }

    private static PhysicalDiskInventoryJson[] PhysicalRowsForInference()
    {
        var nodeView = rawRows
            .Where(row => row.Source?.Equals("StorageNodeView", StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
        if (nodeView.Length > 0)
            return nodeView;

        var local = rawRows
            .Where(row => row.Source?.Equals("Get-PhysicalDiskLocal", StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
        if (local.Length > 0)
            return local;

        return rawRows
            .Where(row => row.Source?.Equals("Get-PhysicalDisk", StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
    }

    private static bool IsCloseSize(ulong left, ulong right)
    {
        if (left == 0 || right == 0)
            return false;

        var delta = Math.Abs((double)left - right);
        var tolerance = Math.Max(1024d * 1024 * 1024, Math.Max(left, right) * 0.02d);
        return delta <= tolerance;
    }

    private static string FormatType(string? busType, string? mediaType)
    {
        var bus = Normalize(busType);
        var media = Normalize(mediaType);

        if (bus.Equals("NVMe", StringComparison.OrdinalIgnoreCase))
            return "NVMe";

        if (string.IsNullOrWhiteSpace(bus) && string.IsNullOrWhiteSpace(media))
            return "n/a";
        if (string.IsNullOrWhiteSpace(bus))
            return media;
        if (string.IsNullOrWhiteSpace(media) || media.Equals("Unspecified", StringComparison.OrdinalIgnoreCase))
            return bus;
        if (bus.Equals(media, StringComparison.OrdinalIgnoreCase))
            return bus;

        return $"{bus} {media}";
    }

    private static string Normalize(string? value)
    {
        value = value?.Trim() ?? string.Empty;
        return value.ToUpperInvariant() switch
        {
            "FIXED HARD DISK MEDIA" => string.Empty,
            "UNKNOWN" => string.Empty,
            "UNSPECIFIED" => string.Empty,
            "SSD" => "SSD",
            "HDD" => "HDD",
            "SCM" => "SCM",
            "SATA" => "SATA",
            "SAS" => "SAS",
            "NVME" => "NVMe",
            "ISCSI" => "iSCSI",
            "FIBRE CHANNEL" => "FC",
            "FC" => "FC",
            _ => value
        };
    }

    private static string FormatSize(ulong size)
        => size == 0 ? "n/a" : CapacityFormatter.FormatPhysicalDiskCapacity(size);

    private static string MappingLabel(PhysicalDiskInventoryJson row)
    {
        var sourceName = row.Source ?? "unknown";
        if (!sourceName.Equals("StorageBusClientDevice", StringComparison.OrdinalIgnoreCase))
            return $"Direct ({sourceName})";

        var details = new[]
            {
                string.IsNullOrWhiteSpace(row.ConnectedNode) ? string.Empty : $"node={row.ConnectedNode}",
                string.IsNullOrWhiteSpace(row.ConnectedNodeDeviceNumber) ? string.Empty : $"ndev={row.ConnectedNodeDeviceNumber}"
            }
            .Where(value => !string.IsNullOrWhiteSpace(value));
        var suffix = string.Join(" ", details);
        return string.IsNullOrWhiteSpace(suffix)
            ? "Direct (StorageBusClientDevice)"
            : $"Direct (StorageBusClientDevice {suffix})";
    }

    private static PhysicalDiskInventoryEntry MissingEntry(string id)
        => new(id, string.Empty, "n/a", "n/a", string.Empty, string.Empty, string.Empty, string.Empty, "n/a");

    private static PhysicalDiskInventoryEntry VirtualFallbackEntry(string id)
        => ApplyVirtualFallback(MissingEntry(id));

    private static PhysicalDiskInventoryEntry ApplyVirtualFallback(PhysicalDiskInventoryEntry entry)
    {
        if (virtualStorageType.Equals("n/a", StringComparison.OrdinalIgnoreCase))
            return entry;
        if (!entry.Type.Equals("n/a", StringComparison.OrdinalIgnoreCase)
            && !IsWeakVirtualDiskType(entry.Type)
            && !LooksLikeVirtualDisk(entry))
            return entry;

        var friendlyName = string.IsNullOrWhiteSpace(entry.FriendlyName)
            ? virtualStorageType
            : entry.FriendlyName;
        var model = string.IsNullOrWhiteSpace(entry.Model)
            ? virtualStorageType
            : entry.Model;
        var mapping = entry.Mapping.Equals("n/a", StringComparison.OrdinalIgnoreCase)
            ? $"Virtual platform fallback ({virtualStorageType})"
            : entry.Type.Equals("n/a", StringComparison.OrdinalIgnoreCase) || IsWeakVirtualDiskType(entry.Type)
                ? $"{entry.Mapping}; virtual platform fallback"
                : entry.Mapping;

        return new PhysicalDiskInventoryEntry(
            entry.PhysicalDiskId,
            friendlyName,
            virtualStorageType,
            entry.Size,
            entry.Manufacturer,
            model,
            entry.FirmwareVersion,
            entry.SerialNumber,
            mapping);
    }

    private static bool IsWeakVirtualDiskType(string type)
        => type.Equals("SCSI", StringComparison.OrdinalIgnoreCase)
           || type.Equals("IDE", StringComparison.OrdinalIgnoreCase)
           || type.Equals("ATA", StringComparison.OrdinalIgnoreCase)
           || type.Equals("SCSI HDD", StringComparison.OrdinalIgnoreCase)
           || type.Equals("SCSI SSD", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeVirtualDisk(PhysicalDiskInventoryEntry entry)
    {
        var evidence = $"{entry.FriendlyName} {entry.Manufacturer} {entry.Model}";
        return ContainsAny(evidence, "Msft Virtual", "Microsoft Virtual", "Virtual Disk", "Virtual HD", "VMware Virtual", "QEMU", "VirtIO", "VirtualBox");
    }

    private static string BuildVirtualStorageEvidence(PhysicalDiskInventoryJson[] rows)
        => string.Join(" ", rows
            .Where(row => row.Source is "ComputerSystem" or "StorageController" or "Win32_DiskDrive" or "Get-Disk")
            .SelectMany(row => new[] { row.Manufacturer, row.Model, row.FriendlyName, row.BusType, row.MediaType })
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string DetectVirtualStorageType(string evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence))
            return "n/a";

        if (ContainsAny(evidence, "VMware"))
        {
            if (ContainsAny(evidence, "PVSCSI", "Paravirtual"))
                return "VMware PVSCSI";
            return "VMware Storage";
        }

        if (ContainsAny(evidence, "Microsoft Corporation Virtual Machine", "Hyper-V", "Msft Virtual", "Virtual HD", "Microsoft Virtual"))
            return "Hyper-V Storage";

        if (ContainsAny(evidence, "QEMU", "KVM", "Red Hat", "VirtIO", "Virtio"))
            return "VirtIO Storage";

        if (ContainsAny(evidence, "Xen", "XenServer"))
            return "Xen Storage";

        if (ContainsAny(evidence, "VirtualBox", "Oracle VM"))
            return "VirtualBox Storage";

        if (ContainsAny(evidence, "Amazon EC2", "Amazon Elastic Block Store", "NVMe Amazon"))
            return "AWS EBS";

        if (ContainsAny(evidence, "Google Compute Engine", "Google PersistentDisk"))
            return "Google Persistent Disk";

        if (ContainsAny(evidence, "Virtual"))
            return "Virtual Storage";

        return "n/a";
    }

    private static bool ContainsAny(string value, params string[] needles)
        => needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string InventoryId(PhysicalDiskInventoryJson row)
        => FirstNonEmpty(row.DeviceNumber, row.DeviceId).Trim();

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string TrimForEvent(string value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        value = value.Trim();
        return value.Length <= max ? value : value[..max];
    }

}

internal sealed record PhysicalDiskInventoryJson(string? Source, string? DeviceId, string? DeviceNumber, string? FriendlyName, string? MediaType, string? BusType, ulong Size, string? Manufacturer, string? Model, string? FirmwareVersion, string? SerialNumber, string? ConnectedNode, string? ConnectedNodeDeviceNumber, string? Flags);

internal sealed record PhysicalDiskInventoryEntry(string PhysicalDiskId, string FriendlyName, string Type, string Size, string Manufacturer, string Model, string FirmwareVersion, string SerialNumber, string Mapping);
