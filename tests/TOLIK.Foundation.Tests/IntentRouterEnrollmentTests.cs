using TOLIK.Core;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// EnrollTrustedPerson intent (docs/voice-identity.md §5, VI-4): the conversational
/// enrollment patterns are checked BEFORE StoreMemory in ClassifyPatterns (more specific
/// first, like семейный мем); «запомни голос» без name → Argument empty → the caller
/// asks; the single-word mode guards ("ночь"/"молчи"/"тишина") stay intact and the
/// StoreMemory path keeps working (regression). IntentRouterTests.cs stays untouched —
/// the VI-4 cases live here.
/// </summary>
public sealed class IntentRouterEnrollmentTests
{
    [Fact]
    public void Conversational_enrollment_with_relation_captures_the_name()
    {
        // «Толик, это моя жена Мария, запомни её голос» → EnrollTrustedPerson; the
        // Argument contains the spoken name (the caller parses/creates the user).
        var intent = IntentRouter.Classify("Толик, это моя жена Мария, запомни её голос");
        Assert.Equal(ChatIntentKind.EnrollTrustedPerson, intent.Kind);
        Assert.Contains("Мария", intent.Argument);
    }

    [Fact]
    public void Relation_form_without_marker_captures_the_name()
    {
        var intent = IntentRouter.Classify("это моя сестра Аня");
        Assert.Equal(ChatIntentKind.EnrollTrustedPerson, intent.Kind);
        Assert.Contains("Аня", intent.Argument);
    }

    [Fact]
    public void Bare_marker_after_a_spoken_subject_extracts_the_name()
    {
        // «это моя жена Мария, запомни голос» — the spoken subject precedes the bare
        // marker; the Argument carries the name (otherwise the caller would ask again).
        var intent = IntentRouter.Classify("это моя жена Мария, запомни голос");
        Assert.Equal(ChatIntentKind.EnrollTrustedPerson, intent.Kind);
        Assert.Contains("Мария", intent.Argument);
    }

    [Fact]
    public void Voice_marker_with_name_captures_the_name()
    {
        var intent = IntentRouter.Classify("запомни голос Марии");
        Assert.Equal(ChatIntentKind.EnrollTrustedPerson, intent.Kind);
        Assert.Equal("Марии", intent.Argument);
    }

    [Fact]
    public void Plan_example_bare_name_with_pronoun_extracts_the_name()
    {
        // plan §5: «Толик, это Мария, запомни её голос» — the spoken name precedes the
        // request clause; the Argument carries it.
        var intent = IntentRouter.Classify("Толик, это Мария, запомни её голос");
        Assert.Equal(ChatIntentKind.EnrollTrustedPerson, intent.Kind);
        Assert.Contains("Мария", intent.Argument);
    }

    [Theory]
    [InlineData("запомни её голос")]
    [InlineData("запомни его голос")]
    [InlineData("запомни голос")]
    public void Voice_marker_without_name_leaves_argument_empty(string message)
    {
        // "запомни голос" без name → Argument empty → the caller asks for the name.
        var intent = IntentRouter.Classify(message);
        Assert.Equal(ChatIntentKind.EnrollTrustedPerson, intent.Kind);
        Assert.Equal(string.Empty, intent.Argument);
    }

    [Fact]
    public void StoreMemory_still_works_regression()
    {
        // «запомни, что завтра встреча» — the StoreMemory path must keep working (the
        // EnrollTrustedPerson patterns are more specific and checked first).
        var intent = IntentRouter.Classify("запомни, что завтра встреча");
        Assert.Equal(ChatIntentKind.StoreMemory, intent.Kind);
        Assert.Equal("что завтра встреча", intent.Argument);
    }

    [Theory]
    [InlineData("ночь", "Night")]
    [InlineData("молчи", "Silent")]
    [InlineData("тишина", "Silent")]
    [InlineData("Толик, ночь", "Night")]
    public void Single_word_mode_guards_stay_intact(string message, string expectedMode)
    {
        var intent = IntentRouter.Classify(message);
        Assert.Equal(ChatIntentKind.SwitchVoiceMode, intent.Kind);
        Assert.Equal(expectedMode, intent.Argument);
    }

    [Fact]
    public void Bare_possessive_or_relation_stays_conversation()
    {
        // A possessive without a kinship term and a bare relation without a name are
        // speech, not enrollment → Conversation (no false-positive enrollment prompts).
        Assert.Equal(ChatIntentKind.Conversation, IntentRouter.Classify("это моя машина").Kind);
        Assert.Equal(ChatIntentKind.Conversation, IntentRouter.Classify("это моя мама").Kind);
        Assert.Equal(ChatIntentKind.Conversation, IntentRouter.Classify("это наша мама сидит рядом").Kind);
    }
}
