using System.Collections.Immutable;
using System.Text.Json.Nodes;
using TOLIK.Contracts;

namespace TOLIK.Foundation.Tests.Contracts;

public sealed class ValidEnvelope
{
    [Fact]
    public void Given_a_valid_command_envelope_when_round_tripped_as_json_then_version_requester_capability_and_correlation_are_preserved()
    {
        // Given
        var envelope = CreateValidEnvelope();

        // When
        var json = ContractJson.Serialize(envelope);
        var parsed = ContractJson.Parse<CommandRequest>(json);

        // Then
        Assert.True(parsed.IsValid);
        var roundTripped = Assert.IsType<ProtocolEnvelope<CommandRequest>>(parsed.Envelope);
        Assert.Equal(ProtocolVersions.V1, roundTripped.ProtocolVersion);
        Assert.Equal(envelope.CorrelationId, roundTripped.CorrelationId);
        Assert.Equal(envelope.Payload.Requester, roundTripped.Payload.Requester);
        Assert.Equal(AgentCapability.RunBuild, roundTripped.Payload.Command.Capability);
    }

    private static ProtocolEnvelope<CommandRequest> CreateValidEnvelope() => new(
        ProtocolVersions.V1,
        Guid.Parse("4f583ccd-ffbf-4d43-94ee-07639028f59f"),
        new CommandRequest(
            new RequesterIdentity(Guid.Parse("4e8275c0-f0dd-4029-834d-b68706c3b01c"), IdentityRole.Owner),
            new SessionReference(
                Guid.Parse("54e55714-b223-444b-8dc1-910bc3cc86f1"),
                Guid.Parse("f632f36a-cce4-414a-936b-4c26c73085e7")),
            new TaskReference(Guid.Parse("d61a0236-3dd2-4904-9500-4a18f8e59acf")),
            new AgentCommand(
                Guid.Parse("42339907-b8c6-46d4-8284-9d8350edfeac"),
                AgentCapability.RunBuild,
                ToolRiskLevel.LowRisk,
                ImmutableDictionary<string, string>.Empty.Add("projectId", "sample-project"))));
}

public sealed class InvalidEnvelope
{
    [Fact]
    public void Given_a_command_envelope_without_a_correlation_id_when_validated_then_a_deterministic_validation_error_is_returned()
    {
        // Given
        var envelope = CreateValidEnvelope() with { CorrelationId = Guid.Empty };

        // When
        var validation = envelope.Validate();

        // Then
        Assert.Contains(validation.Errors, error => error.Code == ProtocolValidationErrorCode.MissingCorrelationId);
    }

    [Fact]
    public void Given_an_unknown_protocol_version_when_validated_then_a_deterministic_validation_error_is_returned()
    {
        // Given
        var envelope = CreateValidEnvelope() with { ProtocolVersion = new ProtocolVersion("v999") };

        // When
        var validation = envelope.Validate();

        // Then
        Assert.Contains(validation.Errors, error => error.Code == ProtocolValidationErrorCode.UnknownProtocolVersion);
    }

    [Fact]
    public void Given_an_unknown_risk_level_when_validated_then_a_deterministic_validation_error_is_returned()
    {
        // Given
        var command = CreateValidEnvelope().Payload.Command with { RiskLevel = (ToolRiskLevel)999 };
        var envelope = CreateValidEnvelope() with
        {
            Payload = CreateValidEnvelope().Payload with { Command = command }
        };

        // When
        var validation = envelope.Validate();

        // Then
        Assert.Contains(validation.Errors, error => error.Code == ProtocolValidationErrorCode.UnknownRiskLevel);
    }

    [Fact]
    public void Given_a_json_integer_for_risk_level_when_parsed_then_the_envelope_is_rejected_before_transport()
    {
        // Given
        var validJson = ContractJson.Serialize(CreateValidEnvelope());
        var malformedJson = validJson.Replace("\"lowRisk\"", "999", StringComparison.Ordinal);

        // When
        var parsed = ContractJson.Parse<CommandRequest>(malformedJson);

        // Then
        Assert.False(parsed.IsValid);
        Assert.Null(parsed.Envelope);
        Assert.Contains(parsed.Validation.Errors, error => error.Code == ProtocolValidationErrorCode.MalformedJson);
    }

    [Theory]
    [InlineData("capability")]
    [InlineData("riskLevel")]
    public void Given_command_envelope_json_without_a_required_security_property_when_parsed_then_it_is_rejected_before_transport(string propertyName)
    {
        // Given
        var malformedJson = RemoveCommandProperty(CreateValidEnvelope(), propertyName);

        // When
        var parsed = ContractJson.Parse<CommandRequest>(malformedJson);

        // Then
        Assert.False(parsed.IsValid);
        Assert.Null(parsed.Envelope);
        Assert.Contains(parsed.Validation.Errors, error => error.Code == ProtocolValidationErrorCode.MalformedJson);
    }

    [Fact]
    public void Given_command_result_json_without_an_outcome_when_parsed_then_it_is_rejected_before_transport()
    {
        // Given
        var malformedJson = RemovePayloadProperty(CreateCommandResultEnvelope(), "outcome");

        // When
        var parsed = ContractJson.Parse<CommandResult>(malformedJson);

        // Then
        Assert.False(parsed.IsValid);
        Assert.Null(parsed.Envelope);
        Assert.Contains(parsed.Validation.Errors, error => error.Code == ProtocolValidationErrorCode.MalformedJson);
    }

    [Fact]
    public void Given_event_json_without_a_kind_when_parsed_then_it_is_rejected_before_transport()
    {
        // Given
        var malformedJson = RemovePayloadProperty(CreateEventEnvelope(), "kind");

        // When
        var parsed = ContractJson.Parse<ProtocolEvent>(malformedJson);

        // Then
        Assert.False(parsed.IsValid);
        Assert.Null(parsed.Envelope);
        Assert.Contains(parsed.Validation.Errors, error => error.Code == ProtocolValidationErrorCode.MalformedJson);
    }

    [Fact]
    public void Given_command_json_with_null_arguments_when_parsed_then_it_is_rejected_before_transport()
    {
        // Given
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(ContractJson.Serialize(CreateValidEnvelope())));
        var payload = Assert.IsType<JsonObject>(root["payload"]);
        var command = Assert.IsType<JsonObject>(payload["command"]);
        command["arguments"] = null;

        // When
        var parsed = ContractJson.Parse<CommandRequest>(root.ToJsonString());

        // Then
        Assert.False(parsed.IsValid);
        Assert.Contains(parsed.Validation.Errors, error => error.Code == ProtocolValidationErrorCode.MalformedJson);
    }

    [Fact]
    public void Given_a_duplicate_correlation_id_when_parsed_then_it_is_rejected_before_transport()
    {
        // Given
        var envelope = CreateValidEnvelope();
        var validJson = ContractJson.Serialize(envelope);
        var malformedJson = validJson.Replace(
            "\"payload\":",
            $"\"correlationId\":\"{envelope.CorrelationId}\",\"payload\":",
            StringComparison.Ordinal);
        Assert.NotEqual(validJson, malformedJson);

        // When
        var parsed = ContractJson.Parse<CommandRequest>(malformedJson);

        // Then
        Assert.False(parsed.IsValid);
        Assert.Null(parsed.Envelope);
        Assert.Contains(parsed.Validation.Errors, error => error.Code == ProtocolValidationErrorCode.MalformedJson);
    }

    [Fact]
    public void Given_an_undefined_identity_role_when_validated_then_a_deterministic_validation_error_is_returned()
    {
        // Given
        var envelope = CreateValidEnvelope() with
        {
            Payload = CreateValidEnvelope().Payload with
            {
                Requester = new RequesterIdentity(
                    CreateValidEnvelope().Payload.Requester.IdentityId,
                    (IdentityRole)999)
            }
        };

        // When
        var validation = envelope.Validate();

        // Then
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Code == ProtocolValidationErrorCode.UnknownIdentityRole);
    }

    private static string RemoveCommandProperty(ProtocolEnvelope<CommandRequest> envelope, string propertyName)
    {
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(ContractJson.Serialize(envelope)));
        var payload = Assert.IsType<JsonObject>(root["payload"]);
        var command = Assert.IsType<JsonObject>(payload["command"]);
        Assert.True(command.Remove(propertyName));
        return root.ToJsonString();
    }

    private static string RemovePayloadProperty<TPayload>(ProtocolEnvelope<TPayload> envelope, string propertyName)
        where TPayload : IProtocolPayload
    {
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(ContractJson.Serialize(envelope)));
        var payload = Assert.IsType<JsonObject>(root["payload"]);
        Assert.True(payload.Remove(propertyName));
        return root.ToJsonString();
    }

    private static ProtocolEnvelope<CommandResult> CreateCommandResultEnvelope() => new(
        ProtocolVersions.V1,
        Guid.Parse("2a38eb75-5ec6-49b2-8225-1fab1884f142"),
        new CommandResult(
            Guid.Parse("42339907-b8c6-46d4-8284-9d8350edfeac"),
            CommandOutcome.Completed,
            null));

    private static ProtocolEnvelope<ProtocolEvent> CreateEventEnvelope() => new(
        ProtocolVersions.V1,
        Guid.Parse("71cbb5e6-1ee8-4a80-b0dd-6ff192f03ddf"),
        new ProtocolEvent(
            Guid.Parse("98465500-a116-4f1a-97f6-4e320d3ff2f0"),
            ProtocolEventKind.TaskStarted,
            DateTimeOffset.Parse("2026-09-18T00:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture),
            Guid.Parse("d61a0236-3dd2-4904-9500-4a18f8e59acf")));

    private static ProtocolEnvelope<CommandRequest> CreateValidEnvelope() => new(
        ProtocolVersions.V1,
        Guid.Parse("4f583ccd-ffbf-4d43-94ee-07639028f59f"),
        new CommandRequest(
            new RequesterIdentity(Guid.Parse("4e8275c0-f0dd-4029-834d-b68706c3b01c"), IdentityRole.Owner),
            new SessionReference(
                Guid.Parse("54e55714-b223-444b-8dc1-910bc3cc86f1"),
                Guid.Parse("f632f36a-cce4-414a-936b-4c26c73085e7")),
            new TaskReference(Guid.Parse("d61a0236-3dd2-4904-9500-4a18f8e59acf")),
            new AgentCommand(
                Guid.Parse("42339907-b8c6-46d4-8284-9d8350edfeac"),
                AgentCapability.RunBuild,
                ToolRiskLevel.LowRisk,
                ImmutableDictionary<string, string>.Empty)));
}
