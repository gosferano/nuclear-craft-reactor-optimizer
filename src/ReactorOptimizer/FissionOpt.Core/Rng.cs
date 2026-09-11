namespace FissionOpt.Core;

/// <summary>
/// Seeded RNG for the optimizers. Wraps <see cref="Random"/> (the port does not need to match
/// <c>std::mt19937</c> streams, only to be reproducible for a given seed within this application).
/// </summary>
public sealed class Rng
{
    private readonly Random _r;
    public int Seed { get; }

    public Rng(int seed)
    {
        Seed = seed;
        _r = new Random(seed);
    }

    /// <summary>Uniform integer in [0, maxInclusive], like <c>std::uniform_int_distribution&lt;&gt;(0, max)</c>.</summary>
    public int NextInt(int maxInclusive) => _r.Next(maxInclusive + 1);

    /// <summary>Uniform double in [0, 1).</summary>
    public double NextDouble() => _r.NextDouble();

    /// <summary>Normal deviate via Marsaglia's polar method.</summary>
    public double NextGaussian(double mean, double stddev)
    {
        double u, v, s;
        do
        {
            u = 2.0 * _r.NextDouble() - 1.0;
            v = 2.0 * _r.NextDouble() - 1.0;
            s = u * u + v * v;
        } while (s >= 1.0 || s == 0.0);
        return mean + stddev * u * Math.Sqrt(-2.0 * Math.Log(s) / s);
    }

    /// <summary>Fisher–Yates shuffle, like <c>std::shuffle</c>.</summary>
    public void Shuffle<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; --i)
        {
            int j = _r.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
