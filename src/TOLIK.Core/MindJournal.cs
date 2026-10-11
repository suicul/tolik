namespace TOLIK.Core;

/// <summary>The fixed journal entry kinds — literals only, never user content (§19/§50).</summary>
public static class MindJournalKind
{
    public const string TaskStarted = "TaskStarted";
    public const string TaskCompleted = "TaskCompleted";
    public const string TaskFailed = "TaskFailed";
    public const string TaskCancelled = "TaskCancelled";
    public const string Interaction = "Interaction";
    public const string ActivitySelected = "ActivitySelected";
}

/// <summary>One operational journal record: when, what kind, the deterministic generic summary.</summary>
public sealed record MindJournalEntry(DateTimeOffset At, string Kind, string Summary);

/// <summary>
/// MindJournal (docs/mind.md §7, MI-6 — PLAN REFORM PRG-1b): TOLIK's structured
/// OPERATIONAL history — the real events of its own operation rendered as
/// deterministic generic summaries. NOT chain-of-thought. Privacy by construction
/// (§19/§50): the kinds are FIXED LITERALS and the summaries are generic — NO task
/// IDs, NO message text, NO descriptions, NO user identifiers EVER enter the journal,
/// so any authenticated session may safely read it. Bounded ring (drop-oldest);
/// thread-safe. «Чем занимался пока меня не было?» reads from this.
/// </summary>
public sealed class MindJournal
{
    public const int Capacity = 512;

    private readonly object _gate = new();
    private readonly Queue<MindJournalEntry> _entries = new();

    /// <summary>Records one event by its fixed kind; the summary is the deterministic
    /// generic rendering of that kind (never caller-supplied text).</summary>
    public void Record(string kind, DateTimeOffset? at = null)
    {
        var summary = kind switch
        {
            MindJournalKind.TaskStarted => "Задача запущена",
            MindJournalKind.TaskCompleted => "Задача завершена",
            MindJournalKind.TaskFailed => "Задача провалена",
            MindJournalKind.TaskCancelled => "Задача отменена",
            MindJournalKind.Interaction => "Взаимодействие",
            _ => throw new ArgumentException("Unknown journal kind (fixed literals only — never user content).", nameof(kind))
        };
        lock (_gate)
        {
            _entries.Enqueue(new MindJournalEntry(at ?? DateTimeOffset.UtcNow, kind, summary));
            while (_entries.Count > Capacity) _entries.Dequeue();
        }
    }

    /// <summary>Records the selected activity (MI-3): the kind is the fixed literal;
    /// the summary MUST come from the fixed candidate catalog — anything else is
    /// rejected (privacy by construction, §19/§50: never caller-supplied content).</summary>
    public void RecordActivity(string activitySummary, DateTimeOffset? at = null)
    {
        if (!ActivityCandidate.Catalog.Any(c => string.Equals(c.Summary, activitySummary, StringComparison.Ordinal)))
            throw new ArgumentException("Unknown activity summary (fixed catalog only — never user content).", nameof(activitySummary));
        lock (_gate)
        {
            _entries.Enqueue(new MindJournalEntry(at ?? DateTimeOffset.UtcNow, MindJournalKind.ActivitySelected, activitySummary));
            while (_entries.Count > Capacity) _entries.Dequeue();
        }
    }

    /// <summary>The most recent entries (oldest-first; the newest `limit` kept).</summary>
    public IReadOnlyList<MindJournalEntry> Recent(int limit = 32)
    {
        if (limit is < 1 or > Capacity) limit = 32;
        lock (_gate)
        {
            var all = _entries.ToArray();
            return all.Length <= limit ? all : all.Skip(all.Length - limit).ToArray();
        }
    }

    /// <summary>A deterministic grouped Russian summary of the events within the window:
    /// «За последний час: 3 задачи завершено, 1 провалена, 2 взаимодействия.»
    /// The plural forms follow the Russian rules (1/2-4/5+); an empty window is honest.</summary>
    public string RenderRecent(TimeSpan window, DateTimeOffset now)
    {
        int started = 0, completed = 0, failed = 0, cancelled = 0, interactions = 0;
        var activities = new List<string>();
        lock (_gate)
        {
            var from = now - window;
            foreach (var entry in _entries)
            {
                if (entry.At < from) continue;
                switch (entry.Kind)
                {
                    case MindJournalKind.TaskStarted: started++; break;
                    case MindJournalKind.TaskCompleted: completed++; break;
                    case MindJournalKind.TaskFailed: failed++; break;
                    case MindJournalKind.TaskCancelled: cancelled++; break;
                    case MindJournalKind.Interaction: interactions++; break;
                    case MindJournalKind.ActivitySelected:
                        if (!activities.Contains(entry.Summary)) activities.Add(entry.Summary); // the distinct occupations
                        break;
                }
            }
        }

        var parts = new List<string>();
        if (started > 0) parts.Add(TaskPhrase(started, "запущена", "запущено"));
        if (completed > 0) parts.Add(TaskPhrase(completed, "завершена", "завершено"));
        if (failed > 0) parts.Add(TaskPhrase(failed, "провалена", "провалено"));
        if (cancelled > 0) parts.Add(TaskPhrase(cancelled, "отменена", "отменено"));
        if (interactions > 0) parts.Add($"{interactions} {PluralWord(interactions, "взаимодействие", "взаимодействия", "взаимодействий")}");
        if (activities.Count > 0) parts.Add($"занятия: {string.Join(", ", activities)}");
        return parts.Count == 0
            ? $"{WindowText(window)}: ничего существенного."
            : $"{WindowText(window)}: {string.Join(", ", parts)}.";
    }

    private static string TaskPhrase(int count, string one, string many) =>
        $"{count} {PluralWord(count, "задача", "задачи", "задач")} {(count % 10 == 1 && count % 100 != 11 ? one : many)}";

    /// <summary>Russian plural: one (1, 21, 31…), few (2-4, 22-24…), many (5-20, 11-14, 25…).</summary>
    private static string PluralWord(int count, string one, string few, string many)
    {
        if (count % 10 == 1 && count % 100 != 11) return one;
        if (count % 10 is >= 2 and <= 4 && count % 100 is not (>= 12 and <= 14)) return few;
        return many;
    }

    private static string WindowText(TimeSpan window)
    {
        var hours = (int)Math.Max(1.0, Math.Round(window.TotalHours, MidpointRounding.AwayFromZero));
        if (hours == 1) return "За последний час";
        if (hours < 24) return $"За последние {hours} {PluralWord(hours, "час", "часа", "часов")}";
        var days = (int)Math.Round(hours / 24.0, MidpointRounding.AwayFromZero);
        return $"За последние {days} {PluralWord(days, "день", "дня", "дней")}";
    }
}
