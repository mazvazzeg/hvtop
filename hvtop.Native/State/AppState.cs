namespace hvtop.Native;

internal sealed class AppState
{
    private readonly object gate = new();
    private Snapshot snapshot = Snapshot.Empty;
    private EventRow[] localEvents = [];
    private bool refreshRequested = true;
    private TimeSpan refresh;
    private string fatalError = string.Empty;

    public AppState(TimeSpan refresh)
    {
        this.refresh = refresh;
    }

    public TimeSpan Refresh
    {
        get
        {
            lock (gate)
                return refresh;
        }
    }

    public void Publish(Snapshot next)
    {
        lock (gate)
        {
            snapshot = next with { Events = MergeEvents(localEvents, next.Events) };
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
        RdcLog.Info($"{severity} {message}");
        lock (gate)
        {
            localEvents = localEvents.Prepend(new EventRow(DateTime.Now, severity, message)).Take(200).ToArray();
            snapshot = snapshot with
            {
                Events = MergeEvents(localEvents, snapshot.Events)
            };
        }
    }

    private static EventRow[] MergeEvents(EventRow[] local, EventRow[] published)
        => local
            .Concat(published)
            .GroupBy(evt => $"{evt.At:O}\0{evt.Severity}\0{evt.Message}", StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(evt => evt.At)
            .Take(200)
            .ToArray();

    public void SetRdcStatus(string status)
    {
        lock (gate)
        {
            snapshot = snapshot with { RdcStatus = status };
        }
    }

    public void SetFatalError(string message)
    {
        lock (gate)
            fatalError = message;
    }

    public string ReadFatalError()
    {
        lock (gate)
            return fatalError;
    }

    public void RequestRefresh()
    {
        lock (gate)
            refreshRequested = true;
    }

    public TimeSpan CycleRefresh(bool reverse = false)
    {
        var steps = new[] { 1.0, 2.0, 5.0, 10.0 };
        lock (gate)
        {
            var current = refresh.TotalSeconds;
            var index = Array.FindIndex(steps, value => Math.Abs(value - current) < 0.01);
            if (index < 0)
                index = steps.Select((value, i) => new { value, i }).OrderBy(item => Math.Abs(item.value - current)).First().i;

            index = reverse
                ? (index - 1 + steps.Length) % steps.Length
                : (index + 1) % steps.Length;
            refresh = TimeSpan.FromSeconds(steps[index]);
            return refresh;
        }
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

