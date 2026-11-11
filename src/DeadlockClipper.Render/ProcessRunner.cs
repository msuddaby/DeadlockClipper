using System.Diagnostics;
using System.Text;

namespace DeadlockClipper.Render;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>A long-running child process (e.g. a recorder) that is stopped with SIGINT so it can finalize output.</summary>
public interface IRunningProcess : IAsyncDisposable
{
    int Id { get; }
    bool HasExited { get; }
    string StdErrSoFar { get; }
    /// <summary>Sends SIGINT and waits for exit; kills the process if it doesn't exit within the timeout.</summary>
    Task<int> InterruptAsync(TimeSpan timeout);
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string file, IEnumerable<string> args, IReadOnlyDictionary<string, string>? env = null,
        TimeSpan? timeout = null, CancellationToken ct = default);

    IRunningProcess Start(string file, IEnumerable<string> args, IReadOnlyDictionary<string, string>? env = null);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string file, IEnumerable<string> args,
        IReadOnlyDictionary<string, string>? env = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var p = new Process { StartInfo = Info(file, args, env) };
        p.Start();
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) timeoutCts.CancelAfter(t);
        try
        {
            await p.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            return new ProcessResult(-1, await stdout, $"timed out after {timeout}: {await stderr}");
        }
        return new ProcessResult(p.ExitCode, await stdout, await stderr);
    }

    public IRunningProcess Start(string file, IEnumerable<string> args, IReadOnlyDictionary<string, string>? env = null)
    {
        var p = new Process { StartInfo = Info(file, args, env), EnableRaisingEvents = true };
        var stderr = new StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
        p.OutputDataReceived += (_, _) => { };
        p.Start();
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        return new RunningProcess(p, stderr);
    }

    private static ProcessStartInfo Info(string file, IEnumerable<string> args, IReadOnlyDictionary<string, string>? env)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
        return psi;
    }

    private sealed class RunningProcess(Process process, StringBuilder stderr) : IRunningProcess
    {
        public int Id => process.Id;
        public bool HasExited => process.HasExited;
        public string StdErrSoFar { get { lock (stderr) return stderr.ToString(); } }

        public async Task<int> InterruptAsync(TimeSpan timeout)
        {
            if (!process.HasExited)
            {
                // .NET has no SIGINT API; recorders need INT (not KILL) to write their container trailer.
                using var kill = Process.Start("kill", ["-INT", process.Id.ToString()]);
                await kill!.WaitForExitAsync();
            }
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            return process.ExitCode;
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync();
            }
            process.Dispose();
        }
    }
}
