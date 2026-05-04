# hvtop Native Architecture

The PowerShell version is useful for proving the interaction model, but it should
not be the long-term collector. hvtop needs a native sampler that can collect
once per second without blocking input or repainting.

## Process Model

hvtop.Native uses three separate concerns:

- Collector loop: samples Windows counters and inventories on a fixed interval.
- History engine: stores rolling samples and calculates current/max values.
- TUI loop: reads the latest immutable snapshot and redraws independently.

This keeps slow collection from freezing keyboard input.

## Current Native Collectors

- Host CPU: PDH `\Processor(_Total)\% Processor Time`
- Host memory: PDH `\Memory\% Committed Bytes In Use`
- Host storage throughput: PDH `\PhysicalDisk(_Total)\Disk Bytes/sec`
- Host IOPS: PDH `\PhysicalDisk(_Total)\Disk Transfers/sec`
- Host queue depth: PDH `\PhysicalDisk(_Total)\Current Disk Queue Length`
- Host latency: PDH `\PhysicalDisk(_Total)\Avg. Disk sec/Transfer`
- Network throughput: `System.Net.NetworkInformation.NetworkInterface`
- Capacity/free space: `System.IO.DriveInfo`

No PowerShell and no NuGet packages are used in the hot path.

## Recommended Hyper-V Data Path

Use this order for production collectors:

1. PDH/perflib counters for high-frequency numeric metrics.
2. Hyper-V WMI/CIM APIs for topology and inventory, sampled less frequently.
3. Cluster APIs for CSV ownership and CSV-specific state.
4. ETW only for event-rich timelines or when PDH lacks enough detail.

Avoid calling PowerShell cmdlets from the sampler. Cmdlets are fine for one-time
diagnostics, but not for a TUI refresh loop.

## Hyper-V Counter Families To Add Next

- `Hyper-V Hypervisor Logical Processor(*)`
- `Hyper-V Hypervisor Virtual Processor(*)`
- `Hyper-V Dynamic Memory VM(*)`
- `Hyper-V Virtual Storage Device(*)`
- `Hyper-V Virtual Network Adapter(*)`

The tricky part is mapping counter instances back to friendly VM names and vDisk
paths. That should be done by a slower topology collector, then joined to PDH
samples in memory.
