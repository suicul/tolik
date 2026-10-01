using System.Diagnostics;

namespace TOLIK.Infrastructure.Runners;

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);
public abstract class ToolRunner(string executable)
{
    public async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo { FileName = executable, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try { if (!process.Start()) throw new InvalidOperationException("Runner could not start."); }
        catch (Exception ex) { throw new InvalidOperationException($"Runner unavailable: {ex.Message}", ex); }
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeoutCts.CancelAfter(timeout);
        try { await process.WaitForExitAsync(timeoutCts.Token); return new ProcessResult(process.ExitCode, await output, await error, false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { try { process.Kill(true); } catch { } return new ProcessResult(-1, await output, await error, true); }
    }
}
public sealed class CodexRunner(string executable = "codex") : ToolRunner(executable);
public sealed class OpenCodeRunner(string executable = "opencode") : ToolRunner(executable);
