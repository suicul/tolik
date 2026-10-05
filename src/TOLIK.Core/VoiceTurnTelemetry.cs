using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace TOLIK.Core;

/// <summary>
/// Voice turn stage telemetry (voice-runtime-v2.md M7): per-turn stage timings logged
/// as structured events — turn id, stage, elapsed ms, outcome. NO private text ever
/// passes through: transcripts, responses and audio are never logged (SECURITY §38) —
/// timings and stage names only. Created per turn (transient); Mark() as each stage
/// completes; Complete() logs the summary. Cold/warm TTFA measurements come from these
/// logs (the first turn after a restart = cold).
/// </summary>
public sealed class VoiceTurnTelemetry
{
    private readonly ILogger _logger;
    private readonly Guid _turnId = Guid.NewGuid();
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private readonly List<(string Stage, long ElapsedMs)> _stages = [];
    private readonly object _lock = new();

    public VoiceTurnTelemetry(ILogger<VoiceTurnTelemetry> logger) => _logger = logger;

    public Guid TurnId => _turnId;

    /// <summary>Marks a stage completion. Stage names are fixed literals, never user content.</summary>
    public void Mark(string stage)
    {
        var elapsed = ElapsedMs();
        lock (_lock)
        {
            _stages.Add((stage, elapsed));
        }
        _logger.LogInformation("VoiceTurn {TurnId} stage {Stage} at {ElapsedMs}ms", _turnId, stage, elapsed);
    }

    /// <summary>
    /// Outcome summary: one structured line per turn — stages, total, outcome
    /// (done/cancelled/error — fixed literals, no private text).
    /// </summary>
    public void Complete(string outcome)
    {
        var elapsed = ElapsedMs();
        string stages;
        lock (_lock)
        {
            stages = string.Join(",", _stages.Select(s => $"{s.Stage}@{s.ElapsedMs}"));
        }
        _logger.LogInformation("VoiceTurn {TurnId} outcome {Outcome} total {ElapsedMs}ms stages [{Stages}]",
            _turnId, outcome, elapsed, stages);
    }

    private long ElapsedMs() => (Stopwatch.GetTimestamp() - _startTimestamp) * 1000 / Stopwatch.Frequency;
}
