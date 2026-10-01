namespace TOLIK.Core;

public sealed record PersonProfile(Guid Id, string Name, string Relationship, MemoryScope Scope);
public sealed record PersonalityProfile(string Tone, string Style, bool UseHumor);
public sealed record HumorItem(Guid OwnerId, MemoryScope Scope, string Content, string Source);
public sealed record FamilyLoreItem(Guid Id, Guid OwnerId, MemoryScope Scope, string Content, DateTimeOffset CreatedAt);
public sealed record RelationshipItem(Guid Id, Guid OwnerId, MemoryScope Scope, Guid PersonId, string Relation, DateTimeOffset CreatedAt);
public sealed record PersonalEventItem(Guid Id, Guid OwnerId, MemoryScope Scope, string Type, string Content, DateTimeOffset OccurredAt, DateTimeOffset CreatedAt);
public sealed class PersonalContext
{
    private readonly List<PersonProfile> people = [];
    private readonly List<HumorItem> humor = [];
    public PersonProfile AddPerson(Guid requester, string name, string relationship, MemoryScope scope)
    {
        if (requester == Guid.Empty || string.IsNullOrWhiteSpace(name) || name.Length > 128 || string.IsNullOrWhiteSpace(relationship)) throw new ArgumentException("Invalid person.");
        var person = new PersonProfile(Guid.NewGuid(), name, relationship, scope); people.Add(person); return person;
    }
    public HumorItem AddHumor(Guid ownerId, MemoryScope scope, string content, string source)
    {
        if (ownerId == Guid.Empty || string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Invalid humor item.");
        var item = new HumorItem(ownerId, scope, content, source); humor.Add(item); return item;
    }
    public IReadOnlyList<HumorItem> GetHumor(Guid requester) => humor.Where(x => x.Scope != MemoryScope.Private || x.OwnerId == requester).ToArray();
}
