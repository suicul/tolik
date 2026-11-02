using TOLIK.Desktop.Bridge;

namespace TOLIK.Desktop.Chat;

/// <summary>What the conversation surface renders. One value, replaced atomically.</summary>
public sealed record ChatViewState(
    IReadOnlyList<ChatMessage> Messages,
    bool IsSending,
    string? StatusLine)
{
    public static readonly ChatViewState Empty = new([], IsSending: false, StatusLine: null);
}

/// <summary>
/// Conversation state for the Windows client — the whole chat brain, with no WPF
/// dependency so the behaviour is testable.
///
/// Rules it enforces (AGENTS.md §34, §40, §43):
///   • a failed turn is visible: a Notice line says what went wrong, and it is NOT
///     rendered as an answer;
///   • no silent retry loops — one send, one outcome, reported honestly;
///   • "sending" is a real state, so the UI cannot look idle mid-turn;
///   • history is bounded (a presence surface, not an archive — the archive is
///     Core's memory, not this list).
/// </summary>
public sealed class ChatSession
{
    /// <summary>Bounded history: the presence window is a working surface, not storage.</summary>
    public const int MaxMessages = 200;

    private readonly IChatTransport _transport;
    private readonly List<ChatMessage> _messages = [];
    private readonly object _gate = new();
    private bool _sending;
    private string? _status;

    public ChatSession(IChatTransport transport)
        => _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    /// <summary>Raised after every state change (already computed, no locks held).</summary>
    public event Action<ChatViewState>? Changed;

    public ChatViewState Snapshot()
    {
        lock (_gate)
        {
            return new ChatViewState(_messages.ToArray(), _sending, _status);
        }
    }

    public bool IsSending
    {
        get { lock (_gate) { return _sending; } }
    }

    /// <summary>
    /// Sends one message. Returns the outcome so callers (and tests) can assert on
    /// it; the transcript already reflects everything the user must see.
    /// </summary>
    public async Task<ChatOutcome> SendAsync(string text, CancellationToken cancellationToken)
    {
        string message = (text ?? string.Empty).Trim();
        if (message.Length == 0)
        {
            return ChatOutcome.Failed(ChatStatus.Malformed, "пустое сообщение");
        }

        lock (_gate)
        {
            if (_sending)
            {
                return ChatOutcome.Failed(ChatStatus.Malformed, "предыдущий запрос ещё выполняется");
            }
            _sending = true;
            _status = "Толик думает…";
            Append(ChatMessage.OwnerSaid(message));
        }
        Raise();

        ChatOutcome outcome;
        try
        {
            outcome = await _transport.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_gate)
            {
                _sending = false;
                _status = null;
                Append(ChatMessage.Notice("отменено"));
            }
            Raise();
            throw;
        }
        catch (Exception)
        {
            // The transport contract says it does not throw; if an implementation
            // breaks that promise the user still gets an honest line, not a crash.
            outcome = ChatOutcome.Failed(ChatStatus.Unavailable, "сбой связи с Core");
        }

        lock (_gate)
        {
            _sending = false;
            _status = null;
            if (outcome.IsSuccess && outcome.Answer is { Length: > 0 } answer)
            {
                Append(ChatMessage.TolikSaid(answer));
            }
            else
            {
                Append(ChatMessage.Notice(DescribeFailure(outcome)));
            }
        }
        Raise();
        return outcome;
    }

    /// <summary>Token-free reason; every branch says something the owner can act on.</summary>
    public static string DescribeFailure(ChatOutcome outcome) => outcome.Status switch
    {
        ChatStatus.Unavailable => $"Core недоступен — {outcome.Detail ?? "нет связи"}",
        ChatStatus.AuthRejected => "сессия отклонена, нужен новый вход",
        ChatStatus.Malformed => $"некорректный ответ — {outcome.Detail ?? "неизвестно"}",
        ChatStatus.Rejected => outcome.Detail ?? "Core отклонил запрос",
        _ => "не удалось отправить",
    };

    /// <summary>Clears the visible conversation (Core's memory is untouched).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _messages.Clear();
            _status = null;
        }
        Raise();
    }

    /// <summary>Appends a local note (e.g. voice errors) without a network turn.</summary>
    public void Note(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        lock (_gate)
        {
            Append(ChatMessage.Notice(text.Trim()));
        }
        Raise();
    }

    private void Append(ChatMessage message)
    {
        _messages.Add(message);
        if (_messages.Count > MaxMessages)
        {
            _messages.RemoveRange(0, _messages.Count - MaxMessages);
        }
    }

    private void Raise() => Changed?.Invoke(Snapshot());
}

/// <summary>The one thing the conversation needs from the transport (keeps tests trivial).</summary>
public interface IChatTransport
{
    Task<ChatOutcome> SendAsync(string message, CancellationToken cancellationToken);
}
