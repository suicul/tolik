using System.Threading.Channels;
using TOLIK.Contracts;
using TOLIK.Core;
using TOLIK.Infrastructure.Auditing;
using TOLIK.Infrastructure.Persistence;

namespace TOLIK.Core.Host;

/// <summary>One confirmed adaptive-learning candidate (docs/voice-identity.md VI-2):
/// produced by a HIGH-tier + quality turn, consumed asynchronously by the adaptation
/// service. Never carries text — an embedding and metadata only (SECURITY §38).</summary>
public sealed record AdaptiveCandidate(
    Guid UserId,
    IReadOnlyList<double> Embedding,
    int QualitySpeechMs,
    double Similarity,
    DateTimeOffset At);

/// <summary>
/// Bounded non-blocking channel for adaptive candidates: a turn NEVER awaits learning
/// (owner requirement #10/#19 — realtime first). Drop-oldest under pressure — a lost
/// candidate costs nothing, the voice pipeline stays responsive.
/// </summary>
public sealed class VoiceAdaptationChannel
{
    private readonly Channel<AdaptiveCandidate> _channel =
        Channel.CreateBounded<AdaptiveCandidate>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });

    public bool TryEnqueue(AdaptiveCandidate candidate) => _channel.Writer.TryWrite(candidate);

    public ChannelReader<AdaptiveCandidate> Reader => _channel.Reader;
}

/// <summary>
/// Adaptive speaker learning (docs/voice-identity.md VI-2): consumes confirmed
/// candidates OUTSIDE the voice turn path. Only profiles that ALREADY exist get
/// updated — a turn never creates a profile (enrollment flows are the only creators);
/// unknown voices can never bootstrap themselves into an identity. The update path is
/// the existing durable-memory item ("speaker.profile") + audit; embeddings only,
/// raw audio never persists. Anti-poisoning is structural: SpeakerProfile bounds the
/// adaptive layer (cap/TTL) and the canonical anchors stay untouched.
/// </summary>
public sealed class VoiceAdaptationService(
    VoiceAdaptationChannel channel,
    IServiceProvider services,
    ILogger<VoiceAdaptationService> logger) : BackgroundService
{
    public const string ProfileSource = "speaker.profile";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var candidate in channel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    // AuditWriter/DurableMemoryStore are scoped services — a scope per
                    // candidate keeps the singleton hosted service valid.
                    using var scope = services.CreateScope();
                    await AdaptAsync(
                        candidate,
                        scope.ServiceProvider.GetRequiredService<TOLIK.Infrastructure.Persistence.DurableMemoryStore>(),
                        scope.ServiceProvider.GetRequiredService<TOLIK.Infrastructure.Auditing.AuditWriter>(),
                        scope.ServiceProvider.GetRequiredService<VoiceAdaptationPolicy>(),
                        stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // A failed update costs one candidate — the pipeline continues.
                    logger.LogWarning("Adaptive update skipped: {Message}", ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
    }

    private async Task AdaptAsync(
        AdaptiveCandidate candidate,
        TOLIK.Infrastructure.Persistence.DurableMemoryStore durableMemory,
        TOLIK.Infrastructure.Auditing.AuditWriter audit,
        VoiceAdaptationPolicy policy,
        CancellationToken cancellationToken)
    {
        if (!policy.Enabled)
        {
            // Global learning toggle (VI-5): the turn path already refuses to enqueue,
            // this is the second gate (defense in depth) — the candidate is dropped.
            logger.LogInformation("Adaptive candidate for {UserId} dropped: adaptation disabled.", candidate.UserId);
            return;
        }
        var profiles = await durableMemory.SearchBySourceAsync(candidate.UserId, ProfileSource, cancellationToken);
        if (profiles.Count == 0)
        {
            // No profile exists — a turn NEVER creates one: enrollment flows only.
            logger.LogInformation("Adaptive candidate for {UserId} dropped: no profile (enrollment is the only creator).", candidate.UserId);
            return;
        }
        var profile = SpeakerProfile.TryParse(profiles[0].Content);
        if (profile is null)
        {
            logger.LogInformation("Adaptive candidate for {UserId} dropped: stored profile unreadable.", candidate.UserId);
            return;
        }
        var updated = profile.AddAdaptive(candidate.Embedding, candidate.At);
        await durableMemory.DeleteAsync(candidate.UserId, profiles[0].Id, cancellationToken);
        await durableMemory.AddAsync(candidate.UserId, MemoryScope.Private, updated.ToJson(), ProfileSource, 1.0, cancellationToken);
        // Audited WITHOUT the embedding content (§38): counts and similarity only.
        await audit.WriteAsync(
            Guid.NewGuid(),
            "voice.speaker.adaptive",
            "ALLOW",
            $"user:{candidate.UserId:D};adaptive_count:{updated.Adaptive.Count};similarity:{candidate.Similarity:0.00};speech_ms:{candidate.QualitySpeechMs}",
            cancellationToken);
        logger.LogInformation("Adaptive profile updated for {UserId}: {Count} samples (similarity {Similarity:0.00}).",
            candidate.UserId, updated.Adaptive.Count, candidate.Similarity);
    }
}
