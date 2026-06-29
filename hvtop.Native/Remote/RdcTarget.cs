namespace hvtop.Native;

internal sealed record RdcTarget(string Host, string? User, string? Password, string Source, int? LineNumber, int? Port = null, string? Token = null, bool? SkipDeploy = null)
{
    public bool HasCredentials => !string.IsNullOrWhiteSpace(User);
    public string CredentialMode => HasCredentials ? $"supplied credentials ({User})" : "current credentials";
}

internal sealed record RdcConfigResult(string? Path, RdcTarget[] Targets, RdcConfigSettings Settings, EventRow[] Events);

internal sealed record RdcConfigSettings(int? Port, double? RefreshSeconds, int? TimeoutSeconds, int? CopyTimeoutSeconds, string? Token, bool? SkipDeploy);

internal static class RdcConfigLoader
{
    private const char DefaultDelimiter = ':';

    public static RdcConfigResult Load(string? configuredPath)
    {
        var events = new List<EventRow>();
        var path = ResolvePath(configuredPath, events);
        if (path is null)
            return new RdcConfigResult(null, [], new RdcConfigSettings(null, null, null, null, null, null), events.ToArray());

        var targets = new List<RdcTarget>();
        int? port = null;
        double? refreshSeconds = null;
        int? timeoutSeconds = null;
        int? copyTimeoutSeconds = null;
        string? token = null;
        bool? skipDeploy = null;
        var delimiter = DefaultDelimiter;
        var delimiterSeen = false;
        var targetSeen = false;
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex)
        {
            events.Add(Event("WARN", $"RDC config '{path}' could not be read: {Trim(ex.Message, 120)}"));
            return new RdcConfigResult(path, [], new RdcConfigSettings(null, null, null, null, null, null), events.ToArray());
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            var line = lines[i].Trim();
            if (line.Length == 0
                || line.StartsWith("#", StringComparison.Ordinal)
                || line.StartsWith("//", StringComparison.Ordinal)
                || line.StartsWith(";", StringComparison.Ordinal))
                continue;

            if (line.Equals("SKIP-DEPLOY", StringComparison.OrdinalIgnoreCase)
                || line.Equals("RDC-SKIP-DEPLOY", StringComparison.OrdinalIgnoreCase))
            {
                skipDeploy = true;
                continue;
            }

            if (line.StartsWith("DELIMITER", StringComparison.OrdinalIgnoreCase))
            {
                if (targetSeen || delimiterSeen)
                {
                    events.Add(Event("WARN", $"RDC config line {lineNumber} skipped: delimiter must be declared before targets."));
                }
                else if (!TryParseDelimiter(line, out delimiter))
                {
                    events.Add(Event("WARN", $"RDC config line {lineNumber} skipped: invalid delimiter declaration."));
                }
                else
                {
                    delimiterSeen = true;
                }
                continue;
            }

            if (TryParseSetting(line, out var settingName, out var settingValue))
            {
                switch (settingName)
                {
                    case "PORT":
                        if (TryParseInt(settingValue, 1, 65535, out var parsedPort))
                            port = parsedPort;
                        else
                            events.Add(Event("WARN", $"RDC config line {lineNumber} skipped: invalid PORT value."));
                        break;
                    case "REFRESH":
                        if (double.TryParse(settingValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedRefresh) && parsedRefresh >= 1)
                            refreshSeconds = parsedRefresh;
                        else
                            events.Add(Event("WARN", $"RDC config line {lineNumber} skipped: invalid REFRESH value."));
                        break;
                    case "TIMEOUT":
                        if (TryParseInt(settingValue, 1, int.MaxValue, out var parsedTimeout))
                            timeoutSeconds = parsedTimeout;
                        else
                            events.Add(Event("WARN", $"RDC config line {lineNumber} skipped: invalid TIMEOUT value."));
                        break;
                    case "COPY-TIMEOUT":
                        if (TryParseInt(settingValue, 1, int.MaxValue, out var parsedCopyTimeout))
                            copyTimeoutSeconds = parsedCopyTimeout;
                        else
                            events.Add(Event("WARN", $"RDC config line {lineNumber} skipped: invalid COPY-TIMEOUT value."));
                        break;
                    case "TOKEN":
                    case "RDC-TOKEN":
                        token = settingValue;
                        break;
                    case "SKIP-DEPLOY":
                    case "RDC-SKIP-DEPLOY":
                        if (TryParseBool(settingValue, out var parsedSkipDeploy))
                            skipDeploy = parsedSkipDeploy;
                        else
                            events.Add(Event("WARN", $"RDC config line {lineNumber} skipped: invalid SKIP-DEPLOY value."));
                        break;
                }
                continue;
            }

            var fields = SplitEscaped(line, delimiter);
            if (fields.Length is < 1 or > 6)
            {
                events.Add(Event("WARN", $"RDC config line {lineNumber} skipped: expected host or host{delimiter}username{delimiter}password{delimiter}port{delimiter}token{delimiter}skip-deploy."));
                continue;
            }

            var host = fields[0].Trim();
            if (host.Length == 0)
            {
                events.Add(Event("WARN", $"RDC config line {lineNumber} skipped: missing host."));
                continue;
            }

            if (fields.Length == 1)
            {
                targetSeen = true;
                targets.Add(new RdcTarget(host, null, null, "config", lineNumber));
                continue;
            }

            var user = FieldOrNull(fields, 1)?.Trim();
            var password = FieldOrNull(fields, 2);
            if ((string.IsNullOrWhiteSpace(user)) != (string.IsNullOrWhiteSpace(password)))
            {
                events.Add(Event("WARN", $"RDC config line {lineNumber} skipped: username requires password."));
                continue;
            }

            int? targetPort = null;
            var portText = FieldOrNull(fields, 3);
            if (!string.IsNullOrWhiteSpace(portText))
            {
                if (TryParseInt(portText, 1, 65535, out var parsedTargetPort))
                    targetPort = parsedTargetPort;
                else
                {
                    events.Add(Event("WARN", $"RDC config line {lineNumber} skipped: invalid target port value."));
                    continue;
                }
            }

            var targetToken = FieldOrNull(fields, 4);
            bool? targetSkipDeploy = null;
            var skipDeployText = FieldOrNull(fields, 5);
            if (!string.IsNullOrWhiteSpace(skipDeployText))
            {
                if (TryParseBool(skipDeployText, out var parsedTargetSkipDeploy))
                    targetSkipDeploy = parsedTargetSkipDeploy;
                else
                {
                    events.Add(Event("WARN", $"RDC config line {lineNumber} skipped: invalid target SKIP-DEPLOY value."));
                    continue;
                }
            }

            targetSeen = true;
            targets.Add(new RdcTarget(host, NullIfEmpty(user), NullIfEmpty(password), "config", lineNumber, targetPort, NullIfEmpty(targetToken), targetSkipDeploy));
        }

        events.Add(Event("INFO", $"RDC config loaded: {path}, {targets.Count} target(s)"));
        if (targets.Any(t => t.HasCredentials) || !string.IsNullOrWhiteSpace(token) || targets.Any(t => !string.IsNullOrWhiteSpace(t.Token)))
            events.Add(Event("WARN", $"RDC config '{path}' contains clear-text credentials or tokens."));
        return new RdcConfigResult(path, targets.ToArray(), new RdcConfigSettings(port, refreshSeconds, timeoutSeconds, copyTimeoutSeconds, token, skipDeploy), events.ToArray());
    }

    private static string? ResolvePath(string? configuredPath, List<EventRow> events)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var path = Path.GetFullPath(configuredPath);
            if (!File.Exists(path))
                events.Add(Event("WARN", $"RDC config not found: {path}"));
            return File.Exists(path) ? path : null;
        }

        var defaultPath = Path.Combine(AppContext.BaseDirectory, "hvtop-rdc.conf");
        return File.Exists(defaultPath) ? defaultPath : null;
    }

    private static bool TryParseDelimiter(string line, out char delimiter)
    {
        delimiter = DefaultDelimiter;
        var text = line["DELIMITER".Length..].Trim();
        if (text.StartsWith("=", StringComparison.Ordinal))
            text = text[1..].Trim();
        else if (text.Length >= 2 && text[0] == ':')
            text = text[1..];

        if (text.Length == 0)
            return false;

        delimiter = text[0];
        return !char.IsWhiteSpace(delimiter) && delimiter != '"' && delimiter != '\\';
    }

    private static bool TryParseSetting(string line, out string name, out string value)
    {
        name = string.Empty;
        value = string.Empty;
        var separator = line.IndexOf(':');
        if (separator < 0)
            separator = line.IndexOf('=');
        if (separator <= 0)
            return false;

        var candidate = line[..separator].Trim().ToUpperInvariant();
        if (candidate is not ("PORT" or "REFRESH" or "TIMEOUT" or "COPY-TIMEOUT" or "TOKEN" or "RDC-TOKEN" or "SKIP-DEPLOY" or "RDC-SKIP-DEPLOY"))
            return false;

        name = candidate;
        value = line[(separator + 1)..].Trim();
        return value.Length > 0;
    }

    private static bool TryParseInt(string text, int min, int max, out int value)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            value = Math.Clamp(value, min, max);
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryParseBool(string text, out bool value)
    {
        if (bool.TryParse(text, out value))
            return true;

        if (text.Equals("1", StringComparison.OrdinalIgnoreCase)
            || text.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || text.Equals("on", StringComparison.OrdinalIgnoreCase)
            || text.Equals("skip-deploy", StringComparison.OrdinalIgnoreCase)
            || text.Equals("rdc-skip-deploy", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        if (text.Equals("0", StringComparison.OrdinalIgnoreCase)
            || text.Equals("no", StringComparison.OrdinalIgnoreCase)
            || text.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }

        value = false;
        return false;
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? FieldOrNull(string[] fields, int index)
        => index < fields.Length ? fields[index] : null;

    private static string[] SplitEscaped(string line, char delimiter)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];

            if (ch == '\\' && i + 1 < line.Length)
            {
                var next = line[i + 1];
                if (next == delimiter || next == '"' || next == '\\')
                {
                    current.Append(next);
                    i++;
                    continue;
                }
            }

            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (ch == delimiter && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        result.Add(current.ToString());
        return result.ToArray();
    }

    private static EventRow Event(string severity, string message)
        => new(DateTime.Now, severity, message);

    private static string Trim(string value, int max)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= max ? value : value[..max];
    }
}
