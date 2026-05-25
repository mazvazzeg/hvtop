namespace hvtop.Native;

internal sealed record RdcOptions(TimeSpan Refresh, TimeSpan History, int Port, string ListenPrefix, string Token, bool DebugLog, bool DebugCounters, bool ShowHelp, bool ShowVersion, string? ParseError)
{
    public static string HelpText =>
        """
        hvtop-rdc remote data collector

        Usage:
          hvtop-rdc.exe [options]

        Options:
          --port <n>            Listen TCP port. Default: 54321
          --listen <prefix>     HTTP listener prefix. Default: http://+:<port>/
          --refresh <seconds>   Collection interval. Default: 1
          --history <minutes>   History window. Default: 15
          --token <value>       Required token for incoming requests.
          --debug-log           Write hvtop-rdc.log beside the executable.
          --help                Show this help.
          --version             Show version and exit.
        """;

    public static RdcOptions Parse(string[] args)
    {
        var refresh = TimeSpan.FromSeconds(1);
        var history = TimeSpan.FromMinutes(15);
        var port = 54321;
        string? listen = null;
        var token = string.Empty;
        var debugLog = false;
        var debugCounters = false;
        var showHelp = false;
        var showVersion = false;
        string? error = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i].Trim();
            if (ArgumentHelper.IsHelp(arg))
            {
                showHelp = true;
                break;
            }
            if (ArgumentHelper.IsVersion(arg))
            {
                showVersion = true;
                break;
            }
            if (arg.Equals("--refresh", StringComparison.OrdinalIgnoreCase))
            {
                if (!ArgumentHelper.TryReadDouble(args, ref i, arg, out var value, out error)) break;
                refresh = TimeSpan.FromSeconds(Math.Max(1, value));
            }
            else if (arg.Equals("--history", StringComparison.OrdinalIgnoreCase))
            {
                if (!ArgumentHelper.TryReadDouble(args, ref i, arg, out var value, out error)) break;
                history = TimeSpan.FromMinutes(Math.Max(1, value));
            }
            else if (arg.Equals("--port", StringComparison.OrdinalIgnoreCase))
            {
                if (!ArgumentHelper.TryReadInt(args, ref i, arg, out var value, out error)) break;
                port = Math.Clamp(value, 1, 65535);
            }
            else if (arg.Equals("--listen", StringComparison.OrdinalIgnoreCase))
            {
                if (!ArgumentHelper.TryReadString(args, ref i, arg, out var value, out error)) break;
                listen = value;
            }
            else if (arg.Equals("--token", StringComparison.OrdinalIgnoreCase))
            {
                if (!ArgumentHelper.TryReadString(args, ref i, arg, out var value, out error)) break;
                token = value;
            }
            else if (arg.Equals("--debug-log", StringComparison.OrdinalIgnoreCase))
                debugLog = true;
            else if (arg.Equals("--debug-counters", StringComparison.OrdinalIgnoreCase))
                debugCounters = true;
            else
            {
                error = $"Unknown option: {arg}";
                break;
            }
        }

        listen ??= $"http://+:{port}/";
        if (!listen.EndsWith("/", StringComparison.Ordinal))
            listen += "/";

        return new RdcOptions(refresh, history, port, listen, token, debugLog, debugCounters, showHelp, showVersion, error);
    }
}

internal sealed record Options(TimeSpan Refresh, TimeSpan History, bool Smoke, bool LocalCollectors, bool RemoteCollectors, int RdcPort, TimeSpan RemoteRefresh, string? RdcHost, string? RdcUser, string? RdcPassword, string? RdcToken, bool DebugLog, bool DebugCounters, bool ShowHelp, bool ShowVersion, string? ParseError)
{
    public static string HelpText =>
        """
        hvtop - Windows / Hyper-V / Failover Cluster TUI monitor

        Usage:
          hvtop.exe [options]

        Options:
          --refresh <seconds>        Local UI/data refresh interval. Default: 1, minimum: 1
          --history <minutes>        History window for max/min values. Default: 15
          --rdc-port <n>             Remote Data Collector TCP port. Default: 54321
          --rdc-refresh <seconds>    Remote Data Collector interval. Default: 1
          --rdc-host <host>          Deploy/poll hvtop-rdc on an explicit remote host.
          --rdc-user <user>          Username for remote ADMIN$/CIM access.
          --rdc-password <password>  Password for remote ADMIN$/CIM access.
          --rdc-token <value>        Token passed to hvtop-rdc. Default: generated per run.
          --rdc-disable              Disable remote data collection.
          --local-disable            Disable local data collection; requires --rdc-host.
          --debug-log                Write hvtop.log; also enables remote hvtop-rdc.log.
          --smoke                    Print one sample and exit.
          --help                     Show this help.
          --version                  Show version and exit.
        """;

    public static Options Parse(string[] args)
    {
        var refresh = TimeSpan.FromSeconds(1);
        var history = TimeSpan.FromMinutes(15);
        var smoke = false;
        var localCollectors = true;
        var remoteCollectors = true;
        var rdcPort = 54321;
        var remoteRefresh = TimeSpan.FromSeconds(1);
        string? rdcHost = null;
        string? rdcUser = null;
        string? rdcPassword = null;
        string? rdcToken = null;
        var debugLog = false;
        var debugCounters = false;
        var showHelp = false;
        var showVersion = false;
        string? error = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i].Trim();
            if (ArgumentHelper.IsHelp(arg))
            {
                showHelp = true;
                break;
            }
            if (ArgumentHelper.IsVersion(arg))
            {
                showVersion = true;
                break;
            }
            if (arg.Equals("--refresh", StringComparison.OrdinalIgnoreCase))
            {
                if (!ArgumentHelper.TryReadDouble(args, ref i, arg, out var value, out error)) break;
                refresh = TimeSpan.FromSeconds(Math.Max(1, value));
            }
            else if (arg.Equals("--history", StringComparison.OrdinalIgnoreCase))
            {
                if (!ArgumentHelper.TryReadDouble(args, ref i, arg, out var value, out error)) break;
                history = TimeSpan.FromMinutes(Math.Max(1, value));
            }
            else if (arg.Equals("--smoke", StringComparison.OrdinalIgnoreCase))
                smoke = true;
            else if (arg.Equals("--local-disable", StringComparison.OrdinalIgnoreCase))
                localCollectors = false;
            else if (arg.Equals("--rdc-disable", StringComparison.OrdinalIgnoreCase))
                remoteCollectors = false;
            else if (arg.Equals("--rdc-port", StringComparison.OrdinalIgnoreCase))
            {
                if (!ArgumentHelper.TryReadInt(args, ref i, arg, out var value, out error)) break;
                rdcPort = Math.Clamp(value, 1, 65535);
            }
            else if (arg.Equals("--rdc-refresh", StringComparison.OrdinalIgnoreCase))
            {
                if (!ArgumentHelper.TryReadDouble(args, ref i, arg, out var value, out error)) break;
                remoteRefresh = TimeSpan.FromSeconds(Math.Max(1, value));
            }
            else if (arg.Equals("--rdc-host", StringComparison.OrdinalIgnoreCase))
            {
                if (!ArgumentHelper.TryReadString(args, ref i, arg, out var value, out error)) break;
                rdcHost = value;
            }
            else if (arg.Equals("--rdc-user", StringComparison.OrdinalIgnoreCase))
            {
                if (!ArgumentHelper.TryReadString(args, ref i, arg, out var value, out error)) break;
                rdcUser = value;
            }
            else if (arg.Equals("--rdc-password", StringComparison.OrdinalIgnoreCase))
            {
                if (!ArgumentHelper.TryReadString(args, ref i, arg, out var value, out error)) break;
                rdcPassword = value;
            }
            else if (arg.Equals("--rdc-token", StringComparison.OrdinalIgnoreCase))
            {
                if (!ArgumentHelper.TryReadString(args, ref i, arg, out var value, out error)) break;
                rdcToken = value;
            }
            else if (arg.Equals("--debug-log", StringComparison.OrdinalIgnoreCase))
                debugLog = true;
            else if (arg.Equals("--debug-counters", StringComparison.OrdinalIgnoreCase))
                debugCounters = true;
            else
            {
                error = $"Unknown option: {arg}";
                break;
            }
        }

        if (smoke)
            remoteCollectors = false;

        if (error is null && !localCollectors && string.IsNullOrWhiteSpace(rdcHost))
            error = "--local-disable requires --rdc-host";
        if (error is null && !localCollectors && !remoteCollectors)
            error = "--local-disable cannot be combined with --rdc-disable";
        if (error is null && !string.IsNullOrWhiteSpace(rdcPassword) && string.IsNullOrWhiteSpace(rdcUser))
            error = "--rdc-password requires --rdc-user";
        if (error is null && !string.IsNullOrWhiteSpace(rdcHost) && !remoteCollectors)
            error = "--rdc-host cannot be combined with --rdc-disable";

        return new Options(refresh, history, smoke, localCollectors, remoteCollectors, rdcPort, remoteRefresh, rdcHost, rdcUser, rdcPassword, rdcToken, debugLog, debugCounters, showHelp, showVersion, error);
    }
}

internal static class ArgumentHelper
{
    public static bool IsHelp(string arg)
        => arg.Equals("--help", StringComparison.OrdinalIgnoreCase);

    public static bool IsVersion(string arg)
        => arg.Equals("--version", StringComparison.OrdinalIgnoreCase);

    public static bool TryReadString(string[] args, ref int index, string option, out string value, out string? error)
    {
        value = string.Empty;
        error = null;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal))
        {
            error = $"Missing value for {option}";
            return false;
        }

        value = args[++index];
        return true;
    }

    public static bool TryReadInt(string[] args, ref int index, string option, out int value, out string? error)
    {
        value = 0;
        if (!TryReadString(args, ref index, option, out var text, out error))
            return false;

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return true;

        error = $"Invalid integer for {option}: {text}";
        return false;
    }

    public static bool TryReadDouble(string[] args, ref int index, string option, out double value, out string? error)
    {
        value = 0;
        if (!TryReadString(args, ref index, option, out var text, out error))
            return false;

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            return true;

        error = $"Invalid number for {option}: {text}";
        return false;
    }
}

