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
        output = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
        error = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;
        exitCode = process.ExitCode;
        timedOut = false;
        return process.ExitCode == 0;
    }
}

