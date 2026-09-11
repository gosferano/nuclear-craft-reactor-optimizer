// Evaluation oracle for differential testing of the C# port.
//
// Reads a stream of requests from stdin and writes one response per request to
// stdout, flushing after each. It is deliberately dumb: no JSON library, just
// whitespace-separated tokens, so it builds with nothing but the FissionOpt
// sources and xtensor. Doubles are printed with %.17g (exact round-trip);
// non-finite values are printed as Infinity / -Infinity / NaN.
//
// Request grammar (tokens, any whitespace):
//
//   classic
//   sizeX sizeY sizeZ
//   fuelBasePower fuelBaseHeat
//   limit[0..31]                       (32 ints)
//   coolingRates[0..29]                (30 doubles)
//   ensureActiveCoolerAccessible ensureHeatNeutral goal symX symY symZ
//   state[sizeX*sizeY*sizeZ]           (x-major, then y, then z)
//
//   overhaul
//   sizeX sizeY sizeZ
//   nFuels
//   (efficiency limit criticality heat selfPriming) * nFuels
//   limits[0..39]                      (40 ints)
//   sourceLimits[0..2]
//   goal controllable symX symY symZ
//   shieldOn
//   state[sizeX*sizeY*sizeZ]
//
//   quit
//
// Each response ends with a line "END". Field order is documented inline below
// and mirrored by FissionOpt.Tests/Oracle/OracleClient.cs.
#include <cstdio>
#include <cmath>
#include <iostream>
#include <string>
#include "../FissionOpt/Fission.h"
#include "../FissionOpt/OverhaulFission.h"

namespace {
  void pd(double v) {
    if (std::isnan(v)) { std::cout << "NaN"; return; }
    if (std::isinf(v)) { std::cout << (v < 0 ? "-Infinity" : "Infinity"); return; }
    char buf[64];
    std::snprintf(buf, sizeof buf, "%.17g", v);
    std::cout << buf;
  }
  void pi(long long v) { std::cout << v; }
  void pb(bool v) { std::cout << (v ? 1 : 0); }
  void nl() { std::cout << '\n'; }
  void sp() { std::cout << ' '; }

  bool readState(xt::xtensor<int, 3> &state, int sx, int sy, int sz) {
    state = xt::empty<int>({sx, sy, sz});
    for (int x{}; x < sx; ++x)
      for (int y{}; y < sy; ++y)
        for (int z{}; z < sz; ++z)
          if (!(std::cin >> state(x, y, z)))
            return false;
    return true;
  }

  void printState(const xt::xtensor<int, 3> &state, int sx, int sy, int sz) {
    bool first(true);
    for (int x{}; x < sx; ++x)
      for (int y{}; y < sy; ++y)
        for (int z{}; z < sz; ++z) {
          if (!first) sp();
          first = false;
          pi(state(x, y, z));
        }
    nl();
  }

  bool runClassic() {
    Fission::Settings s{};
    std::cin >> s.sizeX >> s.sizeY >> s.sizeZ;
    std::cin >> s.fuelBasePower >> s.fuelBaseHeat;
    for (int i{}; i < Fission::Air; ++i) std::cin >> s.limit[i];
    for (int i{}; i < Fission::Cell; ++i) std::cin >> s.coolingRates[i];
    int a, h, sx, sy, sz;
    std::cin >> a >> h >> s.goal >> sx >> sy >> sz;
    s.ensureActiveCoolerAccessible = a;
    s.ensureHeatNeutral = h;
    s.symX = sx; s.symY = sy; s.symZ = sz;
    xt::xtensor<int, 3> state;
    if (!readState(state, s.sizeX, s.sizeY, s.sizeZ))
      return false;

    Fission::Evaluator evaluator(s);
    Fission::Evaluation e;
    evaluator.run(state, e);

    // Line 1: powerMult heatMult cooling breed heat netHeat dutyCycle avgMult power avgPower avgBreed efficiency
    pd(e.powerMult); sp(); pd(e.heatMult); sp(); pd(e.cooling); sp(); pi(e.breed); sp();
    pd(e.heat); sp(); pd(e.netHeat); sp(); pd(e.dutyCycle); sp(); pd(e.avgMult); sp();
    pd(e.power); sp(); pd(e.avgPower); sp(); pd(e.avgBreed); sp(); pd(e.efficiency); nl();
    // Line 2: nInvalid, then x y z triples in the order the evaluator emitted them
    pi(e.invalidTiles.size());
    for (auto &[x, y, z] : e.invalidTiles) { sp(); pi(x); sp(); pi(y); sp(); pi(z); }
    nl();
    std::cout << "END" << std::endl;
    return true;
  }

  bool runOverhaul() {
    using namespace OverhaulFission;
    Settings s{};
    std::cin >> s.sizeX >> s.sizeY >> s.sizeZ;
    int nFuels;
    std::cin >> nFuels;
    for (int i{}; i < nFuels; ++i) {
      Fuel f{};
      int sp_;
      std::cin >> f.efficiency >> f.limit >> f.criticality >> f.heat >> sp_;
      f.selfPriming = sp_;
      s.fuels.push_back(f);
    }
    for (int i{}; i < Tiles::Air; ++i) std::cin >> s.limits[i];
    for (int i{}; i < 3; ++i) std::cin >> s.sourceLimits[i];
    int c, sx, sy, sz, shieldOn;
    std::cin >> s.goal >> c >> sx >> sy >> sz >> shieldOn;
    s.controllable = c;
    s.symX = sx; s.symY = sy; s.symZ = sz;
    s.compute();
    State state;
    if (!readState(state, s.sizeX, s.sizeY, s.sizeZ))
      return false;

    Evaluation e;
    e.initialize(s, shieldOn);
    e.run(state);

    // Line 1 (settings): nCellTypes (fuel source)* maxOutput minCriticality minHeat
    pi(s.cellTypes.size());
    for (auto &[fuel, source] : s.cellTypes) { sp(); pi(fuel); sp(); pi(source); }
    sp(); pd(s.maxOutput); sp(); pi(s.minCriticality); sp(); pi(s.minHeat); nl();
    // Line 2 (scalars): rawEfficiency efficiency rawOutput output density sparsityPenalty
    //                   nFunctionalBlocks totalPositiveNetHeat irradiatorFlux nActiveCells totalRawFlux maxCellFlux
    pd(e.rawEfficiency); sp(); pd(e.efficiency); sp(); pd(e.rawOutput); sp(); pd(e.output); sp();
    pd(e.density); sp(); pd(e.sparsityPenalty); sp();
    pi(e.nFunctionalBlocks); sp(); pi(e.totalPositiveNetHeat); sp(); pi(e.irradiatorFlux); sp();
    pi(e.nActiveCells); sp(); pi(e.totalRawFlux); sp(); pi(e.maxCellFlux); nl();
    // Line 3: nFluxRoots (x y z)*   -- roots of the final (converged) propagation
    pi(e.fluxRoots.size());
    for (auto &[x, y, z] : e.fluxRoots) { sp(); pi(x); sp(); pi(y); sp(); pi(z); }
    nl();
    // Line 4: nClusters
    // then per cluster one line: rawOutput coolingPenaltyMult output rawEfficiency efficiency heat cooling netHeat hasCasingConnection nTiles (x y z)*
    pi(e.clusters.size()); nl();
    for (auto &cl : e.clusters) {
      pd(cl.rawOutput); sp(); pd(cl.coolingPenaltyMult); sp(); pd(cl.output); sp();
      pd(cl.rawEfficiency); sp(); pd(cl.efficiency); sp();
      pi(cl.heat); sp(); pi(cl.cooling); sp(); pi(cl.netHeat); sp(); pb(cl.hasCasingConnection); sp();
      pi(cl.tiles.size());
      for (auto &[x, y, z] : cl.tiles) { sp(); pi(x); sp(); pi(y); sp(); pi(z); }
      nl();
    }
    // Then one line per tile in x-major order. First token is the kind:
    //   A                                                      air
    //   C neutronSource blocked excludedFromRoots active flux heatMult cluster positionalEfficiency
    //     [if cluster>=0: fluxEfficiency efficiency]
    //     then 6 edges: 0 | 1 efficiency flux nModerators isReflected
    //   M active functional
    //   R active
    //   S flux cluster
    //   I flux cluster
    //   K cluster                                              conductor
    //   H active cluster                                       heat sink
    for (int x{}; x < s.sizeX; ++x)
      for (int y{}; y < s.sizeY; ++y)
        for (int z{}; z < s.sizeZ; ++z) {
          std::visit(Overload {
            [&](Air &) { std::cout << 'A'; },
            [&](Cell &t) {
              std::cout << "C "; pi(t.neutronSource); sp(); pb(t.isNeutronSourceBlocked); sp();
              pb(t.isExcludedFromFluxRoots); sp(); pb(t.isActive); sp(); pi(t.flux); sp();
              pi(t.heatMult); sp(); pi(t.cluster); sp(); pd(t.positionalEfficiency);
              if (t.cluster >= 0) { sp(); pd(t.fluxEfficiency); sp(); pd(t.efficiency); }
              for (int i{}; i < 6; ++i) {
                sp();
                if (!t.fluxEdges[i].has_value()) { std::cout << '0'; continue; }
                auto &ed(*t.fluxEdges[i]);
                std::cout << "1 "; pd(ed.efficiency); sp(); pi(ed.flux); sp(); pi(ed.nModerators); sp(); pb(ed.isReflected);
              }
            },
            [&](Moderator &t) { std::cout << "M "; pb(t.isActive); sp(); pb(t.isFunctional); },
            [&](Reflector &t) { std::cout << "R "; pb(t.isActive); },
            [&](Shield &t) { std::cout << "S "; pi(t.flux); sp(); pi(t.cluster); },
            [&](Irradiator &t) { std::cout << "I "; pi(t.flux); sp(); pi(t.cluster); },
            [&](Conductor &t) { std::cout << "K "; pi(t.cluster); },
            [&](HeatSink &t) { std::cout << "H "; pb(t.isActive); sp(); pi(t.cluster); }
          }, e.tiles(x, y, z));
          nl();
        }
    // Last line: canonicalized state
    State canon(state);
    e.canonicalize(canon);
    printState(canon, s.sizeX, s.sizeY, s.sizeZ);
    std::cout << "END" << std::endl;
    return true;
  }
}

int main() {
  std::ios::sync_with_stdio(false);
  std::string mode;
  while (std::cin >> mode) {
    if (mode == "classic") { if (!runClassic()) break; }
    else if (mode == "overhaul") { if (!runOverhaul()) break; }
    else if (mode == "quit") break;
    else { std::cerr << "oracle: unknown mode '" << mode << "'\n"; return 1; }
  }
  return 0;
}
