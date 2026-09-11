using static FissionOpt.Core.Overhaul.OverhaulTiles;

namespace FissionOpt.Core.Overhaul;

/// <summary>Mirrors <c>OverhaulFission::Cluster</c>. Instances are pooled and reset by the evaluation.</summary>
public sealed class OverhaulCluster
{
    /// <summary>Flat tile indices of the cluster's members, in the same DFS order as the C++.</summary>
    public List<int> Tiles { get; } = new();
    public double RawOutput, CoolingPenaltyMult, Output, RawEfficiency, Efficiency;
    public int Heat, Cooling, NetHeat;
    public bool HasCasingConnection;

    internal void Reset()
    {
        Tiles.Clear();
        RawOutput = 0; CoolingPenaltyMult = 0; Output = 0; RawEfficiency = 0; Efficiency = 0;
        Heat = 0; Cooling = 0; NetHeat = 0;
        HasCasingConnection = false;
    }
}

/// <summary>
/// Port of <c>OverhaulFission::Evaluation</c> (OverhaulFission.cpp). Like the C++, one instance owns
/// the tile grid and all per-tile state, is bound to one <see cref="OverhaulSettings"/> via
/// <see cref="Initialize"/>, and is re-run in place. The <c>std::variant</c> tile becomes a
/// struct-of-arrays keyed by <see cref="TileKind"/>; nothing allocates after warm-up.
/// </summary>
public sealed class OverhaulEvaluation
{
    private OverhaulSettings _settings = null!;
    private bool _shieldOn;
    private int _sizeX, _sizeY, _sizeZ, _n;

    // Per-tile state (see OverhaulFission.h for which fields each kind uses).
    private TileKind[] _kind = null!;
    private int[] _type = null!;          // heat sink / moderator / reflector type, or cell type index for cells
    private int[] _cluster = null!;       // cell, shield, irradiator, conductor, heat sink
    private bool[] _isActive = null!;     // cell, moderator, reflector, heat sink
    private bool[] _isFunctional = null!; // moderator
    private int[] _flux = null!;          // cell, shield, irradiator
    private int[] _fuel = null!;          // cell: index into settings.Fuels
    private int[] _neutronSource = null!;
    private bool[] _isNeutronSourceBlocked = null!;
    private bool[] _isExcludedFromFluxRoots = null!;
    private bool[] _hasAlreadyPropagatedFlux = null!;
    private int[] _heatMult = null!;
    private double[] _positionalEfficiency = null!, _fluxEfficiency = null!, _efficiency = null!;
    // Flux edges: six per tile, index = tile * 6 + direction.
    private bool[] _edgeHas = null!;
    private double[] _edgeEfficiency = null!;
    private int[] _edgeFlux = null!;
    private int[] _edgeNModerators = null!;
    private bool[] _edgeIsReflected = null!;

    private readonly List<int> _cells = new(), _tier1s = new(), _tier2s = new(), _tier3s = new();
    private readonly List<int> _shields = new(), _irradiators = new(), _conductors = new(), _fluxRoots = new();
    private readonly List<OverhaulCluster> _clusters = new();
    private int _nClusters;
    private int[] _stack = null!;
    private int[] _stackDir = null!;

    // Results
    public double RawEfficiency, Efficiency, RawOutput, Output, Density, SparsityPenalty;
    public int NFunctionalBlocks, TotalPositiveNetHeat, IrradiatorFlux, NActiveCells, TotalRawFlux, MaxCellFlux;

    public OverhaulSettings Settings => _settings;
    public bool ShieldOn => _shieldOn;
    public int SizeX => _sizeX;
    public int SizeY => _sizeY;
    public int SizeZ => _sizeZ;
    public IReadOnlyList<int> Cells => _cells;
    public IReadOnlyList<int> FluxRoots => _fluxRoots;
    public IReadOnlyList<int> Irradiators => _irradiators;
    public int ClusterCount => _nClusters;
    public OverhaulCluster GetCluster(int i) => _clusters[i];

    // Per-tile accessors for display / tests.
    public TileKind KindAt(int i) => _kind[i];
    public int TypeAt(int i) => _type[i];
    public int ClusterAt(int i) => _cluster[i];
    public bool IsActiveAt(int i) => _isActive[i];
    public bool IsFunctionalAt(int i) => _isFunctional[i];
    public int FluxAt(int i) => _flux[i];
    public int NeutronSourceAt(int i) => _neutronSource[i];
    public bool IsNeutronSourceBlockedAt(int i) => _isNeutronSourceBlocked[i];
    public bool IsExcludedFromFluxRootsAt(int i) => _isExcludedFromFluxRoots[i];
    public int HeatMultAt(int i) => _heatMult[i];
    public double PositionalEfficiencyAt(int i) => _positionalEfficiency[i];
    public double FluxEfficiencyAt(int i) => _fluxEfficiency[i];
    public double EfficiencyAt(int i) => _efficiency[i];
    public bool EdgeHas(int i, int dir) => _edgeHas[i * 6 + dir];
    public (double efficiency, int flux, int nModerators, bool isReflected) Edge(int i, int dir) =>
        (_edgeEfficiency[i * 6 + dir], _edgeFlux[i * 6 + dir], _edgeNModerators[i * 6 + dir], _edgeIsReflected[i * 6 + dir]);

    public int Index(int x, int y, int z) => (x * _sizeY + y) * _sizeZ + z;
    public Coord CoordOf(int i) => new(i / (_sizeY * _sizeZ), i / _sizeZ % _sizeY, i % _sizeZ);
    private bool InBounds(int x, int y, int z) => (uint)x < (uint)_sizeX && (uint)y < (uint)_sizeY && (uint)z < (uint)_sizeZ;

    /// <summary>Mirrors <c>Evaluation::initialize</c>.</summary>
    public void Initialize(OverhaulSettings settings, bool shieldOn)
    {
        _settings = settings;
        _shieldOn = shieldOn;
        _sizeX = settings.SizeX; _sizeY = settings.SizeY; _sizeZ = settings.SizeZ;
        int n = _n = settings.Volume;
        _kind = new TileKind[n]; _type = new int[n]; _cluster = new int[n];
        _isActive = new bool[n]; _isFunctional = new bool[n]; _flux = new int[n];
        _fuel = new int[n]; _neutronSource = new int[n];
        _isNeutronSourceBlocked = new bool[n]; _isExcludedFromFluxRoots = new bool[n]; _hasAlreadyPropagatedFlux = new bool[n];
        _heatMult = new int[n]; _positionalEfficiency = new double[n]; _fluxEfficiency = new double[n]; _efficiency = new double[n];
        _edgeHas = new bool[n * 6]; _edgeEfficiency = new double[n * 6]; _edgeFlux = new int[n * 6];
        _edgeNModerators = new int[n * 6]; _edgeIsReflected = new bool[n * 6];
        _stack = new int[n];
        _stackDir = new int[n];
    }

    /// <summary>Mirrors <c>Evaluation::checkNeutronSource</c>: a source only counts if some axis reaches the casing unblocked.</summary>
    private void CheckNeutronSource(int i, int x, int y, int z)
    {
        if (_neutronSource[i] == 0)
            return;
        foreach (var (dx, dy, dz) in Directions)
        {
            int cx = x, cy = y, cz = z;
            bool blocked = false;
            while (!blocked)
            {
                cx += dx; cy += dy; cz += dz;
                if (!InBounds(cx, cy, cz))
                    return;
                int c = Index(cx, cy, cz);
                switch (_kind[c])
                {
                    case TileKind.Reflector: blocked = ReflectorFluxMults[_type[c]] >= 1.0; break;
                    case TileKind.Irradiator: blocked = true; break;
                    case TileKind.Cell: blocked = true; break;
                    // Note: an active shield is treated as non-blocking because activating a shield won't undo the flux activation.
                }
            }
        }
        _neutronSource[i] = 0;
        _isNeutronSourceBlocked[i] = true;
    }

    /// <summary>Mirrors <c>Evaluation::computeFluxEdge</c>.</summary>
    private void ComputeFluxEdge(int i, int x, int y, int z)
    {
        for (int d = 0; d < 6; ++d)
        {
            int e = i * 6 + d;
            double efficiency = 0.0;
            int flux = 0, nModerators;
            bool isReflected = false;
            var (dx, dy, dz) = Directions[d];
            int cx = x, cy = y, cz = z;
            bool success = false;
            for (nModerators = 0; nModerators <= NeutronReach; ++nModerators)
            {
                cx += dx; cy += dy; cz += dz;
                if (!InBounds(cx, cy, cz))
                    break;
                bool stop = false;
                int c = Index(cx, cy, cz);
                switch (_kind[c])
                {
                    case TileKind.Moderator:
                        efficiency += ModeratorEfficiencies[_type[c]];
                        flux += ModeratorFluxes[_type[c]];
                        break;
                    case TileKind.Shield:
                        if (_shieldOn) stop = true;
                        else efficiency += ShieldEfficiency;
                        break;
                    case TileKind.Cell:
                        stop = true;
                        if (nModerators != 0)
                        {
                            efficiency /= nModerators;
                            success = true;
                        }
                        break;
                    case TileKind.Irradiator:
                        stop = true;
                        if (nModerators != 0)
                        {
                            efficiency = 0.0;
                            success = true;
                        }
                        break;
                    case TileKind.Reflector:
                        stop = true;
                        if (nModerators != 0 && nModerators <= NeutronReach / 2)
                        {
                            efficiency = ReflectorEfficiencies[_type[c]] * efficiency / nModerators;
                            flux = (int)(2 * flux * ReflectorFluxMults[_type[c]]);
                            isReflected = true;
                            success = true;
                        }
                        break;
                    default:
                        stop = true;
                        break;
                }
                if (stop)
                    break;
            }
            _edgeHas[e] = success;
            if (success)
            {
                _edgeEfficiency[e] = efficiency;
                _edgeFlux[e] = flux;
                _edgeNModerators[e] = nModerators;
                _edgeIsReflected[e] = isReflected;
                TotalRawFlux += flux;
            }
        }
    }

    /// <summary>
    /// Mirrors the recursive <c>propagateFlux(x,y,z)</c> with a worklist. A cell propagates once, when it
    /// is a root or when the flux it has received reaches criticality; since flux only ever increases,
    /// the set of propagating cells and every final flux sum are independent of visit order.
    /// </summary>
    private void PropagateFluxFrom(int root)
    {
        if (_hasAlreadyPropagatedFlux[root])
            return;
        _hasAlreadyPropagatedFlux[root] = true;
        int sp = 0;
        _stack[sp++] = root;
        while (sp > 0)
        {
            int i = _stack[--sp];
            var (x, y, z) = CoordOf(i);
            for (int d = 0; d < 6; ++d)
            {
                int e = i * 6 + d;
                if (!_edgeHas[e])
                    continue;
                if (_edgeIsReflected[e])
                {
                    _flux[i] += _edgeFlux[e];
                    continue;
                }
                var (dx, dy, dz) = Directions[d];
                int steps = _edgeNModerators[e] + 1;
                int to = Index(x + dx * steps, y + dy * steps, z + dz * steps);
                if (_kind[to] != TileKind.Cell)
                    continue;
                _flux[to] += _edgeFlux[e];
                if (_flux[to] >= _settings.Fuels[_fuel[to]].Criticality && !_hasAlreadyPropagatedFlux[to])
                {
                    _hasAlreadyPropagatedFlux[to] = true;
                    _stack[sp++] = to;
                }
            }
        }
    }

    /// <summary>Mirrors <c>Evaluation::propagateFlux()</c>: the criticality fixpoint loop.</summary>
    private void PropagateFlux()
    {
        bool converged = false;
        while (!converged)
        {
            _fluxRoots.Clear();
            foreach (int i in _cells)
            {
                // TODO (upstream): handle neutron source indirection while keeping canonicalization valid.
                if (!_isExcludedFromFluxRoots[i] && (
                        _settings.Fuels[_fuel[i]].SelfPriming || _neutronSource[i] != 0
                        || (_shieldOn && _flux[i] >= _settings.Fuels[_fuel[i]].Criticality)))
                    _fluxRoots.Add(i);
                _hasAlreadyPropagatedFlux[i] = false;
                _flux[i] = 0;
            }
            foreach (int i in _fluxRoots)
                PropagateFluxFrom(i);
            converged = true;
            foreach (int i in _fluxRoots)
            {
                if (_flux[i] < _settings.Fuels[_fuel[i]].Criticality)
                {
                    _isExcludedFromFluxRoots[i] = true;
                    converged = false;
                }
            }
        }
    }

    /// <summary>Mirrors <c>Evaluation::computeFluxActivation</c>.</summary>
    private void ComputeFluxActivation()
    {
        NActiveCells = 0;
        MaxCellFlux = 0;
        foreach (int i in _cells)
        {
            MaxCellFlux = Math.Max(MaxCellFlux, _flux[i]);
            _isActive[i] = _flux[i] >= _settings.Fuels[_fuel[i]].Criticality;
            if (!_isActive[i])
                continue;
            ++NActiveCells;
            var (x, y, z) = CoordOf(i);
            for (int d = 0; d < 6; ++d)
            {
                int e = i * 6 + d;
                if (!_edgeHas[e])
                    continue;
                var (dx, dy, dz) = Directions[d];
                int nMod = _edgeNModerators[e];
                // Skip edges that end at an inactive cell.
                {
                    int steps = nMod + 1;
                    int to = Index(x + dx * steps, y + dy * steps, z + dz * steps);
                    if (_kind[to] == TileKind.Cell && _flux[to] < _settings.Fuels[_fuel[to]].Criticality)
                        continue;
                }
                ++_heatMult[i];
                _positionalEfficiency[i] += _edgeEfficiency[e];
                int cx = x, cy = y, cz = z;
                for (int j = 0; j <= nMod; ++j)
                {
                    cx += dx; cy += dy; cz += dz;
                    int c = Index(cx, cy, cz);
                    switch (_kind[c])
                    {
                        case TileKind.Moderator:
                            if (j == 0)
                                _isActive[c] = true;
                            _isFunctional[c] = true;
                            break;
                        case TileKind.Shield:
                            if (_edgeIsReflected[e] || (d & 1) != 0)
                                _flux[c] += _edgeFlux[e];
                            break;
                        case TileKind.Irradiator:
                            _flux[c] += _edgeFlux[e];
                            break;
                        case TileKind.Reflector:
                            _isActive[c] = true;
                            break;
                    }
                }
            }
        }
    }

    private int CountAdjacentCells(int x, int y, int z)
    {
        int result = 0;
        foreach (var (dx, dy, dz) in Directions)
        {
            int cx = x + dx, cy = y + dy, cz = z + dz;
            if (InBounds(cx, cy, cz))
            {
                int c = Index(cx, cy, cz);
                if (_kind[c] == TileKind.Cell && _isActive[c]) ++result;
            }
        }
        return result;
    }

    private int CountAdjacentCasings(int x, int y, int z)
    {
        int result = 0;
        foreach (var (dx, dy, dz) in Directions)
            if (!InBounds(x + dx, y + dy, z + dz)) ++result;
        return result;
    }

    private int CountAdjacentReflectors(int x, int y, int z)
    {
        int result = 0;
        foreach (var (dx, dy, dz) in Directions)
        {
            int cx = x + dx, cy = y + dy, cz = z + dz;
            if (InBounds(cx, cy, cz))
            {
                int c = Index(cx, cy, cz);
                if (_kind[c] == TileKind.Reflector && _isActive[c]) ++result;
            }
        }
        return result;
    }

    private int CountAdjacentModerators(int x, int y, int z)
    {
        int result = 0;
        foreach (var (dx, dy, dz) in Directions)
        {
            int cx = x + dx, cy = y + dy, cz = z + dz;
            if (InBounds(cx, cy, cz))
            {
                int c = Index(cx, cy, cz);
                if (_kind[c] == TileKind.Moderator && _isActive[c]) ++result;
            }
        }
        return result;
    }

    private bool IsActiveHeatSink(int type, int cx, int cy, int cz)
    {
        if (!InBounds(cx, cy, cz)) return false;
        int c = Index(cx, cy, cz);
        return _kind[c] == TileKind.HeatSink && _isActive[c] && _type[c] == type;
    }

    private int CountAdjacentHeatSinks(int type, int x, int y, int z)
    {
        int result = 0;
        foreach (var (dx, dy, dz) in Directions)
            if (IsActiveHeatSink(type, x + dx, y + dy, z + dz)) ++result;
        return result;
    }

    /// <summary>
    /// Mirrors <c>countAxialAdjacentHeatSinks</c>: counts axes on which BOTH opposing neighbours are
    /// active sinks of <paramref name="type"/>. The C++ walks directions in (-,+) pairs and uses
    /// <c>i |= !valid</c> to skip the + side when the - side failed; written out plainly here.
    /// </summary>
    private int CountAxialAdjacentHeatSinks(int type, int x, int y, int z)
    {
        int result = 0;
        for (int axis = 0; axis < 3; ++axis)
        {
            var (dx, dy, dz) = Directions[axis * 2];
            if (IsActiveHeatSink(type, x + dx, y + dy, z + dz) && IsActiveHeatSink(type, x - dx, y - dy, z - dz))
                ++result;
        }
        return result;
    }

    private bool IsActiveReflector(int cx, int cy, int cz)
    {
        if (!InBounds(cx, cy, cz)) return false;
        int c = Index(cx, cy, cz);
        return _kind[c] == TileKind.Reflector && _isActive[c];
    }

    /// <summary>Mirrors <c>hasAxialAdjacentReflectors</c>: true if any axis has active reflectors on both sides.</summary>
    private bool HasAxialAdjacentReflectors(int x, int y, int z)
    {
        for (int axis = 0; axis < 3; ++axis)
        {
            var (dx, dy, dz) = Directions[axis * 2];
            if (IsActiveReflector(x + dx, y + dy, z + dz) && IsActiveReflector(x - dx, y - dy, z - dz))
                return true;
        }
        return false;
    }

    /// <summary>Mirrors <c>Evaluation::computeHeatSinkActivation</c>.</summary>
    private void ComputeHeatSinkActivation(int i)
    {
        var (x, y, z) = CoordOf(i);
        bool active;
        switch (_type[i])
        {
            default: // Wt
                active = CountAdjacentCells(x, y, z) != 0; break;
            case Fe:
                active = CountAdjacentModerators(x, y, z) != 0; break;
            case Rs:
                active = CountAdjacentCells(x, y, z) != 0 && CountAdjacentModerators(x, y, z) != 0; break;
            case Qz:
                active = CountAdjacentHeatSinks(Rs, x, y, z) != 0; break;
            case Ob:
                active = CountAxialAdjacentHeatSinks(Gs, x, y, z) != 0; break;
            case Nr:
                active = CountAdjacentHeatSinks(Ob, x, y, z) != 0; break;
            case Gs:
                active = CountAdjacentModerators(x, y, z) >= 2; break;
            case Lp:
                active = CountAdjacentCells(x, y, z) != 0 && CountAdjacentCasings(x, y, z) != 0; break;
            case Au:
                active = CountAdjacentHeatSinks(Fe, x, y, z) == 2; break;
            case Pm:
                active = CountAdjacentHeatSinks(Wt, x, y, z) >= 2; break;
            case Sm:
                active = CountAdjacentHeatSinks(Wt, x, y, z) == 1 && CountAdjacentHeatSinks(Pb, x, y, z) >= 2; break;
            case En:
                active = CountAdjacentReflectors(x, y, z) != 0; break;
            case Pr:
                active = CountAdjacentHeatSinks(Fe, x, y, z) != 0 && CountAdjacentReflectors(x, y, z) != 0; break;
            case Dm:
                active = CountAdjacentHeatSinks(Au, x, y, z) != 0 && CountAdjacentCells(x, y, z) != 0; break;
            case Em:
                active = CountAdjacentHeatSinks(Pm, x, y, z) != 0 && CountAdjacentModerators(x, y, z) != 0; break;
            case Cu:
                active = CountAdjacentHeatSinks(Wt, x, y, z) != 0; break;
            case Sn:
                active = CountAxialAdjacentHeatSinks(Lp, x, y, z) != 0; break;
            case Pb:
                active = CountAdjacentHeatSinks(Fe, x, y, z) != 0; break;
            case B:
                active = CountAdjacentHeatSinks(Qz, x, y, z) == 1 && CountAdjacentCasings(x, y, z) != 0; break;
            case Li:
                active = CountAxialAdjacentHeatSinks(Pb, x, y, z) == 1 && CountAdjacentCasings(x, y, z) != 0; break;
            case Mg:
                active = CountAdjacentModerators(x, y, z) == 1 && CountAdjacentCasings(x, y, z) != 0; break;
            case Mn:
                active = CountAdjacentCells(x, y, z) >= 2; break;
            case Al:
                active = CountAdjacentHeatSinks(Qz, x, y, z) != 0 && CountAdjacentHeatSinks(Lp, x, y, z) != 0; break;
            case Ag:
                active = CountAdjacentHeatSinks(Gs, x, y, z) >= 2 && CountAdjacentHeatSinks(Sn, x, y, z) != 0; break;
            case Fl:
                active = CountAdjacentHeatSinks(Au, x, y, z) != 0 && CountAdjacentHeatSinks(Pm, x, y, z) != 0; break;
            case Vi:
                active = CountAdjacentHeatSinks(En, x, y, z) != 0 && CountAdjacentHeatSinks(Rs, x, y, z) != 0; break;
            case Cb:
                active = CountAdjacentHeatSinks(Cu, x, y, z) != 0 && CountAdjacentHeatSinks(En, x, y, z) != 0; break;
            case As:
                active = HasAxialAdjacentReflectors(x, y, z); break;
            case N:
                active = CountAdjacentHeatSinks(Cu, x, y, z) >= 2 && CountAdjacentHeatSinks(Pr, x, y, z) != 0; break;
            case He:
                active = CountAdjacentHeatSinks(Rs, x, y, z) == 2; break;
            case Ed:
                active = CountAdjacentModerators(x, y, z) >= 3; break;
            case Cr:
                active = CountAdjacentCells(x, y, z) >= 3; break;
        }
        _isActive[i] = active;
    }

    /// <summary>Can this tile join a cluster (mirrors the <c>valid</c> visitor in <c>propagateCluster</c>)?</summary>
    private bool ClusterValid(int c) => _kind[c] switch
    {
        TileKind.Cell => _isActive[c] && _cluster[c] < 0,
        TileKind.Shield => !_shieldOn && _flux[c] != 0 && _cluster[c] < 0,
        TileKind.HeatSink => _isActive[c] && _cluster[c] < 0,
        TileKind.Irradiator => _flux[c] != 0 && _cluster[c] < 0,
        TileKind.Conductor => _cluster[c] < 0,
        _ => false,
    };

    /// <summary>
    /// Mirrors <c>propagateCluster(-1, x, y, z)</c>: seeds a new cluster at a tile if it is valid, then
    /// flood-fills. The explicit stack holds (tile, next direction) frames so tiles are visited in
    /// exactly the recursive DFS's pre-order; the cluster stats are floating-point sums over the tile
    /// list, so the order must match the C++ bit for bit.
    /// </summary>
    private void PropagateCluster(int seed)
    {
        if (!ClusterValid(seed))
            return;
        int id = _nClusters++;
        if (_clusters.Count < _nClusters)
            _clusters.Add(new OverhaulCluster());
        var cluster = _clusters[id];
        cluster.Reset();
        _cluster[seed] = id;
        cluster.Tiles.Add(seed);
        int sp = 0;
        _stack[sp++] = seed;
        _stackDir[sp - 1] = 0;
        while (sp > 0)
        {
            int i = _stack[sp - 1];
            int d = _stackDir[sp - 1];
            if (d == 6)
            {
                --sp;
                continue;
            }
            _stackDir[sp - 1] = d + 1;
            var (x, y, z) = CoordOf(i);
            var (dx, dy, dz) = Directions[d];
            int cx = x + dx, cy = y + dy, cz = z + dz;
            if (!InBounds(cx, cy, cz))
            {
                cluster.HasCasingConnection = true;
                continue;
            }
            int c = Index(cx, cy, cz);
            if (!ClusterValid(c))
                continue;
            _cluster[c] = id;
            cluster.Tiles.Add(c);
            _stack[sp] = c;
            _stackDir[sp] = 0;
            ++sp;
        }
    }

    /// <summary>Mirrors <c>Evaluation::computeClusterStats</c>.</summary>
    private void ComputeClusterStats(OverhaulCluster cluster)
    {
        foreach (int i in cluster.Tiles)
        {
            switch (_kind[i])
            {
                case TileKind.HeatSink:
                    cluster.Cooling += CoolingRates[_type[i]];
                    break;
                case TileKind.Cell:
                {
                    var fuel = _settings.Fuels[_fuel[i]];
                    _fluxEfficiency[i] = 1 / (1 + Math.Exp(2 * (_flux[i] - 2 * fuel.Criticality)));
                    _efficiency[i] = _positionalEfficiency[i] * fuel.Efficiency * _fluxEfficiency[i];
                    if (_neutronSource[i] != 0)
                        _efficiency[i] *= SourceEfficiencies[_neutronSource[i] - 1];
                    cluster.RawEfficiency += _efficiency[i];
                    cluster.RawOutput += _efficiency[i] * fuel.Heat;
                    cluster.Heat += _heatMult[i] * fuel.Heat;
                    break;
                }
                case TileKind.Shield:
                    cluster.Heat += _flux[i] * ShieldHeatPerFlux;
                    break;
                // Note: irradiators are ignored as they're all currently zero heat.
            }
        }
        cluster.NetHeat = cluster.Heat - cluster.Cooling;
        // std::min(1.0, x) semantics: a +inf ratio (cooling == 0) yields 1.0.
        double ratio = (double)(cluster.Heat + CoolingEfficiencyLeniency) / cluster.Cooling;
        cluster.CoolingPenaltyMult = ratio < 1.0 ? ratio : 1.0;
        cluster.Output = cluster.RawOutput * cluster.CoolingPenaltyMult;
        cluster.Efficiency = cluster.RawEfficiency * cluster.CoolingPenaltyMult;
    }

    /// <summary>Mirrors <c>Evaluation::computeSparsity</c>.</summary>
    private void ComputeSparsity()
    {
        NFunctionalBlocks = 0;
        for (int i = 0; i < _n; ++i)
        {
            switch (_kind[i])
            {
                case TileKind.Cell: if (_isActive[i]) ++NFunctionalBlocks; break;
                case TileKind.Moderator: if (_isFunctional[i]) ++NFunctionalBlocks; break;
                case TileKind.Reflector: if (_isActive[i]) ++NFunctionalBlocks; break;
                case TileKind.Shield: if (_flux[i] != 0) ++NFunctionalBlocks; break;
                case TileKind.Irradiator: if (_flux[i] != 0) ++NFunctionalBlocks; break;
                case TileKind.HeatSink: if (_isActive[i]) ++NFunctionalBlocks; break;
            }
        }
        Density = (double)NFunctionalBlocks / (_sizeX * _sizeY * _sizeZ);
        if (Density >= SparsityPenaltyThreshold)
            SparsityPenalty = 1.0;
        else
            SparsityPenalty = MaxSparsityPenaltyMult + (1 - MaxSparsityPenaltyMult)
                * Math.Sin(Density * Math.Acos(-1.0) / (2 * SparsityPenaltyThreshold));
    }

    /// <summary>Mirrors <c>Evaluation::computeStats</c>.</summary>
    private void ComputeStats()
    {
        TotalPositiveNetHeat = 0;
        RawEfficiency = 0.0;
        RawOutput = 0.0;
        for (int k = 0; k < _nClusters; ++k)
        {
            var cluster = _clusters[k];
            if (cluster.HasCasingConnection)
            {
                TotalPositiveNetHeat += Math.Max(0, cluster.NetHeat);
                RawEfficiency += cluster.Efficiency;
                RawOutput += cluster.Output;
            }
            else
            {
                TotalPositiveNetHeat += cluster.Heat;
            }
        }
        if (NActiveCells != 0)
            RawEfficiency /= NActiveCells;
        Efficiency = RawEfficiency * SparsityPenalty;
        Output = RawOutput * SparsityPenalty;
        IrradiatorFlux = 0;
        foreach (int i in _irradiators)
            IrradiatorFlux += _flux[i];
    }

    /// <summary>Mirrors <c>Evaluation::run</c>.</summary>
    public void Run(Grid3 state)
    {
        if (state.SizeX != _sizeX || state.SizeY != _sizeY || state.SizeZ != _sizeZ)
            throw new ArgumentException("state shape does not match the settings this evaluation was initialized with", nameof(state));
        _cells.Clear(); _tier1s.Clear(); _tier2s.Clear(); _tier3s.Clear();
        _shields.Clear(); _irradiators.Clear(); _conductors.Clear();
        var s = state.Data;
        // Populate tiles, mirroring the per-kind default member initializers of the C++ structs.
        for (int i = 0; i < _n; ++i)
        {
            int type = s[i];
            _cluster[i] = -1;
            _isActive[i] = false;
            _isFunctional[i] = false;
            _flux[i] = 0;
            _type[i] = 0;
            if (type < M0)
            {
                _kind[i] = TileKind.HeatSink;
                _type[i] = type;
                switch (type)
                {
                    case Wt: case Fe: case Rs: case Gs: case Lp: case En: case Mg: case Mn: case As: case Ed: case Cr:
                        _tier1s.Add(i); break;
                    case Qz: case Ob: case Au: case Pm: case Pr: case Cu: case Sn: case Pb: case Vi: case He:
                        _tier2s.Add(i); break;
                    default:
                        _tier3s.Add(i); break;
                }
            }
            else if (type < R0)
            {
                _kind[i] = TileKind.Moderator;
                _type[i] = type - M0;
            }
            else if (type < Shield)
            {
                _kind[i] = TileKind.Reflector;
                _type[i] = type - R0;
            }
            else switch (type)
            {
                case Shield: _kind[i] = TileKind.Shield; _shields.Add(i); break;
                case Irradiator: _kind[i] = TileKind.Irradiator; _irradiators.Add(i); break;
                case Conductor: _kind[i] = TileKind.Conductor; _conductors.Add(i); break;
                case Air: _kind[i] = TileKind.Air; break;
                default:
                {
                    var (fuel, source) = _settings.CellTypes[type - C0];
                    _kind[i] = TileKind.Cell;
                    _type[i] = type - C0;
                    _fuel[i] = fuel;
                    _neutronSource[i] = source;
                    _isNeutronSourceBlocked[i] = false;
                    _isExcludedFromFluxRoots[i] = false;
                    _heatMult[i] = 0;
                    _positionalEfficiency[i] = 0.0;
                    _fluxEfficiency[i] = 0.0;
                    _efficiency[i] = 0.0;
                    _cells.Add(i);
                    break;
                }
            }
        }
        TotalRawFlux = 0;
        foreach (int i in _cells)
        {
            var (x, y, z) = CoordOf(i);
            CheckNeutronSource(i, x, y, z);
            ComputeFluxEdge(i, x, y, z);
        }
        PropagateFlux();
        ComputeFluxActivation();
        foreach (int i in _tier1s) ComputeHeatSinkActivation(i);
        foreach (int i in _tier2s) ComputeHeatSinkActivation(i);
        foreach (int i in _tier3s) ComputeHeatSinkActivation(i);
        _nClusters = 0;
        foreach (int i in _cells) PropagateCluster(i);
        if (!_shieldOn)
            foreach (int i in _shields) PropagateCluster(i);
        foreach (int i in _irradiators) PropagateCluster(i);
        for (int k = 0; k < _nClusters; ++k) ComputeClusterStats(_clusters[k]);
        ComputeSparsity();
        ComputeStats();
    }

    /// <summary>Mirrors <c>Evaluation::canonicalize</c>: strips non-functional tiles from a state, based on the last <see cref="Run"/>.</summary>
    public void Canonicalize(Grid3 state)
    {
        var s = state.Data;
        for (int i = 0; i < _n; ++i)
        {
            switch (_kind[i])
            {
                case TileKind.Cell:
                    if (!_isActive[i])
                        s[i] = Air;
                    else if (_isNeutronSourceBlocked[i])
                        s[i] -= _settings.CellTypes[s[i] - C0].source;
                    break;
                case TileKind.HeatSink:
                    // Note (upstream): according to the planner, heat sinks without a cluster still count as functional blocks.
                    if (!_isActive[i]) s[i] = Air;
                    break;
                case TileKind.Irradiator:
                    if (_flux[i] == 0) s[i] = Air;
                    break;
                case TileKind.Moderator:
                    if (!_isFunctional[i]) s[i] = Air;
                    break;
                case TileKind.Shield:
                    if (_cluster[i] < 0) s[i] = Air;
                    break;
                case TileKind.Reflector:
                    if (!_isActive[i]) s[i] = Air;
                    break;
                case TileKind.Conductor:
                    // TODO (upstream): remove redundant conductors.
                    if (_cluster[i] < 0) s[i] = Air;
                    break;
            }
        }
    }
}
