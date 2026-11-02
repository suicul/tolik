namespace TOLIK.Desktop.Chat;

/// <summary>Who produced a line in the conversation.</summary>
public enum ChatRole
{
    /// <summary>The owner, typed on this PC.</summary>
    Owner,
    /// <summary>TOLIK's answer, as Core returned it.</summary>
    Tolik,
    /// <summary>A failure or status note — never formatted like an answer.</summary>
    Notice,
}

/// <summary>One immutable line of the conversation.</summary>
public sealed record ChatMessage(ChatRole Role, string Text, DateTimeOffset At)
{
    public static ChatMessage OwnerSaid(string text) => new(ChatRole.Owner, text, DateTimeOffset.Now);
    public static ChatMessage TolikSaid(string text) => new(ChatRole.Tolik, text, DateTimeOffset.Now);
    public static ChatMessage Notice(string text) => new(ChatRole.Notice, text, DateTimeOffset.Now);
}
