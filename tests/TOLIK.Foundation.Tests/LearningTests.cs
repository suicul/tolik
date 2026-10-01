using TOLIK.Core;

namespace TOLIK.Foundation.Tests;

public sealed class LearningTests
{
    [Fact]
    public void Feedback_and_rules_are_scoped_to_owner()
    {
        var service = new LearningService(); var owner = Guid.NewGuid();
        service.AddFeedback(owner, Guid.NewGuid(), true, "works");
        service.AddRule(owner, "run tests after C# changes", .9, "feedback");
        service.AddRule(Guid.NewGuid(), "private", .9, "feedback");
        Assert.Single(service.GetRules(owner));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Invalid_rule_confidence_is_rejected(double confidence) => Assert.Throws<ArgumentException>(() => new LearningService().AddRule(Guid.NewGuid(), "rule", confidence, "feedback"));
}
