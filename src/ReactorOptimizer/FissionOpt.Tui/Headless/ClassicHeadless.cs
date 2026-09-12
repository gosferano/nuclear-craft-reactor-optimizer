using System.Diagnostics;
using System.Globalization;
using FissionOpt.Core;
using FissionOpt.Core.Classic;
using FissionOpt.Core.Presets;
using static FissionOpt.Core.Classic.ClassicTiles;

namespace FissionOpt.Tui.Headless;

/// <summary>Headless classic optimization: parse flags, run for a time/step budget, print the best design.</summary>
public static class ClassicHeadless
{
    public const string Usage = """
        Usage: FissionOpt.Tui headless [--mode classic] [options]   (see `--mode overhaul --help` for the overhaul optimizer)
          --size WxHxD            interior size as internal X x Y x Z (default 5x5x5)
          --fuel NAME             fuel preset name, e.g. LEU-235 (default LEU-235)
          --variant Normal|Oxide  fuel variant (default Normal)
          --config Default|E2E|PO3  modpack config for fuel and cooling presets (default Default)
          --power P --heat H      manual fuel base power / heat (overrides --fuel)
          --rates Default|E2E|PO3 cooling-rate preset (default: same as --config)
          --limit LABEL=N         max count for a block (LABEL: Wt Rs Qz Au Gs Lp Dm He Ed Cr Fe Em Cu Sn Mg [] ##,
                                  prefix 'a' for active coolers e.g. aWt). Repeatable. Default: unlimited, active = 0.
          --goal power|breeder|efficiency   (default power)
          --sym x,y,z | --sym none          mirror symmetries (default x,y,z)
          --no-net                disable the value network
          --simd-net on|off       Vector256 kernels in the value net (default on; off = original scalar loops)
          --restart auto|random|tiled  restart episodes from a random grid (upstream) or from tiled unit designs
                                  optimized first on small boxes (auto: tiled for grids of >= 2000 tiles)
          --unit-sizes A,B,...    candidate unit sizes for --restart tiled (default 4,5,6,7,8)
          --unit-steps N          steps spent optimizing each unit design (default 200000)
          --tile-noise F          fraction of tiles randomized on each tiled restart (default 0.02)
          --adopt on|off          also add crops of converged designs to the unit pool (default off; measured slightly worse)
          --incremental auto|on|off  evaluate mutations incrementally (auto: on unless active coolers + accessibility)
          --parallel auto|on|off  evaluate the 4 children of each step on separate threads (auto: on for >= 300 tiles;
                                  only used when incremental evaluation is off)
          --no-heat-neutral       allow heat-positive designs
          --no-accessible         don't require active coolers to be reachable from the casing
          --seed N                RNG seed (default 0)
          --seconds S             time budget (default 30)
          --steps N               step budget instead of a time budget
          --out FILE              write Hellrage Reactor Planner JSON
          --quiet                 only print the final design
        """;

    public static int Run(string[] args)
    {
        string size = "5x5x5", fuel = "LEU-235", variant = "Normal", config = "Default", rates = "";
        double? power = null, heat = null;
        var limits = new List<(string label, int n)>();
        string goal = "power", sym = "x,y,z";
        bool useNet = true, heatNeutral = true, accessible = true, quiet = false;
        bool? parallel = null, incremental = null;
        bool simdNet = true; bool? tiled = null;
        int unitSteps = ClassicSeeding.DefaultUnitSteps; double tileNoise = 0.02; bool adopt = false;
        IEnumerable<int>? unitSizes = null;
        int seed = 0; double seconds = 30; long? steps = null; string? outFile = null;

        for (int i = 0; i < args.Length; ++i)
        {
            string a = args[i];
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{a} needs a value");
            switch (a)
            {
                case "--mode": Next(); break;
                case "--size": size = Next(); break;
                case "--fuel": fuel = Next(); break;
                case "--variant": variant = Next(); break;
                case "--config": config = Next(); break;
                case "--power": power = ParseDouble(Next()); break;
                case "--heat": heat = ParseDouble(Next()); break;
                case "--rates": rates = Next(); break;
                case "--limit":
                {
                    var kv = Next().Split('=');
                    if (kv.Length != 2) throw new ArgumentException("--limit expects LABEL=N");
                    limits.Add((kv[0], int.Parse(kv[1], CultureInfo.InvariantCulture)));
                    break;
                }
                case "--goal": goal = Next(); break;
                case "--sym": sym = Next(); break;
                case "--no-net": useNet = false; break;
                case "--restart":
                    tiled = Next().ToLowerInvariant() switch { "auto" => null, "random" => false, "tiled" => true, var v => throw new ArgumentException("--restart expects auto|random|tiled, got " + v) };
                    break;
                case "--unit-steps": unitSteps = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--unit-sizes": unitSizes = Next().Split(',').Select(v => int.Parse(v.Trim(), CultureInfo.InvariantCulture)).ToList(); break;
                case "--adopt": adopt = Next().ToLowerInvariant() switch { "on" => true, "off" => false, var v => throw new ArgumentException("--adopt expects on|off, got " + v) }; break;
                case "--tile-noise": tileNoise = ParseDouble(Next()); break;
                case "--simd-net":
                    simdNet = Next().ToLowerInvariant() switch { "on" => true, "off" => false, var v => throw new ArgumentException("--simd-net expects on|off, got " + v) };
                    break;
                case "--incremental":
                    incremental = Next().ToLowerInvariant() switch { "auto" => null, "on" => true, "off" => false, var v => throw new ArgumentException("--incremental expects auto|on|off, got " + v) };
                    break;
                case "--parallel":
                    parallel = Next().ToLowerInvariant() switch { "auto" => null, "on" => true, "off" => false, var v => throw new ArgumentException("--parallel expects auto|on|off, got " + v) };
                    break;
                case "--no-heat-neutral": heatNeutral = false; break;
                case "--no-accessible": accessible = false; break;
                case "--seed": seed = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--seconds": seconds = ParseDouble(Next()); break;
                case "--steps": steps = long.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--out": outFile = Next(); break;
                case "--quiet": quiet = true; break;
                case "-h": case "--help": Console.WriteLine(Usage); return 0;
                default: throw new ArgumentException("unknown option " + a);
            }
        }

        var dims = size.ToLowerInvariant().Split('x');
        if (dims.Length != 3) throw new ArgumentException("--size expects WxHxD");
        var settings = new ClassicSettings
        {
            SizeX = int.Parse(dims[0], CultureInfo.InvariantCulture),
            SizeY = int.Parse(dims[1], CultureInfo.InvariantCulture),
            SizeZ = int.Parse(dims[2], CultureInfo.InvariantCulture),
            EnsureActiveCoolerAccessible = accessible,
            EnsureHeatNeutral = heatNeutral,
            Goal = goal.ToLowerInvariant() switch
            {
                "power" => ClassicGoal.Power,
                "breeder" => ClassicGoal.Breeder,
                "efficiency" => ClassicGoal.Efficiency,
                _ => throw new ArgumentException("unknown goal " + goal),
            },
        };
        foreach (var s in sym.ToLowerInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (s == "x") settings.SymX = true;
            else if (s == "y") settings.SymY = true;
            else if (s == "z") settings.SymZ = true;
            else if (s != "none") throw new ArgumentException("unknown symmetry " + s);
        }

        string fuelName;
        if (power.HasValue || heat.HasValue)
        {
            if (!(power.HasValue && heat.HasValue)) throw new ArgumentException("--power and --heat go together");
            settings.FuelBasePower = power.Value;
            settings.FuelBaseHeat = heat.Value;
            fuelName = "";
        }
        else
        {
            var preset = ClassicPresets.FindFuel(config, fuel, variant)
                ?? throw new ArgumentException($"no fuel preset {config} {fuel} {variant}; known fuels: " +
                    string.Join(", ", ClassicPresets.Fuels.Select(f => f.Fuel).Distinct()));
            settings.FuelBasePower = preset.BasePower;
            settings.FuelBaseHeat = preset.BaseHeat;
            fuelName = preset.DisplayName;
        }

        var ratePreset = ClassicPresets.FindCoolingRates(rates.Length > 0 ? rates : config)
            ?? throw new ArgumentException("no cooling-rate preset " + rates);
        Array.Copy(ratePreset.Rates, settings.CoolingRates, NumCoolerIds);

        // Web defaults: everything unlimited except active coolers, which are disabled.
        Array.Fill(settings.Limit, -1);
        for (int t = Active; t < Cell; ++t) settings.Limit[t] = 0;
        foreach (var (label, count) in limits)
            settings.Limit[ParseTileLabel(label)] = count;

        Console.WriteLine($"seed={seed} size={settings.SizeX}x{settings.SizeY}x{settings.SizeZ} fuel={fuelName} power={settings.FuelBasePower} heat={settings.FuelBaseHeat} rates={ratePreset.Config} goal={settings.Goal} sym={sym} net={useNet} heatNeutral={heatNeutral} accessible={accessible}");

        var sw = Stopwatch.StartNew();
        var pool = ClassicSeeding.PoolFor(settings, tiled, seed, unitSizes, unitSteps);
        if (pool != null)
            Console.WriteLine($"unit pool optimized in {sw.Elapsed.TotalSeconds:F1}s ({unitSteps} steps each): " +
                string.Join(", ", pool.Units.Select(u => $"{u.Size.x}x{u.Size.y}x{u.Size.z} score {u.Score:0.####}")));
        using var opt = new ClassicOpt(settings, useNet, seed, parallel, incremental, simdNet, pool, tileNoise, adopt);
        Console.WriteLine($"incremental evaluation: {opt.IncrementalEvaluation}, parallel children: {opt.ParallelChildren}, simd net: {opt.SimdNet}, restarts: {(opt.TiledRestarts ? "tiled" : "random")}");
        long n = 0;
        while (steps.HasValue ? n < steps.Value : sw.Elapsed.TotalSeconds < seconds)
        {
            opt.Step();
            ++n;
            if (!quiet && opt.NeedsRedrawBest())
            {
                var v = opt.Best.Value;
                Console.WriteLine($"[{sw.Elapsed.TotalSeconds,7:F1}s] {StageText(opt)}: avgPower={v.AvgPower:F1} power={v.Power:F1} netHeat={v.NetHeat:F1} duty={v.DutyCycle * 100:F1}% eff={v.Efficiency * 100:F1}% cells={v.Breed}");
            }
        }
        sw.Stop();
        Console.WriteLine($"ran {n} steps in {sw.Elapsed.TotalSeconds:F1}s ({n / sw.Elapsed.TotalSeconds:F0} steps/s); final {StageText(opt)}");
        if (opt.UnitPool != null)
            foreach (var u in opt.UnitPool.Units.OrderBy(u => u.Size.x))
                Console.WriteLine($"unit {u.Size.x}x{u.Size.y}x{u.Size.z}: score {u.Score:0.####} uses {u.Uses} meanOutcome {u.MeanOutcome:0.##} best {u.BestOutcome:0.##} adopted {u.Adopted}");
        Console.WriteLine();
        Console.Write(ClassicExport.RenderMetrics(opt.Best.Value));
        Console.WriteLine();
        Console.Write(ClassicExport.RenderLayers(opt.Best.State));
        Console.WriteLine();
        Console.Write(ClassicExport.RenderBlockCounts(opt.Best.State));

        if (outFile != null)
        {
            File.WriteAllText(outFile, ClassicExport.ToHellrageJson(settings, opt.Best.State, fuelName));
            Console.WriteLine($"wrote {outFile}");
        }
        return 0;
    }

    public static string StageText(ClassicOpt opt) => opt.NStage switch
    {
        ClassicOpt.StageTrain => $"episode {opt.NEpisode}, training iteration {opt.NIteration}",
        ClassicOpt.StageInfer => $"episode {opt.NEpisode}, inference iteration {opt.NIteration}",
        _ => $"episode {opt.NEpisode}, stage {opt.NStage}, iteration {opt.NIteration}",
    };

    private static double ParseDouble(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>Parses a two-character label ("Wt", "[]", "##"), optionally prefixed with 'a' for the active cooler.</summary>
    public static int ParseTileLabel(string label)
    {
        bool active = label.Length == 3 && (label[0] == 'a' || label[0] == 'A');
        string core = active ? label.Substring(1) : label;
        int idx = Array.FindIndex(ClassicPresets.TileNames, n => string.Equals(n, core, StringComparison.OrdinalIgnoreCase));
        if (idx < 0 || idx > 16) throw new ArgumentException("unknown tile label " + label);
        if (active)
        {
            if (idx >= Active) throw new ArgumentException("only coolers have an active variant: " + label);
            return idx + Active;
        }
        return idx < Active ? idx : idx == 15 ? Cell : Moderator;
    }
}
