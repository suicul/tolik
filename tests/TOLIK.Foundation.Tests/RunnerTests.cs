using TOLIK.Infrastructure.Runners;

namespace TOLIK.Foundation.Tests;

public sealed class RunnerTests
{
    [Fact]
    public async Task Runner_captures_exit_and_output()
    {
        var runner = new CodexRunner("cmd.exe");
        var result = await runner.RunAsync(["/c", "echo runner-ok"], TimeSpan.FromSeconds(2));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("runner-ok", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task Missing_runner_is_explicit_failure()
    {
        var runner = new OpenCodeRunner("tolik-command-that-does-not-exist.exe");
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync([], TimeSpan.FromSeconds(1)));
    }
}
