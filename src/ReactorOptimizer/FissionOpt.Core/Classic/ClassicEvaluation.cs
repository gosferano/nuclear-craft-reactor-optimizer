namespace FissionOpt.Core.Classic;

/// <summary>Mirrors <c>Fission::Evaluation</c>. Instances are reused across evaluations.</summary>
public sealed class ClassicEvaluation
{
    // Raw
    /// <summary>Tiles that contribute nothing (inactive coolers, moderators not in any cell line).
    /// Order is the order the evaluator found them: moderators first (pass 2), then coolers (pass 4).</summary>
    public List<Coord> InvalidTiles { get; } = new();
    public double PowerMult;
    public double HeatMult;
    public double Cooling;
    public int Breed;
    // Computed
    public double Heat, NetHeat, DutyCycle, AvgMult, Power, AvgPower, AvgBreed, Efficiency;

    /// <summary>Mirrors <c>Evaluation::compute</c>.</summary>
    public void Compute(ClassicSettings settings)
    {
        Heat = settings.FuelBaseHeat * HeatMult;
        NetHeat = Heat - Cooling;
        // std::min(1.0, cooling / heat) is (b < a) ? b : a, so a NaN or +inf ratio (heat == 0)
        // yields 1.0. Math.Min would propagate the NaN, so spell out the C++ semantics.
        double ratio = Cooling / Heat;
        DutyCycle = ratio < 1.0 ? ratio : 1.0;
        AvgMult = PowerMult * DutyCycle;
        Power = PowerMult * settings.FuelBasePower;
        AvgPower = Power * DutyCycle;
        AvgBreed = Breed * DutyCycle;
        Efficiency = Breed != 0 ? PowerMult / Breed : 1.0;
    }

    public void CopyFrom(ClassicEvaluation other)
    {
        InvalidTiles.Clear();
        InvalidTiles.AddRange(other.InvalidTiles);
        PowerMult = other.PowerMult;
        HeatMult = other.HeatMult;
        Cooling = other.Cooling;
        Breed = other.Breed;
        Heat = other.Heat;
        NetHeat = other.NetHeat;
        DutyCycle = other.DutyCycle;
        AvgMult = other.AvgMult;
        Power = other.Power;
        AvgPower = other.AvgPower;
        AvgBreed = other.AvgBreed;
        Efficiency = other.Efficiency;
    }
}
