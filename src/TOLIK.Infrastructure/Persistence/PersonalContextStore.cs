using System.Text.Json;
using TOLIK.Core;

namespace TOLIK.Infrastructure.Persistence;

public sealed class PersonalContextStore(DurableMemoryStore memoryStore)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PersonProfile> AddPersonAsync(
        Guid ownerId,
        string name,
        string relationship,
        MemoryScope scope,
        CancellationToken cancellationToken = default)
    {
        var person = new PersonProfile(Guid.NewGuid(), name, relationship, scope);
        Validate(ownerId, person);
        await memoryStore.AddAsync(ownerId, scope, JsonSerializer.Serialize(person, JsonOptions), "people.profile", 1, cancellationToken);
        return person;
    }

    public async Task<IReadOnlyList<PersonProfile>> GetPeopleAsync(
        Guid requesterId,
        CancellationToken cancellationToken = default)
    {
        var items = await memoryStore.SearchBySourceAsync(requesterId, "people.profile", cancellationToken);
        return items.Select(TryDeserialize<PersonProfile>).Where(x => x is not null).Select(x => x!).ToArray();
    }

    public async Task<HumorItem> AddHumorAsync(
        Guid ownerId,
        MemoryScope scope,
        string content,
        string source,
        CancellationToken cancellationToken = default)
    {
        var item = new HumorItem(ownerId, scope, content, source);
        Validate(ownerId, item);
        await memoryStore.AddAsync(ownerId, scope, JsonSerializer.Serialize(item, JsonOptions), "humor.item", 1, cancellationToken);
        return item;
    }

    public async Task<IReadOnlyList<HumorItem>> GetHumorAsync(
        Guid requesterId,
        CancellationToken cancellationToken = default)
    {
        var items = await memoryStore.SearchBySourceAsync(requesterId, "humor.item", cancellationToken);
        return items.Select(TryDeserialize<HumorItem>).Where(x => x is not null).Select(x => x!).ToArray();
    }

    public async Task<PersonalityProfile> SetPersonalityAsync(
        Guid ownerId,
        string tone,
        string style,
        bool useHumor,
        CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty || string.IsNullOrWhiteSpace(tone) || tone.Length > 128 ||
            string.IsNullOrWhiteSpace(style) || style.Length > 512)
            throw new ArgumentException("Invalid personality.");

        var profile = new PersonalityProfile(tone, style, useHumor);
        await memoryStore.AddAsync(ownerId, MemoryScope.Private, JsonSerializer.Serialize(profile, JsonOptions), "personality.profile", 1, cancellationToken);
        return profile;
    }

    public async Task<PersonalityProfile?> GetPersonalityAsync(
        Guid requesterId,
        CancellationToken cancellationToken = default)
    {
        var items = await memoryStore.SearchBySourceAsync(requesterId, "personality.profile", cancellationToken);
        return items.Select(TryDeserialize<PersonalityProfile>).FirstOrDefault(x => x is not null);
    }

    public async Task<FamilyLoreItem> AddFamilyLoreAsync(
        Guid ownerId,
        string content,
        CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty || string.IsNullOrWhiteSpace(content) || content.Length > 8192)
            throw new ArgumentException("Invalid family lore.");

        var item = new FamilyLoreItem(Guid.NewGuid(), ownerId, MemoryScope.Shared, content, DateTimeOffset.UtcNow);
        await memoryStore.AddAsync(ownerId, item.Scope, JsonSerializer.Serialize(item, JsonOptions), "family.lore", 1, cancellationToken);
        return item;
    }

    public async Task<IReadOnlyList<FamilyLoreItem>> GetFamilyLoreAsync(
        Guid requesterId,
        CancellationToken cancellationToken = default)
    {
        var items = await memoryStore.SearchBySourceAsync(requesterId, "family.lore", cancellationToken);
        return items.Select(TryDeserialize<FamilyLoreItem>).Where(x => x is not null).Select(x => x!).ToArray();
    }

    public async Task<RelationshipItem> AddRelationshipAsync(
        Guid ownerId,
        MemoryScope scope,
        Guid personId,
        string relation,
        CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty || personId == Guid.Empty || string.IsNullOrWhiteSpace(relation) || relation.Length > 256)
            throw new ArgumentException("Invalid relationship.");

        var item = new RelationshipItem(Guid.NewGuid(), ownerId, scope, personId, relation, DateTimeOffset.UtcNow);
        await memoryStore.AddAsync(ownerId, scope, JsonSerializer.Serialize(item, JsonOptions), "relationship.item", 1, cancellationToken);
        return item;
    }

    public async Task<IReadOnlyList<RelationshipItem>> GetRelationshipsAsync(
        Guid requesterId,
        CancellationToken cancellationToken = default)
    {
        var items = await memoryStore.SearchBySourceAsync(requesterId, "relationship.item", cancellationToken);
        return items.Select(TryDeserialize<RelationshipItem>).Where(x => x is not null).Select(x => x!).ToArray();
    }

    public async Task<PersonalEventItem> AddEventAsync(
        Guid ownerId,
        MemoryScope scope,
        string type,
        string content,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty || string.IsNullOrWhiteSpace(type) || type.Length > 128 ||
            string.IsNullOrWhiteSpace(content) || content.Length > 8192)
            throw new ArgumentException("Invalid personal event.");

        var item = new PersonalEventItem(Guid.NewGuid(), ownerId, scope, type, content, occurredAt, DateTimeOffset.UtcNow);
        await memoryStore.AddAsync(ownerId, scope, JsonSerializer.Serialize(item, JsonOptions), "personal.event", 1, cancellationToken);
        return item;
    }

    public async Task<IReadOnlyList<PersonalEventItem>> GetEventsAsync(
        Guid requesterId,
        CancellationToken cancellationToken = default)
    {
        var items = await memoryStore.SearchBySourceAsync(requesterId, "personal.event", cancellationToken);
        return items.Select(TryDeserialize<PersonalEventItem>).Where(x => x is not null).Select(x => x!).OrderByDescending(x => x!.OccurredAt).ToArray();
    }

    private static T? TryDeserialize<T>(MemoryItem item) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(item.Content, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static void Validate(Guid ownerId, PersonProfile person)
    {
        if (ownerId == Guid.Empty || string.IsNullOrWhiteSpace(person.Name) || person.Name.Length > 128 ||
            string.IsNullOrWhiteSpace(person.Relationship) || person.Relationship.Length > 256)
            throw new ArgumentException("Invalid person.");
    }

    private static void Validate(Guid ownerId, HumorItem item)
    {
        if (ownerId == Guid.Empty || string.IsNullOrWhiteSpace(item.Content) || item.Content.Length > 8192 ||
            string.IsNullOrWhiteSpace(item.Source) || item.Source.Length > 256)
            throw new ArgumentException("Invalid humor item.");
    }
}
