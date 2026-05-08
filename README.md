# hvtop

hvtop is a TUI prototype for monitoring Windows and Hyper-V hosts, VMs when
Hyper-V is present, failover clusters, CSV/storage, network, and recent events.
It is shaped like `htop` or `esxtop`, but with fast drill-down views and a small
rolling history buffer for max/spike visibility.

The native implementation is written in C# with native Windows counters and no
PowerShell in the hot path.

## Run Native

```powershell
cd .\hvtop.Native
dotnet run
```

Publish a single executable:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true
```

Useful options:

```powershell
dotnet run -- --refresh 0.5 --history 15
dotnet run -- --smoke
```

The native version currently uses PDH for host CPU, memory, disk throughput,
IOPS, queue depth, and latency. Network rates come from the native Windows IP
Helper API. VM rows are populated from Hyper-V inventory and counters when
Hyper-V is available; otherwise the VM pane is empty and the host/storage/network
panes remain useful on standard Windows servers.

## Keys

- `H`: Hosts
- `V`: VMs
- `D`: CSV/storage
- `N`: Network
- `E`: Events
- `Up/Down` or `k/j`: move selection
- `Enter`: drill down
- `Backspace` or `Esc`: back
- `q`: quit

## Drill Down

The intended navigation path is:

```text
HOSTS -> select host -> VMs on that host -> select VM -> VM detail
```

Detail panes resolve the selected row from the latest snapshot on every repaint,
so values continue updating live while you are drilled in.

## First Panels

- Hosts: hostname, CPU, memory, I/O, network, status
- VMs: name, CPU, memory, I/O, network, status
- CSV/storage: name, free space, I/O, IOPS, queue depth, latency, status
- Network: adapter, throughput, receive, transmit, drops, status
- Events: timestamped status, spike, and collector events

Each metric shows current and max-in-history values as `current | max`. The history
window defaults to 15 minutes.

Throughput values scale as `KB/s`, `MB/s`, then `GB/s` with compact three-digit
numbers, for example `999 KB/s`, `1.32 MB/s`, `11.3 MB/s`, `111 MB/s`,
`1.33 GB/s`, and `32.2 GB/s`.

## Native Collection Direction

The native collector should use:

- PDH/perflib counters for high-frequency metrics.
- Hyper-V WMI/CIM APIs for topology and inventory, sampled less frequently.
- Cluster APIs for CSV ownership and CSV-specific state.
- ETW later for event-rich timelines where counters are not enough.

Good next additions are:

- Per-VM Hyper-V counter mapping for network and virtual disk throughput.
- Cluster Shared Volume discovery without shelling out to PowerShell.
- Per-vDisk Level 3 drill-down from VM hard disk inventory and virtual storage
  counters
- Threshold configuration in a JSON file.
- CSV or JSON event export.
