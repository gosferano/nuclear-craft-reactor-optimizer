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

## Testing

```bash
dotnet test src/ReactorOptimizer
```

The differential tests generate random reactors (sizes 1³–12³, random tiles and
settings), evaluate them with both the C# port and the C++ oracle, and require every
output field to match bit for bit. Defaults: 100 000 cases, fixed seed. Override with
`FISSIONOPT_FUZZ_CASES` and `FISSIONOPT_FUZZ_SEED`. A mismatch writes the offending
oracle request next to the test binary so it can be replayed:

```bash
reference/oracle/build/oracle < classic-mismatch-N.txt
```
