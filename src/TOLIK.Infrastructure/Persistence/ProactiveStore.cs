using Microsoft.EntityFrameworkCore;

namespace TOLIK.Infrastructure.Persistence;

/// <summary>
/// Durable proactive message store (reminders, scheduled notifications).
/// Core restarts never lose scheduled messages (AGENTS.md §23, §39).
/// </summary>
public sealed class ProactiveStore(IDbContextFactory<TolikDbContext> dbFactory)
{
    public async Task<TolikProactiveEntity> ScheduleAsync(
        Guid userId,
        string text,
        string priority,
        DateTimeOffset dueAt,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty) throw new ArgumentException("User is required.", nameof(userId));
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2048) throw new ArgumentException("Text is invalid.", nameof(text));
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 512) throw new ArgumentException("Reason is required.", nameof(reason));
        if (priority is not ("Low" or "Normal" or "High")) throw new ArgumentException("Priority is invalid.", nameof(priority));
        if (dueAt < DateTimeOffset.UtcNow.AddMinutes(-1)) throw new ArgumentException("DueAt cannot be in the past.", nameof(dueAt));

        var entity = new TolikProactiveEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Text = text.Trim(),
            Priority = priority,
            DueAt = dueAt,
            Reason = reason.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.Proactive.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }

    /// <summary>Takes due-and-undelivered messages for the user and marks them delivered atomically per row.</summary>
    public async Task<IReadOnlyList<TolikProactiveEntity>> TakeDueAsync(
        Guid userId,
        int limit = 8,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty) throw new ArgumentException("User is required.", nameof(userId));
        if (limit is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(limit));
        var now = DateTimeOffset.UtcNow;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var due = await db.Proactive
            .Where(x => x.UserId == userId && x.DeliveredAt == null && x.DueAt <= now)
            .OrderBy(x => x.DueAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        foreach (var message in due)
            message.DeliveredAt = now;

        if (due.Count > 0)
            await db.SaveChangesAsync(cancellationToken);

        return due;
    }

    /// <summary>Lists all scheduled (not yet delivered) messages for the user.</summary>
    public async Task<IReadOnlyList<TolikProactiveEntity>> ListPendingAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Proactive
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.DeliveredAt == null)
            .OrderBy(x => x.DueAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> CancelAsync(Guid userId, Guid messageId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var message = await db.Proactive.SingleOrDefaultAsync(x => x.Id == messageId && x.UserId == userId, cancellationToken);
        if (message is null || message.DeliveredAt is not null) return false;
        db.Proactive.Remove(message);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>True when any message (pending or delivered) with this reason already exists — idempotency guard.</summary>
    public async Task<bool> ExistsAsync(Guid userId, string reason, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(reason)) return true;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Proactive.AnyAsync(x => x.UserId == userId && x.Reason == reason, cancellationToken);
    }
}
