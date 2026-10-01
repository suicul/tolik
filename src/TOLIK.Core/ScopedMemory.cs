namespace TOLIK.Core;

public enum MemoryScope { Private, Shared, Public }
public sealed record MemoryItem(Guid Id, Guid OwnerId, MemoryScope Scope, string Content, string Source, double Confidence, DateTimeOffset CreatedAt, string Origin);
public sealed class ScopedMemory
{
    private readonly List<MemoryItem> items = [];
    public MemoryItem Add(Guid ownerId, MemoryScope scope, string content, string source, double confidence, string origin = MemoryOrigin.Lived)
    {
        if (ownerId == Guid.Empty || string.IsNullOrWhiteSpace(content) || content.Length > 8192 || string.IsNullOrWhiteSpace(source) || confidence is < 0 or > 1) throw new ArgumentException("Invalid memory.");
        var item = new MemoryItem(Guid.NewGuid(), ownerId, scope, content, source, confidence, DateTimeOffset.UtcNow, MemoryOrigin.Normalize(origin)); items.Add(item); return item;
    }
    public IReadOnlyList<MemoryItem> Search(Guid requesterId, string query) => items.Where(x => (x.Scope != MemoryScope.Private || x.OwnerId == requesterId) && x.Content.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.Confidence).ToArray();

    /// <summary>
    /// Owner-only delete (§50: inspect/correct/delete). Only the creator may delete,
    /// for every scope — family members cannot wipe each other's shared memories.
    /// Returns false for unknown ids and foreign owners alike (no existence oracle).
    /// </summary>
    public bool Remove(Guid requesterId, Guid memoryId)
    {
        var index = items.FindIndex(x => x.Id == memoryId);
        if (index < 0) return false;
        if (items[index].OwnerId != requesterId) return false;
        items.RemoveAt(index);
        return true;
    }
}
