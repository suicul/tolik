using TOLIK.Desktop.Bridge;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// The mind-voice wire contract (CA-5 presence_return, MI-7 proactive), verified
/// against Core's EXACT published shapes (CephalonExpressionService.BuildProactiveJson /
/// BuildPresenceReturnJson, wrapped by the hub pump as {"type":"event","payload":{...}}).
/// Same trust-boundary discipline as the expression parser: unknown kinds and the
/// Android-lane duplicates are safely Ignored; anything that claims to be ours but
/// is broken (missing/empty/oversized summary) is REJECTED whole — the band never
/// displays a mangled half-truth, and the wording is presented verbatim.
/// </summary>
public class MessageWireParserTests
{
    /// <summary>The EXACT live wire shape of the MI-7 failure-pattern surface.</summary>
    private const string LiveProactiveFrame =
        "{\"type\":\"event\",\"payload\":{\"kind\":\"proactive\",\"summary\":" +
        "\"Замечена серия неудач: 3 задач подряд не завершились. Стоит разобраться.\"}}";

    /// <summary>The EXACT live wire shape of the CA-5 (re)connect digest.</summary>
    private const string LivePresenceReturnFrame =
        "{\"type\":\"event\",\"payload\":{\"kind\":\"presence_return\",\"summary\":" +
        "\"За последний час: 3 задачи завершено, 1 провалена, 2 взаимодействия.\"}}";

    [Fact]
    public void Live_proactive_shape_parses_verbatim()
    {
        ExpressionParseOutcome outcome = ExpressionWireParser.TryParseMessage(LiveProactiveFrame, out ProactiveMessage? message);

        Assert.Equal(ExpressionParseOutcome.Parsed, outcome);
        Assert.NotNull(message);
        Assert.Equal(MessageKind.Proactive, message!.Kind);
        Assert.Equal("Замечена серия неудач: 3 задач подряд не завершились. Стоит разобраться.", message.Summary);
        Assert.Null(message.At); // the live payloads carry no timestamp
    }

    [Fact]
    public void Live_presence_return_shape_parses_verbatim()
    {
        ExpressionParseOutcome outcome = ExpressionWireParser.TryParseMessage(LivePresenceReturnFrame, out ProactiveMessage? message);

        Assert.Equal(ExpressionParseOutcome.Parsed, outcome);
        Assert.NotNull(message);
        Assert.Equal(MessageKind.PresenceReturn, message!.Kind);
        Assert.Equal("За последний час: 3 задачи завершено, 1 провалена, 2 взаимодействия.", message.Summary);
        Assert.Null(message.At);
    }

    [Fact]
    public void Bare_payload_form_is_tolerated()
    {
        // A direct hub publish without the envelope is still an honest message.
        string json = "{\"kind\":\"proactive\",\"summary\":\"Сборка завершена.\"}";

        Assert.Equal(ExpressionParseOutcome.Parsed, ExpressionWireParser.TryParseMessage(json, out ProactiveMessage? message));
        Assert.NotNull(message);
        Assert.Equal(MessageKind.Proactive, message!.Kind);
        Assert.Equal("Сборка завершена.", message.Summary);
    }

    [Fact]
    public void Android_lane_duplicate_is_ignored_not_shown()
    {
        // Core publishes the SAME event twice with two lane shapes; the desktop lane
        // is the kind-based one. The type/text duplicate must never double-display.
        string json =
            "{\"type\":\"event\",\"payload\":{\"type\":\"proactive_mind\"," +
            "\"text\":\"Замечена серия неудач.\",\"at\":\"2026-10-14T10:00:00.0000000Z\"}}";

        Assert.Equal(ExpressionParseOutcome.Ignored, ExpressionWireParser.TryParseMessage(json, out ProactiveMessage? message));
        Assert.Null(message);
    }

    [Fact]
    public void Expression_frames_are_ignored_by_the_message_parser()
    {
        // The parsers coexist on one socket, each dispatching on payload.kind.
        string json =
            "{\"type\":\"event\",\"payload\":{\"kind\":\"cephalon_expression\"," +
            "\"expression\":{\"eyeScale\":1.0},\"at\":\"t\"}}";

        Assert.Equal(ExpressionParseOutcome.Ignored, ExpressionWireParser.TryParseMessage(json, out ProactiveMessage? message));
        Assert.Null(message);
    }

    [Fact]
    public void Unknown_kind_is_ignored_not_rejected()
    {
        // The hub carries other traffic (tasks, agents) — normal, not ours.
        string json = "{\"type\":\"event\",\"payload\":{\"kind\":\"task_started\",\"taskId\":\"42\"}}";

        Assert.Equal(ExpressionParseOutcome.Ignored, ExpressionWireParser.TryParseMessage(json, out ProactiveMessage? message));
        Assert.Null(message);
    }

    [Fact]
    public void Malformed_json_is_rejected_and_yields_no_message()
    {
        string truncated = LiveProactiveFrame[..^30];

        Assert.Equal(ExpressionParseOutcome.Rejected, ExpressionWireParser.TryParseMessage(truncated, out ProactiveMessage? message));
        Assert.Null(message);
    }

    [Fact]
    public void Ours_but_missing_summary_is_rejected()
    {
        string json = "{\"type\":\"event\",\"payload\":{\"kind\":\"proactive\"}}";

        Assert.Equal(ExpressionParseOutcome.Rejected, ExpressionWireParser.TryParseMessage(json, out ProactiveMessage? message));
        Assert.Null(message);
    }

    [Theory]
    [InlineData("\"summary\":42")]
    [InlineData("\"summary\":null")]
    [InlineData("\"summary\":true")]
    [InlineData("\"summary\":[\"a\"]")]
    public void Ours_but_non_string_summary_is_rejected(string poison)
    {
        string json = "{\"type\":\"event\",\"payload\":{\"kind\":\"presence_return\"," + poison + "}}";

        Assert.Equal(ExpressionParseOutcome.Rejected, ExpressionWireParser.TryParseMessage(json, out ProactiveMessage? message));
        Assert.Null(message);
    }

    [Theory]
    [InlineData("\"summary\":\"\"")]
    [InlineData("\"summary\":\"   \"")]
    public void Ours_but_empty_summary_is_rejected(string poison)
    {
        string json = "{\"type\":\"event\",\"payload\":{\"kind\":\"proactive\"," + poison + "}}";

        Assert.Equal(ExpressionParseOutcome.Rejected, ExpressionWireParser.TryParseMessage(json, out ProactiveMessage? message));
        Assert.Null(message);
    }

    [Fact]
    public void Ours_but_oversized_summary_is_rejected_whole()
    {
        // Never a truncated display: a broken/hostile payload is dropped whole.
        string oversized = new('x', ExpressionWireParser.MaxSummaryLength + 1);
        string json = "{\"type\":\"event\",\"payload\":{\"kind\":\"proactive\",\"summary\":\"" + oversized + "\"}}";

        Assert.Equal(ExpressionParseOutcome.Rejected, ExpressionWireParser.TryParseMessage(json, out ProactiveMessage? message));
        Assert.Null(message);
    }

    [Fact]
    public void Summary_at_the_cap_parses()
    {
        string atCap = new('x', ExpressionWireParser.MaxSummaryLength);
        string json = "{\"type\":\"event\",\"payload\":{\"kind\":\"proactive\",\"summary\":\"" + atCap + "\"}}";

        Assert.Equal(ExpressionParseOutcome.Parsed, ExpressionWireParser.TryParseMessage(json, out ProactiveMessage? message));
        Assert.NotNull(message);
        Assert.Equal(ExpressionWireParser.MaxSummaryLength, message!.Summary.Length);
    }

    [Fact]
    public void String_at_is_carried_verbatim_and_non_string_at_is_dropped()
    {
        // "at" is optional display metadata; it never gates acceptance.
        string withAt = "{\"type\":\"event\",\"payload\":{\"kind\":\"proactive\"," +
                        "\"summary\":\"Проверка.\",\"at\":\"2026-10-14T10:00:00.0000000Z\"}}";
        Assert.Equal(ExpressionParseOutcome.Parsed, ExpressionWireParser.TryParseMessage(withAt, out ProactiveMessage? message));
        Assert.NotNull(message);
        Assert.Equal("2026-10-14T10:00:00.0000000Z", message!.At);

        string badAt = "{\"type\":\"event\",\"payload\":{\"kind\":\"proactive\",\"summary\":\"Проверка.\",\"at\":7}}";
        Assert.Equal(ExpressionParseOutcome.Parsed, ExpressionWireParser.TryParseMessage(badAt, out ProactiveMessage? dropped));
        Assert.NotNull(dropped);
        Assert.Null(dropped!.At);
    }

    [Fact]
    public void Unknown_extra_fields_are_tolerated()
    {
        // Forward compatibility: Core may ADD fields; the band must not break.
        string json = LiveProactiveFrame.Replace("\"summary\":", "\"futureField\":123,\"summary\":");

        Assert.Equal(ExpressionParseOutcome.Parsed, ExpressionWireParser.TryParseMessage(json, out ProactiveMessage? message));
        Assert.NotNull(message);
        Assert.Equal(MessageKind.Proactive, message!.Kind);
    }

    [Fact]
    public void Non_event_envelope_is_ignored()
    {
        string json = "{\"type\":\"request\",\"payload\":{\"kind\":\"proactive\",\"summary\":\"x\"}}";

        Assert.Equal(ExpressionParseOutcome.Ignored, ExpressionWireParser.TryParseMessage(json, out ProactiveMessage? message));
        Assert.Null(message);
    }

    [Fact]
    public void Non_object_payload_is_ignored()
    {
        string json = "{\"type\":\"event\",\"payload\":42}";

        Assert.Equal(ExpressionParseOutcome.Ignored, ExpressionWireParser.TryParseMessage(json, out ProactiveMessage? message));
        Assert.Null(message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"hello\"")]          // a bare JSON string — not our shape
    [InlineData("[1,2,3]")]            // an array — not our shape
    [InlineData("{}")]                 // an empty object — hello/noise frames
    public void Non_message_input_is_ignored(string? json)
    {
        Assert.Equal(ExpressionParseOutcome.Ignored, ExpressionWireParser.TryParseMessage(json, out ProactiveMessage? message));
        Assert.Null(message);
    }

    [Fact]
    public void Message_kinds_do_not_leak_into_the_expression_parser()
    {
        // The reverse direction of the coexistence contract: a mind-voice frame is
        // Ignored (not Rejected) by the expression parser — normal traffic.
        Assert.Equal(ExpressionParseOutcome.Ignored, ExpressionWireParser.TryParse(LiveProactiveFrame, out ExpressionFrame? frame));
        Assert.Null(frame);
    }
}
