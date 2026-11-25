using System.Security.Cryptography;
using System.Text;

namespace ModelBench.Core.Generation;

/// <summary>
/// xoshiro256** seeded via SplitMix64. Used instead of System.Random so generated instances are
/// reproducible across .NET versions (ADR 0004). Never use System.Random in generators.
/// </summary>
public sealed class Rng
{
    private ulong _s0, _s1, _s2, _s3;

    public Rng(ulong seed)
    {
        _s0 = SplitMix(ref seed); _s1 = SplitMix(ref seed); _s2 = SplitMix(ref seed); _s3 = SplitMix(ref seed);
    }

    public Rng(long seed) : this(unchecked((ulong)seed)) { }

    /// <summary>Derives a task seed: first 8 bytes of SHA256("masterSeed|familyId|familyVersion|index").</summary>
    public static long DeriveSeed(long masterSeed, string familyId, int familyVersion, int index)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{masterSeed}|{familyId}|{familyVersion}|{index}"));
        return BitConverter.ToInt64(bytes, 0);
    }

    private static ulong SplitMix(ref ulong x)
    {
        ulong z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public ulong NextULong()
    {
        ulong result = ulong.RotateLeft(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;
        _s2 ^= _s0; _s3 ^= _s1; _s1 ^= _s2; _s0 ^= _s3; _s2 ^= t;
        _s3 = ulong.RotateLeft(_s3, 45);
        return result;
    }

    /// <summary>Unbiased integer in [0, maxExclusive) (Lemire multiply with rejection).</summary>
    public int Next(int maxExclusive)
    {
        if (maxExclusive <= 0) throw new ArgumentOutOfRangeException(nameof(maxExclusive));
        ulong bound = (ulong)maxExclusive;
        ulong threshold = (0 - bound) % bound;
        while (true)
        {
            ulong hi = Math.BigMul(NextULong(), bound, out ulong lo);
            if (lo >= threshold) return (int)hi;
        }
    }

    /// <summary>Integer in [minInclusive, maxInclusive].</summary>
    public int Range(int minInclusive, int maxInclusive) => minInclusive + Next(maxInclusive - minInclusive + 1);

    public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    public bool Chance(double p) => NextDouble() < p;

    public T Pick<T>(IReadOnlyList<T> items) => items[Next(items.Count)];

    public void Shuffle<T>(IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
