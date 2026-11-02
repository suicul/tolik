using TOLIK.Desktop.Bridge;
using TOLIK.Desktop.Chat;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// Conversation state behaviour (CE-5). The Windows client is the main client, so
/// the chat surface must never look idle mid-turn, never print a failure as an
/// answer, and never grow without bound.
/// </summary>
public sealed class ChatSessionTests
{
    /// <summary>Scripted transport: returns the queued outcomes in order.</summary>
    private sealed class ScriptedTransport : IChatTransport
    {
        private readonly Queue<Func<string, CancellationToken, Task<ChatOutcome>>> _script = new();

        public List<string> Sent { get; } = [];

        public ScriptedTransport Then(Func<string, CancellationToken, Task<ChatOutcome>> step)
        {
            _script.Enqueue(step);
            return this;
        }

        public ScriptedTransport ThenOk(string answer) =>
            Then((_, _) => Task.FromResult(ChatOutcome.Ok(answer)));

        public ScriptedTransport ThenFail(ChatStatus status, string detail) =>
            Then((_, _) => Task.FromResult(ChatOutcome.Failed(status, detail)));

        public Task<ChatOutcome> SendAsync(string message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            if (_script.Count == 0)
            {
                return Task.FromResult(ChatOutcome.Failed(ChatStatus.Unavailable, "нет сценария"));
            }
            return _script.Dequeue()(message, cancellationToken);
        }
    }

    [Fact]
    public async Task Successful_turn_shows_the_owner_line_then_the_answer()
    {
        var transport = new ScriptedTransport().ThenOk("Два плюс два — четыре.");
        var session = new ChatSession(transport);

        ChatOutcome outcome = await session.SendAsync("  сколько будет два плюс два  ", CancellationToken.None);

        Assert.True(outcome.IsSuccess);
        ChatViewState state = session.Snapshot();
        Assert.Equal(2, state.Messages.Count);
        Assert.Equal(ChatRole.Owner, state.Messages[0].Role);
        Assert.Equal("сколько будет два плюс два", state.Messages[0].Text);
        Assert.Equal(ChatRole.Tolik, state.Messages[1].Role);
        Assert.Equal("Два плюс два — четыре.", state.Messages[1].Text);
        Assert.False(state.IsSending);
        Assert.Null(state.StatusLine);
        Assert.Equal(["сколько будет два плюс два"], transport.Sent);
    }

    [Fact]
    public async Task Sending_state_is_visible_while_the_turn_is_in_flight()
    {
        var gate = new TaskCompletionSource<ChatOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new ScriptedTransport().Then((_, _) => gate.Task);
        var session = new ChatSession(transport);
        var observed = new List<ChatViewState>();
        session.Changed += state => observed.Add(state);

        Task<ChatOutcome> pending = session.SendAsync("привет", CancellationToken.None);

        Assert.True(session.IsSending);
        Assert.Contains(observed, s => s.IsSending && s.StatusLine is not null);

        gate.SetResult(ChatOutcome.Ok("на связи"));
        await pending;

        Assert.False(session.IsSending);
        Assert.Equal(ChatRole.Tolik, session.Snapshot().Messages[^1].Role);
    }

    [Fact]
    public async Task Failure_becomes_a_notice_never_an_answer()
    {
        var transport = new ScriptedTransport().ThenFail(ChatStatus.Unavailable, "Core недоступен");
        var session = new ChatSession(transport);

        ChatOutcome outcome = await session.SendAsync("привет", CancellationToken.None);

        Assert.False(outcome.IsSuccess);
        ChatViewState state = session.Snapshot();
        Assert.Equal(2, state.Messages.Count);
        Assert.Equal(ChatRole.Notice, state.Messages[1].Role);
        Assert.Contains("Core недоступен", state.Messages[1].Text);
        // No answer line exists at all — Core did not answer.
        Assert.DoesNotContain(state.Messages, m => m.Role == ChatRole.Tolik);
    }

    [Fact]
    public async Task Auth_rejection_says_what_to_do()
    {
        var transport = new ScriptedTransport().ThenFail(ChatStatus.AuthRejected, "сессия отклонена (401)");
        var session = new ChatSession(transport);

        await session.SendAsync("привет", CancellationToken.None);

        Assert.Contains("сессия отклонена", session.Snapshot().Messages[^1].Text);
    }

    [Fact]
    public async Task Empty_message_is_not_sent_and_does_not_touch_the_transcript()
    {
        var transport = new ScriptedTransport();
        var session = new ChatSession(transport);

        ChatOutcome outcome = await session.SendAsync("   ", CancellationToken.None);

        Assert.False(outcome.IsSuccess);
        Assert.Empty(session.Snapshot().Messages);
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public async Task Second_send_while_busy_is_refused_not_queued()
    {
        var gate = new TaskCompletionSource<ChatOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new ScriptedTransport().Then((_, _) => gate.Task);
        var session = new ChatSession(transport);

        Task<ChatOutcome> first = session.SendAsync("первый", CancellationToken.None);
        ChatOutcome second = await session.SendAsync("второй", CancellationToken.None);

        Assert.False(second.IsSuccess);
        Assert.Equal(["первый"], transport.Sent);

        gate.SetResult(ChatOutcome.Ok("готово"));
        await first;
    }

    [Fact]
    public async Task Transport_that_throws_still_produces_an_honest_line()
    {
        var transport = new ScriptedTransport().Then((_, _) => throw new InvalidOperationException("boom"));
        var session = new ChatSession(transport);

        ChatOutcome outcome = await session.SendAsync("привет", CancellationToken.None);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ChatRole.Notice, session.Snapshot().Messages[^1].Role);
        Assert.False(session.IsSending);
    }

    [Fact]
    public async Task History_is_bounded()
    {
        var transport = new ScriptedTransport();
        for (int i = 0; i < ChatSession.MaxMessages; i++)
        {
            transport.ThenOk($"ответ {i}");
        }
        var session = new ChatSession(transport);

        for (int i = 0; i < ChatSession.MaxMessages; i++)
        {
            await session.SendAsync($"сообщение {i}", CancellationToken.None);
        }

        ChatViewState state = session.Snapshot();
        Assert.Equal(ChatSession.MaxMessages, state.Messages.Count);
        // The oldest lines are gone, the newest are kept.
        Assert.Equal($"ответ {ChatSession.MaxMessages - 1}", state.Messages[^1].Text);
    }

    [Fact]
    public void Clear_empties_the_surface_only()
    {
        var session = new ChatSession(new ScriptedTransport().ThenOk("x"));
        session.Note("локальная заметка");
        Assert.Single(session.Snapshot().Messages);

        session.Clear();

        Assert.Empty(session.Snapshot().Messages);
    }

    [Fact]
    public void Note_ignores_blank_text()
    {
        var session = new ChatSession(new ScriptedTransport());

        session.Note("   ");

        Assert.Empty(session.Snapshot().Messages);
    }
}
