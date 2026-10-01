namespace TOLIK.Core;

/// <summary>
/// Speaker accept/reject decision (§13: device ≠ user ≠ speaker).
/// Pure math — voice service extracts embeddings, storage lives in memory
/// items, and this helper only compares. Threshold calibrated live
/// (CampPlus): same speaker ≈0.49–1.0 (short clips score lower),
/// different voice ≈0.2–0.3. Enforcement is soft (guest-mode prompt, device
/// auth still gates everything), so the threshold favors availability;
/// recalibrate with real household voices when they exist.
/// </summary>
public static class SpeakerVerifier
{
    /// <summary>Minimum cosine similarity to accept a claimed speaker.</summary>
    public const double AcceptThreshold = 0.45;

    public static double Cosine(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        if (a is null || b is null || a.Count == 0 || a.Count != b.Count)
            throw new ArgumentException("Embeddings must be non-empty and same length.");
        var dot = 0.0;
        var na = 0.0;
        var nb = 0.0;
        for (var i = 0; i < a.Count; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        if (na <= 0.0 || nb <= 0.0) throw new ArgumentException("Zero-norm embedding.");
        return Math.Clamp(dot / (Math.Sqrt(na) * Math.Sqrt(nb)), -1.0, 1.0);
    }

    public static bool Accept(double similarity) => similarity >= AcceptThreshold;
}
