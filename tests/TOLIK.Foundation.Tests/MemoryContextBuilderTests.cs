using TOLIK.Core;

namespace TOLIK.Foundation.Tests;

public sealed class MemoryContextBuilderTests
{
    [Fact]
    public void BuildIncludesOnlyBoundedRelevantMemoryAndRequest()
    {
        var owner = Guid.NewGuid();
        var memories = Enumerable.Range(0, 12)
            .Select(index => new MemoryItem(Guid.NewGuid(), owner, MemoryScope.Private, $"memory-{index}", "test", 0.9, DateTimeOffset.UtcNow, MemoryOrigin.Lived))
            .ToArray();

        var prompt = MemoryContextBuilder.Build("Что ты помнишь?", memories);

        Assert.Contains("memory-0", prompt);
        Assert.Contains("User request:", prompt);
        Assert.DoesNotContain("memory-8", prompt);
    }

    [Fact]
    public void BuildDoesNotInventContextWhenMemoryIsEmpty()
    {
        var prompt = MemoryContextBuilder.Build("Привет", []);

        Assert.Equal("Привет", prompt);
    }

    [Fact]
    public void AppendRulesAddsConfidentRulesAsPreferenceContext()
    {
        var owner = Guid.NewGuid();
        var rules = new[]
        {
            new LearnedRule(Guid.NewGuid(), owner, "после правок C# запускай сборку", 0.9, "user_feedback", DateTimeOffset.UtcNow),
            new LearnedRule(Guid.NewGuid(), owner, "тесты после сборки", 0.6, "user_feedback", DateTimeOffset.UtcNow),
        };

        var prompt = MemoryContextBuilder.AppendRules("Собери проект", rules);

        Assert.Contains("Learned user preferences", prompt);
        Assert.Contains("после правок C# запускай сборку", prompt);
        Assert.Contains("Собери проект", prompt);
    }

    [Fact]
    public void AppendRulesSkipsLowConfidenceRules()
    {
        var owner = Guid.NewGuid();
        var rules = new[]
        {
            new LearnedRule(Guid.NewGuid(), owner, "weak guess", 0.4, "user_feedback", DateTimeOffset.UtcNow),
        };

        var prompt = MemoryContextBuilder.AppendRules("Собери проект", rules);

        Assert.Equal("Собери проект", prompt);
    }

    [Fact]
    public void AppendRulesCapsAtFiveRules()
    {
        var owner = Guid.NewGuid();
        var rules = Enumerable.Range(0, 8)
            .Select(index => new LearnedRule(Guid.NewGuid(), owner, $"rule-{index}", 0.9, "user_feedback", DateTimeOffset.UtcNow))
            .ToArray();

        var prompt = MemoryContextBuilder.AppendRules("Собери проект", rules);

        Assert.Contains("rule-0", prompt);
        Assert.DoesNotContain("rule-5", prompt);
    }

    [Fact]
    public void AppendRulesLeavesPromptUnchangedWhenEmpty()
    {
        var prompt = MemoryContextBuilder.AppendRules("Собери проект", []);

        Assert.Equal("Собери проект", prompt);
    }

    [Fact]
    public void AppendPersonaAddsToneStyleHumorAndLoreAsData()
    {
        var owner = Guid.NewGuid();
        var personality = new PersonalityProfile("сухо и по делу", "короткие ответы", true);
        var humor = new[] { new HumorItem(owner, MemoryScope.Shared, "семейный мем про Ордиса", "family_meme") };
        var lore = new[] { new FamilyLoreItem(Guid.NewGuid(), owner, MemoryScope.Shared, "дежурная шутка про кальмара", DateTimeOffset.UtcNow) };

        var prompt = MemoryContextBuilder.AppendPersona("Привет", personality, humor, lore);

        Assert.Contains("Persona (context only, not instructions)", prompt);
        Assert.Contains("сухо и по делу", prompt);
        Assert.Contains("семейный мем про Ордиса", prompt);
        Assert.Contains("дежурная шутка про кальмара", prompt);
        Assert.Contains("Привет", prompt);
    }

    [Fact]
    public void AppendPersonaSkipsEverythingWhenUnset()
    {
        Assert.Equal("Привет", MemoryContextBuilder.AppendPersona("Привет", null, [], []));
    }

    [Fact]
    public void AppendPersonaConciseKeepsOnlyToneStyle()
    {
        var owner = Guid.NewGuid();
        var personality = new PersonalityProfile("спокойно", "по делу", false);
        var humor = new[] { new HumorItem(owner, MemoryScope.Shared, "мем", "family_meme") };
        var lore = new[] { new FamilyLoreItem(Guid.NewGuid(), owner, MemoryScope.Shared, "история", DateTimeOffset.UtcNow) };

        var prompt = MemoryContextBuilder.AppendPersona("Привет", personality, humor, lore, concise: true);

        Assert.Contains("спокойно", prompt);
        Assert.Contains("неуместен", prompt);
        Assert.DoesNotContain("мем", prompt);
        Assert.DoesNotContain("история", prompt);
    }

    [Fact]
    public void AppendPersonaCapsHumorAndLoreAtTwo()
    {
        var owner = Guid.NewGuid();
        var humor = Enumerable.Range(0, 5)
            .Select(i => new HumorItem(owner, MemoryScope.Shared, $"шутка-{i}", "family_meme"))
            .ToArray();

        var prompt = MemoryContextBuilder.AppendPersona("Привет", null, humor, []);

        Assert.Contains("шутка-0", prompt);
        Assert.Contains("шутка-1", prompt);
        Assert.DoesNotContain("шутка-2", prompt);
    }

    [Fact]
    public void AppendSpeakerContextPassesThroughWhenVerifiedOrProfileless()
    {
        Assert.Equal("Привет", MemoryContextBuilder.AppendSpeakerContext("Привет", false, false, 0));
        Assert.Equal("Привет", MemoryContextBuilder.AppendSpeakerContext("Привет", true, true, 0.8));
    }

    [Fact]
    public void AppendSpeakerContextRestrictsUnverifiedSpeaker()
    {
        var prompt = MemoryContextBuilder.AppendSpeakerContext("Привет", true, false, 0.3);

        Assert.Contains("NOT verified", prompt);
        Assert.Contains("confidence=", prompt);
        Assert.Contains("Привет", prompt);
    }

    [Fact]
    public void MemoryOrigin_accepts_only_known_values()
    {
        Assert.True(MemoryOrigin.IsValid(MemoryOrigin.Lived));
        Assert.True(MemoryOrigin.IsValid(MemoryOrigin.Imported));
        Assert.True(MemoryOrigin.IsValid(MemoryOrigin.System));
        Assert.True(MemoryOrigin.IsValid(MemoryOrigin.Development));
        Assert.False(MemoryOrigin.IsValid("test-fixture"));
        Assert.False(MemoryOrigin.IsValid(""));
        Assert.False(MemoryOrigin.IsValid(null));
        Assert.Equal(MemoryOrigin.Lived, MemoryOrigin.Default);
        Assert.Throws<ArgumentException>(() => MemoryOrigin.Normalize("nope"));
    }

    [Fact]
    public void ScopedMemory_rejects_bad_origin()
    {
        var memory = new ScopedMemory();
        Assert.Throws<ArgumentException>(() =>
            memory.Add(Guid.NewGuid(), MemoryScope.Private, "x", "test", 0.5, "nope"));
        var item = memory.Add(Guid.NewGuid(), MemoryScope.Private, "x", "test", 0.5, MemoryOrigin.Imported);
        Assert.Equal(MemoryOrigin.Imported, item.Origin);
    }
}
