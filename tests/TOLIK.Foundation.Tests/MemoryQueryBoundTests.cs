using System.Net;
using System.Net.Http.Json;
using TOLIK.Core;

namespace TOLIK.Core.Host.Tests;

/// <summary>
/// Bounds the memory-search probe.
///
/// Chat and voice hand the whole user sentence to retrieval. The store accepts at most
/// <see cref="DurableMemoryStoreQueryLimit"/> characters and throws beyond that, so any turn
/// whose utterance was longer died with an unhandled ArgumentException → HTTP 500. The
/// fix keeps the store's validated contract and bounds the probe at the caller through one
/// function, so these tests pin both halves: the bound itself, and that a long turn survives.
/// </summary>
public sealed class MemoryQueryBoundTests
{
    // Mirrors DurableMemoryStore.MaxQueryLength without taking a dependency on storage from
    // a pure-function test; the store's own validation test pins the other side.
    private const int DurableMemoryStoreQueryLimit = 256;

    [Fact]
    public void A_null_or_blank_utterance_becomes_an_empty_probe()
    {
        Assert.Equal(string.Empty, MemoryContextBuilder.BuildSearchQuery(null));
        Assert.Equal(string.Empty, MemoryContextBuilder.BuildSearchQuery(""));
        Assert.Equal(string.Empty, MemoryContextBuilder.BuildSearchQuery("   \t\n "));
    }

    [Fact]
    public void A_short_utterance_is_trimmed_but_not_altered()
    {
        Assert.Equal("сколько будет 2+2", MemoryContextBuilder.BuildSearchQuery("  сколько будет 2+2  "));
    }

    [Fact]
    public void A_probe_never_exceeds_the_store_limit()
    {
        string longUtterance = new('я', DurableMemoryStoreQueryLimit * 4);

        string probe = MemoryContextBuilder.BuildSearchQuery(longUtterance);

        Assert.Equal(DurableMemoryStoreQueryLimit, probe.Length);
        Assert.StartsWith(new string('я', 16), probe);
    }

    [Fact]
    public void A_probe_at_the_limit_is_kept_whole()
    {
        string exact = new('x', DurableMemoryStoreQueryLimit);
        Assert.Equal(exact, MemoryContextBuilder.BuildSearchQuery(exact));
    }

    [Fact]
    public async Task A_long_chat_message_is_answered_instead_of_failing()
    {
        // Regression: this used to return 500 with an unhandled ArgumentException from
        // DurableMemoryStore.SearchAsync, because the raw message was used as the probe.
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();
        string token = await TestEnrollment.EnrollOwnerAsync(client);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        string longMessage = "запомни: " + new string('д', 400);

        var response = await client.PostAsJsonAsync("/chat", new { message = longMessage });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_long_recall_request_does_not_crash_the_endpoint()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();
        string token = await TestEnrollment.EnrollOwnerAsync(client);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        string longRecall = "вспомни " + new string('ф', 400);

        var response = await client.PostAsJsonAsync("/chat", new { message = longRecall });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Memory_search_endpoint_survives_an_oversized_query()
    {
        // The endpoint used to carry its own inline retrieval query and pass q through
        // unbounded. It now goes through the same store and the same probe bound as every
        // other caller, so a long query is bounded instead of becoming a 500.
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();
        string token = await TestEnrollment.EnrollOwnerAsync(client);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var response = await client.GetAsync("/memory/search?q=" + new string('z', 300));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
