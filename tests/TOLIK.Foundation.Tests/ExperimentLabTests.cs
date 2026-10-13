using TOLIK.Core;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Experiment Lab + Optimization Budgets (docs/self-evolution.md §2.3/§2.5 — PLAN
/// REFORM PRG-7): the deterministic self-optimization — N candidates → benchmark →
/// compare against the baseline → accept/reject → memory (a rejected optimization
/// is NOT repeated); the resource budgets configurable via TOLIK_OPT_* (fail-closed).
/// The lab never executes anything — it produces the decisions; the execution is
/// the consumer's job, gated by the authorization pipeline.
/// </summary>
public sealed class ExperimentLabTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static ExperimentCandidate Candidate(string desc, double score) =>
        new($"cand-{Guid.NewGuid():N}", "reduce TTS latency", desc, score, T0);

    [Fact]
    public void The_best_candidate_above_baseline_is_accepted()
    {
        var lab = new ExperimentLab();
        var candidates = new[] { Candidate("approach A", 0.6), Candidate("approach B", 0.85), Candidate("approach C", 0.7) };
        var record = lab.RunExperiment("exp-1", "reduce TTS latency", candidates, baselineThreshold: 0.6, T0);
        Assert.Equal(ExperimentStatus.Accepted, record.Status);
        Assert.Contains("approach B", record.Reason); // the best wins
        Assert.True(record.BestScore >= 0.6);
    }

    [Fact]
    public void Below_baseline_is_rejected_with_the_honest_reason()
    {
        var lab = new ExperimentLab();
        var candidates = new[] { Candidate("slower", 0.3), Candidate("same", 0.4) };
        var record = lab.RunExperiment("exp-2", "reduce TTS latency", candidates, baselineThreshold: 0.6, T0);
        Assert.Equal(ExperimentStatus.Rejected, record.Status);
        Assert.Contains("baseline", record.Reason); // the honest rejection reason
    }

    [Fact]
    public void Empty_candidates_reject_honestly()
    {
        var lab = new ExperimentLab();
        var record = lab.RunExperiment("exp-3", "hypothesis", [], 0.6, T0);
        Assert.Equal(ExperimentStatus.Rejected, record.Status);
        Assert.Contains("no candidates", record.Reason);
    }

    [Fact]
    public void The_history_is_bounded_and_records_outcomes()
    {
        var lab = new ExperimentLab();
        for (var i = 0; i < ExperimentLab.MaxHistory + 20; i++)
            lab.RunExperiment($"exp-{i}", "hypothesis", [Candidate("a", 0.5)], 0.6, T0.AddSeconds(i));
        Assert.Equal(ExperimentLab.MaxHistory, lab.History().Count); // bounded
    }

    [Fact]
    public void NaN_score_is_rejected_not_accepted()
    {
        var lab = new ExperimentLab();
        var candidates = new[] { Candidate("nan-score", double.NaN) };
        var record = lab.RunExperiment("exp-nan", "hypothesis", candidates, 0.6, T0);
        Assert.Equal(ExperimentStatus.Rejected, record.Status); // NaN never accepted (§18: no garbage)
    }
}

public sealed class OptimizationBudgetTests
{
    [Fact]
    public void Defaults_are_sane_and_fail_closed()
    {
        var budget = new OptimizationBudget();
        Assert.InRange(budget.MaxCpuPercent, 1.0, 100.0);
        Assert.InRange(budget.MaxRamMb, 64.0, 8192.0);
        Assert.InRange(budget.MaxGpuPercent, 0.0, 50.0);
        Assert.True(budget.PreferIdle);
    }

    [Fact]
    public void Invalid_env_values_fail_closed_to_defaults()
    {
        var old = Environment.GetEnvironmentVariable("TOLIK_OPT_CPU_PERCENT");
        try
        {
            Environment.SetEnvironmentVariable("TOLIK_OPT_CPU_PERCENT", "nonsense");
            var budget = new OptimizationBudget();
            Assert.Equal(25.0, budget.MaxCpuPercent); // the default
            Environment.SetEnvironmentVariable("TOLIK_OPT_CPU_PERCENT", "999");
            var budget2 = new OptimizationBudget();
            Assert.Equal(25.0, budget2.MaxCpuPercent); // out of range → the default
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOLIK_OPT_CPU_PERCENT", old);
        }
    }

    [Fact]
    public void Valid_env_values_are_honored()
    {
        var old = Environment.GetEnvironmentVariable("TOLIK_OPT_CPU_PERCENT");
        try
        {
            Environment.SetEnvironmentVariable("TOLIK_OPT_CPU_PERCENT", "15");
            var budget = new OptimizationBudget();
            Assert.Equal(15.0, budget.MaxCpuPercent);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOLIK_OPT_CPU_PERCENT", old);
        }
    }
}
