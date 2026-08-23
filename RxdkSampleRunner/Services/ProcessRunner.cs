using System.Diagnostics;

namespace RxdkSampleRunner.Services;

/// <summary>
/// Runs an external process, streaming stdout+stderr lines to a callback as they arrive.
/// Used for msbuild, the rxdk CLI, and xemu — the last of which streams the title's serial
/// console output (via "-serial stdio") for the duration of the emulator session.
/// </summary>
public static class ProcessRunner
{
    /// <summary>Exit code returned when the caller cancelled and we killed the process.
    /// Distinct from a real non-zero exit so callers can treat "user stopped" as benign.</summary>
    public const int Cancelled = -1073741510; // 0xC000013A (STATUS_CONTROL_C_EXIT)

    public static async Task<int> RunAsync(
        string exe,
        IEnumerable<string> args,
        string? workingDirectory,
        Action<string> onLine,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = workingDirectory ?? "",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        onLine($"$ {exe} {string.Join(' ', psi.ArgumentList.Select(Quote))}");

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            onLine($"ERROR: cannot start '{exe}': {ex.Message}");
            return -1;
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            // WaitForExitAsync returns on process exit, but the async stdout/stderr readers
            // can still be delivering the tail of the output. Drain them (off the UI thread)
            // so callers like "Build All" don't see the log keep scrolling after it's done.
            try { await Task.Run(proc.WaitForExit).ConfigureAwait(false); } catch { /* gone */ }
        }
        catch (OperationCanceledException)
        {
            KillTree(proc);
            // Bounded reap so a host tool that lingers (xbox-launch streaming the kit) can't
            // make us wait forever.
            try
            {
                using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await proc.WaitForExitAsync(reap.Token).ConfigureAwait(false);
            }
            catch { /* timed out or already gone — it's killed, move on */ }
            return Cancelled;
        }
        return SafeExitCode(proc);
    }

    /// <summary>
    /// Kill the process and its whole child tree (the rxdk CLI spawns xbcp / xbox-launch).
    /// We shell out to <c>taskkill /F /T</c> rather than <see cref="Process.Kill(bool)"/> with
    /// entireProcessTree: the latter enumerates EVERY process on the machine to find
    /// descendants and throws a first-chance Win32Exception for each protected/system process
    /// it can't open — hundreds per call. Under the debugger that reads as an endless storm of
    /// exceptions on every Cancel. taskkill offloads the tree walk to the OS and raises none.
    /// </summary>
    private static void KillTree(Process proc)
    {
        int pid;
        try { if (proc.HasExited) return; pid = proc.Id; }
        catch { return; }

        try
        {
            using var tk = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "/F", "/T", "/PID", pid.ToString() },
            });
            tk?.WaitForExit(3000);
        }
        catch
        {
            // taskkill unavailable (non-Windows) or the process already gone — fall back to a
            // plain single-process kill (no tree walk, so still no exception storm).
            try { if (!proc.HasExited) proc.Kill(); } catch { /* already exiting */ }
        }
    }

    private static int SafeExitCode(Process proc)
    {
        try { return proc.ExitCode; }
        catch { return Cancelled; }
    }

    private static string Quote(string a) => a.Contains(' ') ? $"\"{a}\"" : a;
}
