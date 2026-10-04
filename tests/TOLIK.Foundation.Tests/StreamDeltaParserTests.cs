using TOLIK.Infrastructure.Models;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Streaming completion delta parser (docs/voice-runtime-v2.md 0.5/M4): the Ollama
/// NDJSON lines and the OpenAI SSE "data: {json}" lines both yield text deltas;
/// [DONE]/keep-alives/comments/empty deltas yield null. The first yielded delta IS the
/// first token (LLM TTFT).
/// </summary>
public sealed class StreamDeltaParserTests
{
    [Fact]
    public void Ollama_ndjson_lines_yield_deltas()
    {
        Assert.Equal("Привет", ModelCompletionClient.ExtractStreamDelta("""{"message":{"content":"Привет"},"done":false}"""));
        Assert.Equal("!", ModelCompletionClient.ExtractStreamDelta("""{"message":{"content":"!"},"done":false}"""));
        Assert.Equal(".", ModelCompletionClient.ExtractStreamDelta("""{"message":{"content":"."},"done":true}"""));
    }

    [Fact]
    public void Openai_sse_lines_yield_deltas()
    {
        Assert.Equal("Hello", ModelCompletionClient.ExtractStreamDelta("""data: {"choices":[{"delta":{"content":"Hello"}}]}"""));
        Assert.Equal("!", ModelCompletionClient.ExtractStreamDelta("""data: {"choices":[{"delta":{"content":"!"}}]}"""));
        Assert.Null(ModelCompletionClient.ExtractStreamDelta("data: [DONE]"));
    }

    [Fact]
    public void Non_data_lines_and_empty_deltas_yield_null()
    {
        Assert.Null(ModelCompletionClient.ExtractStreamDelta(null));
        Assert.Null(ModelCompletionClient.ExtractStreamDelta(""));
        Assert.Null(ModelCompletionClient.ExtractStreamDelta(": keep-alive comment"));
        Assert.Null(ModelCompletionClient.ExtractStreamDelta("data: {\"choices\":[{\"delta\":{}}]}")); // empty delta
        Assert.Null(ModelCompletionClient.ExtractStreamDelta("not json at all"));
        Assert.Null(ModelCompletionClient.ExtractStreamDelta("""{"message":{"content":""}}""")); // empty content
    }

    [Fact]
    public void Plain_generation_shape_yields_delta()
    {
        Assert.Equal("delta", ModelCompletionClient.ExtractStreamDelta("""{"response":"delta","done":false}"""));
    }
}
