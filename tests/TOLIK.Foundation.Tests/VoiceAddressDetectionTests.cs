using TOLIK.Contracts;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Address detection + attention arbitration (docs/identity-presence.md §6, IP-6):
/// HEARING EVERYTHING IS NOT RESPONDING TO EVERYTHING — only directed speech proceeds;
/// arbitration is NOT last-speaker-wins; both are routing only, never authorization.
/// </summary>
public sealed class VoiceAddressDetectionTests
{
    private static SpeechEvent Speech(string device, string? speaker, double conf, bool overlap = false, DateTimeOffset? at = null) =>
        new(device, speaker, conf, AddressedToTolik: false, null, overlap, at ?? DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("Толик, открой браузер", true)]
    [InlineData("толик а что это", true)]
    [InlineData("Маша, идём обедать", false)]
    [InlineData("по-моему это бред", false)] // contains "тол"?? no: "по-моему это бред" — no wake word
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Wake_word_is_the_primary_address_signal(string? text, bool expected)
    {
        Assert.Equal(expected, VoiceAddressDetector.IsAddressed(text));
    }

    [Fact]
    public void Voice_turn_audio_is_addressed_by_construction()
    {
        // The voice loop's audio: the user spoke to TOLIK (button/wake) — addressed
        // regardless of what the whisper heard.
        Assert.True(VoiceAddressDetector.IsAddressed("какая-то ерунда", fromVoiceTurn: true));
    }

    [Fact]
    public void Classify_direct_overlapping_and_not_addressed()
    {
        var direct = Speech("pc", "Ivan", 0.9) with { AddressedToTolik = true };
        Assert.Equal(SpeechAddressCategory.Direct, VoiceAddressDetector.Classify(direct, "Толик, привет"));

        var overlap = Speech("phone", "Ivan", 0.8, overlap: true) with { AddressedToTolik = true };
        Assert.Equal(SpeechAddressCategory.Overlapping, VoiceAddressDetector.Classify(overlap, "Толик, привет"));

        var ambient = Speech("laptop", null, 0.2); // Мария говорит с Машей — не addressed
        Assert.Equal(SpeechAddressCategory.NotAddressed, VoiceAddressDetector.Classify(ambient, "Маша, идём обедать"));
    }

    [Fact]
    public void Arbitration_is_not_last_speaker_wins()
    {
        var now = DateTimeOffset.UtcNow;
        // MARIA spoke LAST (later timestamp), but IVAN explicitly addressed TOLIK
        // with high confidence — Ivan's event must win (explicit address > recency).
        var ivan = Speech("pc", "Ivan", 0.97, at: now) with { AddressedToTolik = true };
        var maria = Speech("laptop", "Maria", 0.90, at: now.AddSeconds(2));
        var selected = AttentionArbiter.Select([ivan, maria], currentConversationDevice: "pc");
        Assert.Equal("pc", selected!.SourceDevice);
    }

    [Fact]
    public void Arbitration_prefers_higher_confidence_and_continuity()
    {
        var now = DateTimeOffset.UtcNow;
        var weak = Speech("pc", "Unknown", 0.20, at: now) with { AddressedToTolik = true };
        var strong = Speech("laptop", "Ivan", 0.97, at: now) with { AddressedToTolik = true };
        Assert.Equal("laptop", AttentionArbiter.Select([weak, strong])!.SourceDevice);

        // Equal everything: the current conversation device wins (continuity).
        var a = Speech("pc", "Ivan", 0.80, at: now) with { AddressedToTolik = true };
        var b = Speech("quest", "Ivan", 0.80, at: now) with { AddressedToTolik = true };
        Assert.Equal("pc", AttentionArbiter.Select([a, b], currentConversationDevice: "pc")!.SourceDevice);
    }

    [Fact]
    public void Arbitration_empty_candidates_returns_null()
    {
        Assert.Null(AttentionArbiter.Select([]));
        Assert.Null(AttentionArbiter.Select(null!));
    }

    [Fact]
    public void Speech_event_is_probabilistic_and_carries_no_content_by_shape()
    {
        var @event = SpeechEvent.Unknown("phone", DateTimeOffset.UtcNow);
        Assert.Equal(0, @event.SpeakerConfidence);
        Assert.Null(@event.SpeakerCandidate);
        Assert.False(@event.AddressedToTolik);
        // The type carries perception CONTEXT (device/confidence/overlap) — no
        // transcripts, no raw audio, no authorization fields.
    }
}
