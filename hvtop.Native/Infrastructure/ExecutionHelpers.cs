namespace hvtop.Native;

internal static class BoundedCall
{
    public static bool TryExecute<T>(Func<T> func, int timeoutMs, out T result)
    {
        try
        {
            var task = Task.Run(func);
            if (task.Wait(timeoutMs))
            {
                result = task.Result;
                return true;
            }
        }
        catch
        {
        }
        result = default!;
        return false;
    }
}

internal static class PowerShellRunner
{
    public static bool TryRun(string command, int timeoutMs, out string output)
        => TryRun(command, timeoutMs, out output, out _, out _, out _);

    public static bool TryRun(string command, int timeoutMs, out string output, out string error, out int exitCode, out bool timedOut)
    {
        using var process = new Process();
        process.StartInfo.FileName = "powershell.exe";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command));
        process.StartInfo.Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}";
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(true); } catch { }
            output = string.Empty;
            error = "timeout";
            exitCode = -1;
            timedOut = true;
            return false;
        }

        Task.WaitAll([stdoutTask, stderrTask], 500);
        output = stdoutTask.IsCompletedSuccessfully ? DecodePowerShellStream(stdoutTask.Result) : string.Empty;
        error = stderrTask.IsCompletedSuccessfully ? DecodePowerShellStream(stderrTask.Result) : string.Empty;
        exitCode = process.ExitCode;
        timedOut = false;
        return process.ExitCode == 0;
    }

    private static string DecodePowerShellStream(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var text = value.Trim();
        if (!text.StartsWith("#< CLIXML", StringComparison.OrdinalIgnoreCase))
            return text;

        var xmlStart = text.IndexOf("<Objs", StringComparison.OrdinalIgnoreCase);
        if (xmlStart < 0)
            return DecodePowerShellEscapes(text);

        var xml = text[xmlStart..];
        var messages = new List<string>();
        var searchFrom = 0;
        const string openMarker = "<S S=\"Error\">";
        const string closeMarker = "</S>";
        while (true)
        {
            var open = xml.IndexOf(openMarker, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (open < 0)
                break;

            open += openMarker.Length;
            var close = xml.IndexOf(closeMarker, open, StringComparison.OrdinalIgnoreCase);
            if (close < 0)
                break;

            var message = WebUtility.HtmlDecode(xml[open..close]);
            message = DecodePowerShellEscapes(message).Trim();
            if (!string.IsNullOrWhiteSpace(message))
                messages.Add(message);
            searchFrom = close + closeMarker.Length;
        }

        return messages.Count == 0 ? DecodePowerShellEscapes(xml) : string.Join(" ", messages);
    }

    private static string DecodePowerShellEscapes(string value)
        => value
            .Replace("_x000D__x000A_", " ")
            .Replace("_x000D_", " ")
            .Replace("_x000A_", " ")
            .Replace("_x0009_", " ")
            .ReplaceLineEndings(" ")
            .Trim();
}

