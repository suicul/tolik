namespace TOLIK.Core;

public sealed record Feedback(Guid UserId, Guid TaskId, bool Positive, string? Comment, DateTimeOffset CreatedAt);
public sealed record LearnedRule(Guid Id, Guid OwnerId, string Rule, double Confidence, string Source, DateTimeOffset CreatedAt);
public sealed class LearningService
{
    private readonly List<Feedback> feedback = [];
    private readonly List<LearnedRule> rules = [];
    public Feedback AddFeedback(Guid userId, Guid taskId, bool positive, string? comment)
    {
        if (userId == Guid.Empty || taskId == Guid.Empty) throw new ArgumentException("Invalid feedback.");
        var item = new Feedback(userId, taskId, positive, comment, DateTimeOffset.UtcNow); feedback.Add(item); return item;
    }
    public LearnedRule AddRule(Guid ownerId, string rule, double confidence, string source)
    {
        if (ownerId == Guid.Empty || string.IsNullOrWhiteSpace(rule) || confidence is < 0 or > 1 || string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Invalid rule.");
        var item = new LearnedRule(Guid.NewGuid(), ownerId, rule, confidence, source, DateTimeOffset.UtcNow); rules.Add(item); return item;
    }
    public IReadOnlyList<LearnedRule> GetRules(Guid ownerId) => rules.Where(x => x.OwnerId == ownerId).OrderByDescending(x => x.Confidence).ToArray();
}
