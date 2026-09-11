using System.Diagnostics;
using System.Globalization;
using FissionOpt.Core;
using FissionOpt.Core.Overhaul;
using FissionOpt.Core.Presets;
using static FissionOpt.Core.Overhaul.OverhaulTiles;

namespace FissionOpt.Tui.Headless;

/// <summary>Headless overhaul optimization.</summary>
public static class OverhaulHeadless
{
    public const string Usage = """
        Usage: FissionOpt.Tui headless --mode overhaul [options]
          --size WxHxD            interior size as internal X x Y x Z (default 7x7x7)
          --fuel TYPE:NAME[:LIMIT]  fuel preset, TYPE is OX|NI|ZA, e.g. OX:LEU-235 or ZA:HEU-235:8. Repeatable.
                                  (default OX:LEU-235)
          --manual-fuel NAME,EFF%,HEAT,CRIT[,self][,LIMIT]  a fuel that is not in the presets. Repeatable.
          --source-limits A,B,C   max Cf-252, Po-Be, Ra-Be sources; blank/negative = unlimited (default: unlimited,0,0)
          --limit LABEL=N         max count for a block label (Wt Fe Rs ... Cr ## == -- =) -) <> >< []). Repeatable.
          --goal output|fuel|efficiency|irradiation   (default output)
          --controllable          only generate reactors that shut down with shields on
          --simd-net on|off       Vector256 kernels in the value net (default on; off = original scalar loops)
          --sym x,y,z | --sym none          mirror symmetries (default x,y,z)
          --seed N                RNG seed (default 0)
          --seconds S             time budget (default 60)
          --steps N               step budget instead of a time budget
          --out FILE              write overhaul planner JSON
          --quiet                 only print the final design
        """;

    public static int Run(string[] args)
    {
        string size = "7x7x7", goal = "output", sym = "x,y,z", sourceLimits = ",0,0";
        var fuels = new List<OverhaulFuel>();
        var limits = new List<(string label, int n)>();
        bool controllable = false, quiet = false, simdNet = true;
        int seed = 0; double seconds = 60; long? steps = null; string? outFile = null;

        for (int i = 0; i < args.Length; ++i)
        {
            string a = args[i];
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{a} needs a value");
            switch (a)
            {
                case "--mode": Next(); break;
                case "--size": size = Next(); break;
                case "--fuel":
                {
                    var parts = Next().Split(':');
                    if (parts.Length < 2) throw new ArgumentException("--fuel expects TYPE:NAME[:LIMIT]");
                    var preset = OverhaulPresets.Find(parts[0], parts[1]) ?? throw new ArgumentException($"no overhaul fuel preset {parts[0]} {parts[1]}");
                    int limit = parts.Length > 2 ? int.Parse(parts[2], CultureInfo.InvariantCulture) : -1;
                    fuels.Add(new OverhaulFuel(preset.EfficiencyPercent / 100, limit, preset.Criticality, preset.Heat, preset.SelfPriming, preset.Name));
                    break;
                }
                case "--manual-fuel":
                {
                    var p = Next().Split(',');
                    if (p.Length < 4) throw new ArgumentException("--manual-fuel expects NAME,EFF%,HEAT,CRIT[,self][,LIMIT]");
                    bool self = p.Length > 4 && p[4].Equals("self", StringComparison.OrdinalIgnoreCase);
                    int limit = p.Length > (self ? 5 : 4) ? int.Parse(p[self ? 5 : 4], CultureInfo.InvariantCulture) : -1;
                    fuels.Add(new OverhaulFuel(ParseDouble(p[1]) / 100, limit, int.Parse(p[3], CultureInfo.InvariantCulture), int.Parse(p[2], CultureInfo.InvariantCulture), self, p[0]));
                    break;
                }
                case "--source-limits": sourceLimits = Next(); break;
                case "--limit":
                {
                    var kv = Next().Split('=');
                    if (kv.Length != 2) throw new ArgumentException("--limit expects LABEL=N");
                    limits.Add((kv[0], int.Parse(kv[1], CultureInfo.InvariantCulture)));
                    break;
                }
                case "--goal": goal = Next(); break;
                case "--controllable": controllable = true; break;
                case "--simd-net":
                    simdNet = Next().ToLowerInvariant() switch { "on" => true, "off" => false, var v => throw new ArgumentException("--simd-net expects on|off, got " + v) };
                    break;
                case "--sym": sym = Next(); break;
                case "--seed": seed = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--seconds": seconds = ParseDouble(Next()); break;
                case "--steps": steps = long.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--out": outFile = Next(); break;
                case "--quiet": quiet = true; break;
                case "-h": case "--help": Console.WriteLine(Usage); return 0;
                default: throw new ArgumentException("unknown option " + a);
            }
        }
        if (fuels.Count == 0)
        {
            var p = OverhaulPresets.Find("OX", "LEU-235")!;
            fuels.Add(new OverhaulFuel(p.EfficiencyPercent / 100, -1, p.Criticality, p.Heat, p.SelfPriming, p.Name));
        }

        var dims = size.ToLowerInvariant().Split('x');
        if (dims.Length != 3) throw new ArgumentException("--size expects WxHxD");
        var settings = new OverhaulSettings
        {
            SizeX = int.Parse(dims[0], CultureInfo.InvariantCulture),
            SizeY = int.Parse(dims[1], CultureInfo.InvariantCulture),
            SizeZ = int.Parse(dims[2], CultureInfo.InvariantCulture),
            Controllable = controllable,
            Goal = goal.ToLowerInvariant() switch
            {
                "output" => OverhaulGoal.Output,
                "fuel" or "fueluse" or "breeder" => OverhaulGoal.FuelUse,
                "efficiency" => OverhaulGoal.Efficiency,
                "irradiation" => OverhaulGoal.Irradiation,
                _ => throw new ArgumentException("unknown goal " + goal),
            },
        };
        settings.Fuels.AddRange(fuels);
        foreach (var s in sym.ToLowerInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (s == "x") settings.SymX = true;
            else if (s == "y") settings.SymY = true;
            else if (s == "z") settings.SymZ = true;
            else if (s != "none") throw new ArgumentException("unknown symmetry " + s);
        }
        Array.Fill(settings.Limits, -1);
        foreach (var (label, count) in limits)
            settings.Limits[ParseTileLabel(label)] = count;
        var sl = sourceLimits.Split(',');
        for (int i = 0; i < 3; ++i)
            settings.SourceLimits[i] = i < sl.Length && int.TryParse(sl[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : -1;

        Console.WriteLine($"seed={seed} mode=overhaul size={settings.SizeX}x{settings.SizeY}x{settings.SizeZ} fuels={string.Join(",", fuels.Select(f => f.Name))} goal={settings.Goal} sym={sym} controllable={controllable} sourceLimits={string.Join(",", settings.SourceLimits)}");

        var opt = new OverhaulOpt(settings, seed, simdNet);
        Console.WriteLine($"simd net: {opt.SimdNet}");
        var sw = Stopwatch.StartNew();
        long n = 0;
        while (steps.HasValue ? n < steps.Value : sw.Elapsed.TotalSeconds < seconds)
        {
            opt.Step();
            ++n;
            if (!quiet && opt.NeedsRedrawBest())
            {
                var v = opt.Best.Value;
                Console.WriteLine($"[{sw.Elapsed.TotalSeconds,7:F1}s] {StageText(opt)}: output={v.Output / 16:F1} mB/t eff={v.Efficiency * 100:F1}% cells={v.NActiveCells} irr={v.IrradiatorFlux}");
            }
        }
        sw.Stop();
        Console.WriteLine($"ran {n} steps in {sw.Elapsed.TotalSeconds:F1}s ({n / sw.Elapsed.TotalSeconds:F0} steps/s); final {StageText(opt)}");
        Console.WriteLine();
        Console.Write(OverhaulExport.RenderMetrics(opt.Best.Value));
        Console.WriteLine();
        Console.Write(OverhaulExport.RenderLayers(settings, opt.Best.State));
        Console.WriteLine();
        Console.Write(OverhaulExport.RenderBlockCounts(settings, opt.Best.State));
        if (outFile != null)
        {
            File.WriteAllText(outFile, OverhaulExport.ToPlannerJson(settings, opt.Best.State));
            Console.WriteLine($"wrote {outFile}");
        }
        return 0;
    }

    public static string StageText(OverhaulOpt opt) => opt.NStage switch
    {
        OverhaulOpt.StageTrain => $"episode {opt.NEpisode}, training iteration {opt.NIteration}",
        OverhaulOpt.StageInfer => $"episode {opt.NEpisode}, inference iteration {opt.NIteration}",
        _ => $"episode {opt.NEpisode}, rollout iteration {opt.NIteration}",
    };

    private static double ParseDouble(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    public static int ParseTileLabel(string label)
    {
        int idx = Array.FindIndex(OverhaulPresets.TileNames, n => string.Equals(n, label, StringComparison.OrdinalIgnoreCase));
        if (idx < 0 || idx >= Air) throw new ArgumentException("unknown tile label " + label);
        return idx;
    }
}
