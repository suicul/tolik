using System.Text.Json;

namespace TOLIK.Core;

/// <summary>One adaptive layer sample (docs/voice-identity.md §2): a confirmed
/// high-confidence embedding with aging metadata. Never touches the canonical anchors.</summary>
public sealed record AdaptiveSample(IReadOnlyList<double> Vector, DateTimeOffset AddedAt, double Weight = 1.0);

/// <summary>Identity confidence tier (docs/voice-identity.md §3, VI-0 calibrated).</summary>
public enum SpeakerConfidenceTier { High, Medium, Low }

/// <summary>
/// Speaker identity profile v2 (docs/voice-identity.md): immutable canonical enrollment
/// anchors + a bounded, aging adaptive layer. Pure deterministic logic — storage lives
/// in the existing durable-memory "speaker.profile" items, embeddings come from the
/// voice service; this type owns blend/compare/decay decisions. Anti-poisoning by
/// construction: adaptive samples cap out (LRU-decay) and never overwrite the anchor,
/// so a reset restores the pristine enrollment. v1 profiles (a bare embedding array)
/// load as canonical-only — backward compatible; upgrade happens on re-enrollment.
/// Thresholds VI-0-calibrated on 329 real dataset clips (see voice-identity.md §3).
/// </summary>
public sealed class SpeakerProfile
{
    public const int AdaptiveSampleCap = 8;
    public static readonly TimeSpan AdaptiveSampleTtl = TimeSpan.FromDays(30);
    /// <summary>Canonical anchor weight in the blend (VI-0 start; refine by simulation).</summary>
    public const double CanonicalAnchorWeight = 0.7;
    /// <summary>High-confidence bar (VI-0: ~p25-to-mean; all measured foreign voices below).</summary>
    public const double AdaptiveTier = 0.60;
    public const double AcceptTier = SpeakerVerifier.AcceptThreshold; // 0.45, existing soft accept
    /// <summary>Minimum speech duration (ms) for an adaptive candidate (VI-0).</summary>
    public const int MinAdaptiveQualitySpeechMs = 2500;

    public IReadOnlyList<IReadOnlyList<double>> Canonical { get; }
    public IReadOnlyList<AdaptiveSample> Adaptive { get; }
    public string Model { get; }
    public DateTimeOffset EnrolledAt { get; }

    private readonly double[] _canonicalMean;
    private readonly double[]? _blendedMean;

    public SpeakerProfile(
        IReadOnlyList<IReadOnlyList<double>> canonical,
        IReadOnlyList<AdaptiveSample>? adaptive = null,
        string model = "campplus",
        DateTimeOffset? enrolledAt = null)
    {
        if (canonical is null || canonical.Count == 0) throw new ArgumentException("At least one canonical anchor is required.", nameof(canonical));
        var dim = canonical[0].Count;
        if (dim == 0 || canonical.Any(v => v.Count != dim)) throw new ArgumentException("Canonical anchors must be non-empty and same length.", nameof(canonical));
        Canonical = canonical;
        Adaptive = adaptive ?? [];
        Model = model;
        EnrolledAt = enrolledAt ?? DateTimeOffset.UtcNow;
        _canonicalMean = UnitMean(canonical);
        _blendedMean = ComputeBlendedMean(now: null);
    }

    private SpeakerProfile(SpeakerProfile source, IReadOnlyList<AdaptiveSample> adaptive)
        : this(source.Canonical, adaptive, source.Model, source.EnrolledAt)
    {
    }

    public IReadOnlyList<double> CanonicalMean => _canonicalMean;
    public IReadOnlyList<double> BlendedMean => _blendedMean ?? _canonicalMean;

    /// <summary>Turn similarity: max over the canonical anchor and the adaptive blend —
    /// the anchor never stops recognizing the owner (docs/voice-identity.md §2).</summary>
    public double Similarity(IReadOnlyList<double> embedding) =>
        Math.Max(
            SpeakerVerifier.Cosine(embedding, _canonicalMean),
            _blendedMean is null ? 0.0 : SpeakerVerifier.Cosine(embedding, _blendedMean));

    /// <summary>Identity tier by similarity alone (docs/voice-identity.md §3).</summary>
    public static SpeakerConfidenceTier Tier(double similarity) =>
        similarity >= AdaptiveTier ? SpeakerConfidenceTier.High
        : similarity >= AcceptTier ? SpeakerConfidenceTier.Medium
        : SpeakerConfidenceTier.Low;

    /// <summary>Adaptive learning is allowed ONLY for high-confidence, good-quality
    /// turns (owner requirement #3/#12): medium/low NEVER train anything.</summary>
    public static bool IsAdaptiveCandidate(double similarity, int qualitySpeechMs) =>
        Tier(similarity) == SpeakerConfidenceTier.High && qualitySpeechMs >= MinAdaptiveQualitySpeechMs;

    /// <summary>
    /// Adds a confirmed adaptive sample: bounded (cap + LRU-decay eviction of the
    /// lowest weight×age), the canonical anchors untouched (poisoning containment:
    /// even a chain of wrong high-confidence accepts drifts the blend only).
    /// </summary>
    public SpeakerProfile AddAdaptive(IReadOnlyList<double> embedding, DateTimeOffset addedAt)
    {
        if (embedding.Count != Canonical[0].Count) throw new ArgumentException("Embedding dimension mismatch.", nameof(embedding));
        var list = new List<AdaptiveSample>(Adaptive) { new(embedding, addedAt) };
        while (list.Count > AdaptiveSampleCap)
        {
            var oldest = list[0];
            for (var i = 1; i < list.Count; i++)
                if (list[i].AddedAt < oldest.AddedAt) oldest = list[i];
            list.Remove(oldest);
        }
        return new SpeakerProfile(this, list);
    }

    /// <summary>Drops the adaptive layer entirely — the canonical enrollment survives (recovery).</summary>
    public SpeakerProfile ResetAdaptive() => new(this, []);

    private double[]? ComputeBlendedMean(DateTimeOffset? now)
    {
        if (Adaptive.Count == 0) return null;
        var reference = now ?? DateTimeOffset.UtcNow;
        // Only un-expired samples take part (docs: aging; expired ones linger in history
        // until LRU eviction but stop pulling the blend).
        var active = Adaptive.Where(s => reference - s.AddedAt < AdaptiveSampleTtl).ToList();
        if (active.Count == 0) return null;
        var weight = 1.0 - CanonicalAnchorWeight;
        var dim = _canonicalMean.Length;
        var blend = new double[dim];
        for (var i = 0; i < dim; i++)
        {
            var adaptiveSum = 0.0;
            var totalWeight = 0.0;
            foreach (var sample in active)
            {
                var w = sample.Weight;
                adaptiveSum += sample.Vector[i] * w;
                totalWeight += w;
            }
            blend[i] = _canonicalMean[i] * CanonicalAnchorWeight
                + (adaptiveSum / totalWeight) * weight;
        }
        var norm = Math.Sqrt(blend.Sum(x => x * x));
        return norm <= 0.0 ? null : blend.Select(x => x / norm).ToArray();
    }

    private static double[] UnitMean(IReadOnlyList<IReadOnlyList<double>> vectors)
    {
        var dim = vectors[0].Count;
        var mean = new double[dim];
        foreach (var vector in vectors)
            for (var i = 0; i < dim; i++)
                mean[i] += vector[i] / vectors.Count;
        var norm = Math.Sqrt(mean.Sum(x => x * x));
        if (norm <= 0.0) throw new ArgumentException("Zero-norm canonical mean.");
        return mean.Select(x => x / norm).ToArray();
    }

    /// <summary>Recomputes the blend against a reference time (tests: aging).</summary>
    public IReadOnlyList<double> BlendedMeanAt(DateTimeOffset now)
    {
        var blended = new SpeakerProfile(Canonical, Adaptive, Model, EnrolledAt, blendNow: now).BlendedMean;
        return blended;
    }

    private SpeakerProfile(
        IReadOnlyList<IReadOnlyList<double>> canonical,
        IReadOnlyList<AdaptiveSample> adaptive,
        string model,
        DateTimeOffset enrolledAt,
        DateTimeOffset? blendNow) : this(canonical, adaptive, model, enrolledAt)
    {
        _blendedMean = ComputeBlendedMean(blendNow); // recompute for the test time reference
    }

    /// <summary>Serializes v2 (canonical + adaptive). The storage contract of the
    /// existing "speaker.profile" memory items, extended — v1 readers unaffected.</summary>
    public string ToJson() => JsonSerializer.Serialize(new
    {
        version = 2,
        model = Model,
        canonical = Canonical.Select(v => v.ToArray()).ToArray(),
        adaptive = Adaptive.Select(s => new { vector = s.Vector.ToArray(), addedAt = s.AddedAt, weight = s.Weight }).ToArray(),
        enrolledAt = EnrolledAt
    });

    /// <summary>
    /// Parses the stored profile: v2 when version=2, v1 ({"embedding": [...]}) loads as a
    /// canonical-single profile — backward compatible with every existing enrollment.
    /// Null when the payload is not a usable profile (caller keeps "verification disabled").
    /// </summary>
    public static SpeakerProfile? TryParse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        try
        {
            using var doc = JsonDocument.Parse(stored);
            var root = doc.RootElement;
            if (root.TryGetProperty("version", out var version) && version.GetInt32() == 2)
            {
                var canonical = root.GetProperty("canonical").EnumerateArray()
                    .Select(a => a.EnumerateArray().Select(v => v.GetDouble()).ToArray())
                    .Cast<IReadOnlyList<double>>().ToList();
                var adaptive = new List<AdaptiveSample>();
                if (root.TryGetProperty("adaptive", out var adaptiveEl))
                    foreach (var s in adaptiveEl.EnumerateArray())
                        adaptive.Add(new AdaptiveSample(
                            s.GetProperty("vector").EnumerateArray().Select(v => v.GetDouble()).ToArray(),
                            s.GetProperty("addedAt").GetDateTimeOffset(),
                            s.TryGetProperty("weight", out var w) ? w.GetDouble() : 1.0));
                return new SpeakerProfile(canonical, adaptive,
                    root.TryGetProperty("model", out var m) ? m.GetString() ?? "campplus" : "campplus",
                    root.TryGetProperty("enrolledAt", out var e) ? e.GetDateTimeOffset() : null);
            }
            if (root.TryGetProperty("embedding", out var embeddingEl))
            {
                var embedding = embeddingEl.EnumerateArray().Select(v => v.GetDouble()).ToArray();
                if (embedding.Length == 0) return null;
                return new SpeakerProfile([embedding],
                    null,
                    root.TryGetProperty("model", out var m) ? m.GetString() ?? "campplus" : "campplus",
                    root.TryGetProperty("enrolledAt", out var e) ? e.GetDateTimeOffset() : null);
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
