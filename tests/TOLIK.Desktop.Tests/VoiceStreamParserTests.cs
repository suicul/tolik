using TOLIK.Desktop.Voice;
using Xunit;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// Voice stream parsing (CE-5 voice). The wire format belongs to Core; this side must
/// read it exactly and fail loudly on garbage — a client that silently drops audio
/// leaves the owner with a silent assistant and no reason why.
/// </summary>
public sealed class VoiceStreamParserTests
{
    [Fact]
    public void Delta_carries_the_text()
    {
        var parsed = VoiceStreamParser.ParseLine("""data: {"type":"delta","text":"Привет,"}""");

        var delta = Assert.IsType<VoiceStreamEvent.Delta>(parsed);
        Assert.Equal("Привет,", delta.Text);
    }

    [Fact]
    public void Audio_carries_decoded_bytes()
    {
        byte[] raw = [1, 2, 3, 4, 5];
        string base64 = Convert.ToBase64String(raw);

        var parsed = VoiceStreamParser.ParseLine($$"""data: {"type":"audio","audio":"{{base64}}"}""");

        var audio = Assert.IsType<VoiceStreamEvent.Audio>(parsed);
        Assert.Equal(raw, audio.Data);
    }

    [Fact]
    public void Done_and_error_are_distinct()
    {
        Assert.IsType<VoiceStreamEvent.Done>(VoiceStreamParser.ParseLine("""data: {"type":"done"}"""));

        var error = Assert.IsType<VoiceStreamEvent.Error>(
            VoiceStreamParser.ParseLine("""data: {"type":"error","error":"model_unavailable"}"""));
        Assert.Equal("model_unavailable", error.Reason);
    }

    [Fact]
    public void Broken_json_is_an_error_not_silence()
    {
        var parsed = VoiceStreamParser.ParseLine("data: {not json");

        Assert.IsType<VoiceStreamEvent.Error>(parsed);
    }

    [Fact]
    public void Corrupted_base64_audio_is_reported()
    {
        var parsed = VoiceStreamParser.ParseLine("""data: {"type":"audio","audio":"!!!not base64!!!"}""");

        var error = Assert.IsType<VoiceStreamEvent.Error>(parsed);
        Assert.Contains("аудио", error.Reason);
    }

    [Fact]
    public void Event_without_a_type_is_an_error()
    {
        var parsed = VoiceStreamParser.ParseLine("""data: {"text":"без типа"}""");

        Assert.IsType<VoiceStreamEvent.Error>(parsed);
    }

    [Fact]
    public void Unknown_event_types_are_ignored_for_forward_compatibility()
    {
        Assert.Null(VoiceStreamParser.ParseLine("""data: {"type":"spectrum","level":0.5}"""));
    }

    [Fact]
    public void Blank_lines_comments_and_keepalives_are_skipped()
    {
        Assert.Null(VoiceStreamParser.ParseLine(null));
        Assert.Null(VoiceStreamParser.ParseLine(""));
        Assert.Null(VoiceStreamParser.ParseLine("   "));
        Assert.Null(VoiceStreamParser.ParseLine(": keep-alive"));
        Assert.Null(VoiceStreamParser.ParseLine("data:"));
        Assert.Null(VoiceStreamParser.ParseLine("event: message"));
    }

    [Fact]
    public void Delta_without_text_is_skipped_not_rendered_as_empty()
    {
        Assert.Null(VoiceStreamParser.ParseLine("""data: {"type":"delta"}"""));
    }

    [Fact]
    public void Empty_audio_payload_is_skipped()
    {
        Assert.Null(VoiceStreamParser.ParseLine("""data: {"type":"audio","audio":""}"""));
    }
}
