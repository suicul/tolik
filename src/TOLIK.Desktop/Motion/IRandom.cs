namespace TOLIK.Desktop.Motion;

/// <summary>
/// Minimal deterministic random source abstraction. Production uses the seeded
/// xorshift implementation; tests pin seeds to make trajectories reproducible.
/// The Cephalon must never depend on global random state.
/// </summary>
public interface IRandom
{
    /// <summary>Uniform double in [0, 1).</summary>
    double NextDouble();

    /// <summary>Uniform double in [min, max).</summary>
    double Next(double min, double max);

    /// <summary>Uniform int in [minInclusive, maxExclusive).</summary>
    int NextInt(int minInclusive, int maxExclusive);
}

/// <summary>
/// Seeded xorshift64* — small, fast, deterministic. Seed 0 is remapped to a nonzero
/// constant so a zeroed state can never lock the generator up.
/// </summary>
public sealed class XorShiftRandom : IRandom
{
    private ulong _state;

    public XorShiftRandom(ulong seed)
    {
        _state = seed != 0 ? seed : 0x9E3779B97F4A7C15UL;
    }

    public double NextDouble()
    {
        ulong x = NextULong();
        return (x >> 11) * (1.0 / 9007199254740992.0); // 2^53
    }

    public double Next(double min, double max)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max))
        {
            return 0.0;
        }
        if (max < min)
        {
            (min, max) = (max, min);
        }
        return min + (max - min) * NextDouble();
    }

    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
        {
            return minInclusive;
        }
        ulong x = NextULong();
        long range = (long)maxExclusive - minInclusive;
        return (int)(minInclusive + (long)(x % (ulong)range));
    }

    private ulong NextULong()
    {
        ulong x = _state;
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        _state = x;
        return x;
    }
}
