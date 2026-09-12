# nuclear-craft-reactor-optimizer

A .NET port of [FissionOpt](https://github.com/cyb0124/FissionOpt) — cyb0124's
NuclearCraft fission reactor design optimizer — as a Terminal.Gui application.

## Attribution

All of the reactor rules, the search algorithm, the value network and the fuel/cooler
data tables in this repository are ported from **FissionOpt by cyb0124**:

- Source: https://github.com/cyb0124/FissionOpt
- Web version: https://leu-235.com/

The upstream repository has no LICENSE file, so it is technically all-rights-reserved.
This port is not to be publicly released or redistributed without clarifying that first.
The original C++ is vendored under `reference/FissionOpt` (as a git submodule) and is
used verbatim as the test oracle.

## Layout

```
reference/
  FissionOpt/          upstream C++ (submodule, pinned to the last commit, Aug 2020)
  xtl/, xtensor/       header-only deps (submodules, pinned to 0.6.16 / 0.21.5)
  oracle/              Oracle.cpp: stdin/stdout evaluation oracle used by the tests
src/ReactorOptimizer/
  FissionOpt.Core/     evaluators, search, value net, presets — no UI dependency
  FissionOpt.Tui/      Terminal.Gui v2 app
  FissionOpt.Tests/    xunit; includes the differential fuzz harness against the oracle
```

## Building

```bash
git submodule update --init
dotnet build src/ReactorOptimizer
```

The test project builds the C++ oracle on first run (needs a C++17 compiler on `PATH`
as `c++`, or set `CXX`). To build it by hand:

```bash
reference/oracle/build.sh
```

## Running the TUI

```bash
dotnet run -c Release --project src/ReactorOptimizer/FissionOpt.Tui
```

Pick the NuclearCraft version at the top (Alt+C classic / Alt+O overhaul), then three tabs
(Alt+S / Alt+B / Alt+R): **Settings** (size, searchable fuel presets or manual fuels, goal,
symmetry, toggles, seed), **Blocks** (per-block limits; classic also has cooling rates with
the Default / E2E / PO3 presets), **Reactor** (live design, metrics, block counts,
training-loss sparkline). F5 runs, F6 pauses/resumes, F7 stops, F8 saves planner JSON
(Hellrage format for classic, the overhaul planner format for overhaul), Ctrl+Q quits.
Axes are shown the way leu-235.com and the planner show them (X × Y × Z, Y vertical);
internally the layer axis is x. Active coolers are drawn in reverse video.

## Running headless

```bash
dotnet run -c Release --project src/ReactorOptimizer/FissionOpt.Tui -- headless --size 5x5x5 --fuel LEU-235 --seconds 60 --seed 1 --out reactor.json
```

`--help` lists every flag (fuel/config/rate presets, per-block limits, goal, symmetry,
value-net toggle). Add `--mode overhaul` for the post-overhaul optimizer (`--mode overhaul
--help` for its flags: `--fuel OX:LEU-235`, `--source-limits`, `--controllable`, …). The seed
is printed on the first line; the same seed reproduces the run. `--out` writes planner JSON
in the same shape leu-235.com saves for each mode.

## Performance notes

The optimizer runs on one background thread, exactly like the original. Two classic-mode
accelerations exist, both toggleable and both tested to reproduce the identical run for a seed:

- **Incremental evaluation** (`--incremental auto|on|off`, checkbox in the TUI): mutations are
  evaluated by updating only the tiles a change can reach, with undo; the scalar evaluator
  (`ClassicEvaluator`) remains the reference and both assemble their totals from the same
  integer counters, so results are bit-identical. Automatic unless active coolers are enabled
  *and* must be accessible (that connectivity check is global), in which case it falls back.
- **Parallel children** (`--parallel auto|on|off`): when incremental evaluation is off and the
  grid has 300+ tiles, the four children of each step are evaluated on separate threads.

The value net (`ValueNet`) has two arithmetic paths, selectable per run ("SIMD value net"
checkbox, `--simd-net on|off`, default on): hand-written `Vector256` kernels with a fixed
reduction order — about 3× faster than the loops and bit-identical on every CPU — or the
original plain loops (kept bit-exact against the pre-SIMD implementation by test). The two
paths differ in the last bits of the dot products, so a seeded run with the net on reproduces
exactly on any machine *with the same setting*, and may differ between settings.

Measured on a 24³, no symmetry, breeder goal (Release, this machine): scalar ~390 steps/s,
parallel children ~1 450, incremental ~150 000 in rollout; a training iteration costs ~0.5 ms
and an inference ~2.4 µs. Always build Release — Debug is ~3.5× slower.

**Tiled restarts** (classic; not in upstream): every rule is local, so for large grids each
episode restarts from a small unit design tiled across the grid (random phase shift, 2% noise)
instead of from a random grid. A pool of units of sizes 4–8 is optimized up front (~2 s, in
parallel); each episode picks one — every unit once, then epsilon-greedy on how episodes seeded
from it ended — and in this mode the value net's guided climb starts from the fresh tiling
rather than from the previous design. `--restart auto|random|tiled` / "Episode restarts" in
the TUI; auto = tiled for grids of 2 000+ tiles (no measurable difference at 10³). Measured on a
24³ without symmetry, 5 minutes, 8 seeds: breeding 5 838 vs 5 421 cells (+7.7%, best seed
6 638), power 1.487 M vs 1.460 M RF/t (+1.9%), and the pool is at 10 s where random restarts are
at 5 minutes. `--adopt on` additionally feeds crops of converged designs back into the pool;
it measured slightly worse (crops cost an episode each to evaluate), so it is off by default.

One consequence of the counter-based totals: the port's classic double totals can differ
from the C++ in the last bit (summation order), so the classic oracle test compares those
to 1e-9 relative and everything else exactly.

## Presets

The fuel, cooling-rate and tile-name tables in `FissionOpt.Core/Presets/*.g.cs` are
generated from the upstream JavaScript by `tools/extract_presets.py`; re-run it rather
than editing them.

## Testing

```bash
dotnet test src/ReactorOptimizer
```

The differential tests generate random reactors (sizes 1³–12³, random tiles and
settings) for both the classic and the overhaul evaluator, evaluate them with both the C#
port and the C++ oracle, and require every output field — scalars, cluster stats,
per-tile state, flux edges, the canonicalized state — to match bit for bit. Defaults: 100 000 cases, fixed seed. Override with
`FISSIONOPT_FUZZ_CASES` and `FISSIONOPT_FUZZ_SEED`. A mismatch writes the offending
oracle request next to the test binary so it can be replayed:

```bash
reference/oracle/build/oracle < classic-mismatch-N.txt
```
