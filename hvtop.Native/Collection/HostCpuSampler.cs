namespace hvtop.Native;

internal sealed class HostCpuSampler : IDisposable
{
    private readonly PdhQuery query = new();
    private readonly Counter? totalUtility;
    private readonly Counter? totalProcessorInformationTime;
    private readonly Counter? totalProcessorTime;
    private readonly Counter[] utilityCounters;
    private readonly Counter[] processorInformationTimeCounters;
    private readonly Counter[] processorTimeCounters;

    public string LastDiagnostic { get; private set; } = "source=n/a";

    public HostCpuSampler()
    {
        totalUtility = query.TryAdd(@"\Processor Information(_Total)\% Processor Utility");
        totalProcessorInformationTime = query.TryAdd(@"\Processor Information(_Total)\% Processor Time");
        totalProcessorTime = query.TryAdd(@"\Processor(_Total)\% Processor Time");
        utilityCounters = AddExpanded(@"\Processor Information(*)\% Processor Utility", IsLogicalProcessorInformationInstance);
        processorInformationTimeCounters = AddExpanded(@"\Processor Information(*)\% Processor Time", IsLogicalProcessorInformationInstance);
        processorTimeCounters = AddExpanded(@"\Processor(*)\% Processor Time", IsLogicalProcessorInstance);
        TryCollect();
    }

    public double Read()
    {
        TryCollect();

        var utilityAverage = Average(utilityCounters);
        var processorInformationTimeAverage = Average(processorInformationTimeCounters);
        var processorTimeAverage = Average(processorTimeCounters);
        var utilityTotal = totalUtility?.Read() ?? double.NaN;
        var processorInformationTotal = totalProcessorInformationTime?.Read() ?? double.NaN;
        var processorTotal = totalProcessorTime?.Read() ?? double.NaN;
        var source = "n/a";
        double result;
        if (IsUsableSample(processorInformationTimeAverage))
        {
            source = "avg-processor-information-time";
            result = ClampPercent(processorInformationTimeAverage);
        }
        else if (IsUsableSample(processorInformationTotal))
        {
            source = "total-processor-information-time";
            result = ClampPercent(processorInformationTotal);
        }
        else if (IsUsableSample(processorTimeAverage))
        {
            source = "avg-processor-time";
            result = ClampPercent(processorTimeAverage);
        }
        else if (IsUsableSample(processorTotal))
        {
            source = "total-processor-time";
            result = ClampPercent(processorTotal);
        }
        else if (IsUsableSample(utilityAverage))
        {
            source = "avg-utility";
            result = ClampPercent(utilityAverage);
        }
        else if (IsUsableSample(utilityTotal))
        {
            source = "total-utility";
            result = ClampPercent(utilityTotal);
        }
        else
        {
            result = double.NaN;
        }

        LastDiagnostic = $"source={source} chosen={FormatDiagnostic(result)} avgProcessorInformationTime={FormatDiagnostic(processorInformationTimeAverage)} avgProcessorTime={FormatDiagnostic(processorTimeAverage)} avgUtility={FormatDiagnostic(utilityAverage)} totalProcessorInformationTime={FormatDiagnostic(processorInformationTotal)} totalProcessorTime={FormatDiagnostic(processorTotal)} totalUtility={FormatDiagnostic(utilityTotal)} processorInformationInstances={processorInformationTimeCounters.Length} processorInstances={processorTimeCounters.Length} utilityInstances={utilityCounters.Length}";
        return result;
    }

    private Counter[] AddExpanded(string wildcardPath, Func<string, bool> includeInstance)
    {
        try
        {
            return Native.ExpandWildcardPath(wildcardPath)
                .Where(path => includeInstance(ExtractInstance(path)))
                .Select(path => query.TryAdd(path))
                .Where(counter => counter is not null)
                .Cast<Counter>()
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private void TryCollect()
    {
        try
        {
            query.Collect();
        }
        catch
        {
        }
    }

    private static double Average(Counter[] counters)
    {
        if (counters.Length == 0)
            return double.NaN;

        var values = counters.Select(counter => counter.Read())
            .Where(IsUsableSample)
            .ToArray();
        return values.Length == 0 ? double.NaN : values.Average();
    }

    private static bool IsLogicalProcessorInstance(string instance)
        => !string.IsNullOrWhiteSpace(instance)
           && !instance.Contains("_Total", StringComparison.OrdinalIgnoreCase);

    private static bool IsLogicalProcessorInformationInstance(string instance)
    {
        if (string.IsNullOrWhiteSpace(instance)
            || instance.Contains("_Total", StringComparison.OrdinalIgnoreCase))
            return false;

        return instance.Any(char.IsDigit);
    }

    private static string ExtractInstance(string counterPath)
    {
        var open = counterPath.IndexOf('(');
        if (open < 0)
            return string.Empty;
        var close = counterPath.IndexOf(')', open + 1);
        return close <= open + 1 ? string.Empty : counterPath.Substring(open + 1, close - open - 1);
    }

    private static bool IsUsableSample(double value)
        => !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;

    private static double ClampPercent(double value)
        => Math.Clamp(value, 0, 100);

    private static string FormatDiagnostic(double value)
        => double.IsNaN(value) || double.IsInfinity(value) ? "n/a" : $"{value:N1}%";

    public void Dispose()
        => query.Dispose();
}
