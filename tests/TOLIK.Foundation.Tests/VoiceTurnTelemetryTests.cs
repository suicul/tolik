using Microsoft.Extensions.Logging;
using TOLIK.Core;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Voice turn telemetry (docs/voice-runtime-v2.md M7): stage timings as structured
/// events. Only fixed stage literals and timings ever reach the logs — no transcripts,
/// responses or audio (SECURITY §38: no private text in logs).
/// </summary>
public sealed class VoiceTurnTelemetryTests
{
    private sealed class CaptureLogger : ILogger<VoiceTurnTelemetry>
    {
        public List<string> Messages = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public void Marks_log_stages_in_order_and_summary_contains_them()
    {
        var logger = new CaptureLogger();
        var telemetry = new VoiceTurnTelemetry(logger);

        telemetry.Mark("turn_start");
        telemetry.Mark("stt_done");
        telemetry.Mark("llm_first_delta");
        telemetry.Complete("done");

        Assert.Equal(4, logger.Messages.Count);
        Assert.Contains("turn_start", logger.Messages[0]);
        Assert.Contains("stt_done", logger.Messages[1]);
        Assert.Contains("llm_first_delta", logger.Messages[2]);
        // The summary carries the outcome and the full stage list with timings.
        Assert.Contains("outcome done", logger.Messages[3]);
        Assert.Contains("stages [turn_start@0,stt_done@", logger.Messages[3]);
    }

    [Fact]
    public void TurnId_is_unique_per_turn()
    {
        var logger = new CaptureLogger();
        var first = new VoiceTurnTelemetry(logger);
        var second = new VoiceTurnTelemetry(logger);
        Assert.NotEqual(first.TurnId, second.TurnId);
    }
}
