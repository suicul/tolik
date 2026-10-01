namespace TOLIK.Contracts;

public sealed record SpeakerEvidence(string Subject, double Confidence, string Provider, DateTimeOffset ObservedAt)
{
    public bool IsWellFormed => !string.IsNullOrWhiteSpace(Subject)
        && Confidence is >= 0 and <= 1
        && !string.IsNullOrWhiteSpace(Provider);
}

public static class IdentityAuthorization
{
    public static bool IsAuthorized(Guid authenticatedUserId, Guid requestedUserId, SpeakerEvidence? speaker)
        => authenticatedUserId != Guid.Empty && authenticatedUserId == requestedUserId;
}
