namespace hvtop.Native;

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

