namespace TOLIK.Core;

public sealed record PlanStep(int Order, string Action);
public sealed record BoundedPlan(IReadOnlyList<PlanStep> Steps, DateTimeOffset Deadline);
public sealed class BoundedPlanner
{
    public BoundedPlan Create(string request, int maxSteps, TimeSpan budget, IReadOnlyList<string>? steps = null)
    {
        if (maxSteps is < 1 or > 32 || budget <= TimeSpan.Zero || budget > TimeSpan.FromHours(1)) throw new ArgumentException("Planner bounds are invalid.");
        if (steps is not null)
        {
            // Structured multi-step plan (§47, phase 1: deterministic steps, no LLM
            // decomposition yet). Every step is sanitized; unknown actions still fail
            // closed at execute time; per-step authorization applies unchanged.
            var actions = steps.Where(s => !string.IsNullOrWhiteSpace(s)).Select(SanitizeStep).Where(a => !string.IsNullOrWhiteSpace(a)).ToArray();
            if (actions.Length == 0 || actions.Length > maxSteps) throw new ArgumentException("Planner bounds are invalid.");
            return new BoundedPlan(actions.Select((action, i) => new PlanStep(i + 1, action)).ToArray(), DateTimeOffset.UtcNow.Add(budget));
        }
        if (string.IsNullOrWhiteSpace(request)) throw new ArgumentException("Planner bounds are invalid.");
        return new BoundedPlan([new PlanStep(1, SanitizeStep(request))], DateTimeOffset.UtcNow.Add(budget));
    }

    internal static string SanitizeStep(string action) =>
        action.Replace("ignore previous instructions", "", StringComparison.OrdinalIgnoreCase).Replace("system message:", "", StringComparison.OrdinalIgnoreCase).Trim();
}
public sealed class PlanExecutionGuard
{
    public async Task<IReadOnlyList<string>> ExecuteAsync(BoundedPlan plan, Func<PlanStep, CancellationToken, Task<string>> executor, CancellationToken cancellationToken = default)
    {
        if (plan is null || executor is null || plan.Steps.Count > 32) throw new ArgumentException("Invalid bounded plan.");
        var results = new List<string>();
        if (plan.Deadline <= DateTimeOffset.UtcNow) return results;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(plan.Deadline - DateTimeOffset.UtcNow);
        foreach (var step in plan.Steps.OrderBy(x => x.Order))
        {
            if (deadline.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(step.Action)) continue;
            results.Add(await executor(step, deadline.Token));
        }
        return results;
    }
}
