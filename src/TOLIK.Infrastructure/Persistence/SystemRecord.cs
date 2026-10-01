namespace TOLIK.Infrastructure.Persistence;

public sealed class SystemRecord
{
    public required string Key { get; init; }

    public required string Value { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
