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
