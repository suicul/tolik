using TOLIK.Core;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Voice text policy (docs/voice-runtime-v2.md 0.11/0.12): markdown decorations never
/// reach the TTS, fenced code/JSON is dropped from speech, and the spoken-sentence
/// budget follows the deterministic response class (the model never chooses how
/// much of itself is voiced — §23).
/// </summary>
public sealed class VoiceTextPolicyTests
{
    [Theory]
    [InlineData("**Готово**", "Готово")]
    [InlineData("Сделал `dotnet build` успешно", "Сделал dotnet build успешно")]
    [InlineData("*важно*", "важно")]
    [InlineData("__жирно__", "жирно")]
    [InlineData("## Заголовок\nТекст", "Заголовок\nТекст")]
    [InlineData("- пункт один\n- пункт два", "пункт один\nпункт два")]
    [InlineData("1. первое\n2. второе", "первое\nвторое")]
    [InlineData("Смотри [документацию](https://example.com) тут", "Смотри документацию тут")]
    public void Markdown_decorations_never_reach_speech(string input, string expected)
    {
        Assert.Equal(expected, VoiceTextCleaner.CleanForSpeech(input));
    }

    [Fact]
    public void Fenced_code_blocks_are_dropped_from_speech_entirely()
    {
        var answer = "Вот решение:\n```csharp\nConsole.WriteLine(\"hi\");\n```\nГотово.";
        var speech = VoiceTextCleaner.CleanForSpeech(answer);
        Assert.DoesNotContain("Console.WriteLine", speech);
        Assert.DoesNotContain("```", speech);
        Assert.Contains("Вот решение", speech);
        Assert.Contains("Готово", speech);
    }

    [Fact]
    public void Fenced_json_is_not_spoken()
    {
        var answer = "Ответ:\n```json\n{\"status\": \"ok\"}\n```\nВсё в порядке.";
        var speech = VoiceTextCleaner.CleanForSpeech(answer);
        Assert.DoesNotContain("status", speech);
        Assert.Contains("Всё в порядке", speech);
    }

    [Fact]
    public void Unclosed_fence_also_dropped()
    {
        var speech = VoiceTextCleaner.CleanForSpeech("Начало:\n```\nvar x = 1;");
        Assert.DoesNotContain("var x", speech);
        Assert.Contains("Начало", speech);
    }

    [Fact]
    public void Empty_input_cleans_to_empty()
    {
        Assert.Equal(string.Empty, VoiceTextCleaner.CleanForSpeech(null));
        Assert.Equal(string.Empty, VoiceTextCleaner.CleanForSpeech("  "));
    }

    [Fact]
    public void Plain_prose_survives_untouched()
    {
        const string prose = "Привет! Всё работает, чем могу помочь?";
        Assert.Equal(prose, VoiceTextCleaner.CleanForSpeech(prose));
    }

    [Theory]
    [InlineData("Готово", VoiceResponseClass.Ack)] // 6 chars
    [InlineData("Сделал, всё собралось чисто", VoiceResponseClass.Short)] // 27 chars: just past Ack
    [InlineData("Привет! Я Толик, твой персональный ассистент на домашнем компьютере. Я слушаю тебя через микрофон и отвечаю голосом, без лишних слов.", VoiceResponseClass.Normal)] // ~140 chars: past Short
    public void Length_tiers_classify(string text, VoiceResponseClass expected)
    {
        Assert.Equal(expected, VoiceResponsePolicy.Classify(text));
    }

    [Fact]
    public void Detailed_boundary_is_exact_length()
    {
        Assert.Equal(VoiceResponseClass.Normal, VoiceResponsePolicy.Classify(new string('а', 400)));
        Assert.Equal(VoiceResponseClass.Detailed, VoiceResponsePolicy.Classify(new string('а', 401)));
    }

    [Fact]
    public void Any_code_fence_forces_detailed()
    {
        Assert.Equal(VoiceResponseClass.Detailed, VoiceResponsePolicy.Classify("Коротко:\n```cs\nx=1;\n```"));
    }

    [Fact]
    public void Spoken_budgets_are_bounded()
    {
        Assert.Equal(1, VoiceResponsePolicy.MaxSpokenSentences(VoiceResponseClass.Ack));
        Assert.Equal(2, VoiceResponsePolicy.MaxSpokenSentences(VoiceResponseClass.Short));
        Assert.Equal(3, VoiceResponsePolicy.MaxSpokenSentences(VoiceResponseClass.Normal));
        Assert.Equal(1, VoiceResponsePolicy.MaxSpokenSentences(VoiceResponseClass.Detailed));
    }

    [Fact]
    public void Empty_text_classifies_as_ack()
    {
        Assert.Equal(VoiceResponseClass.Ack, VoiceResponsePolicy.Classify(null));
        Assert.Equal(VoiceResponseClass.Ack, VoiceResponsePolicy.Classify("   "));
    }
}
