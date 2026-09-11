using static FissionOpt.Core.Classic.ClassicTiles;

namespace FissionOpt.Core.Classic;

/// <summary>
/// Port of <c>Fission::Evaluator</c> (Fission.cpp). Holds scratch buffers sized for one reactor
/// shape and is reused across evaluations; <see cref="Run"/> allocates nothing after warm-up.
///
/// The five loops in <see cref="Run"/> are ordered and the order is semantic: cooler activation
/// depends on other coolers' already-computed active state (Water → Gold → Iron chains). Do not
/// merge or reorder them.
///
/// The totals are assembled from integer counters at the end (cell multiplier sums, moderator
/// multiplier sum, active-cooler counts per tile ID) rather than accumulated in scan order as the
/// C++ does. Mathematically identical; it can differ from the C++ in the last bit, and it makes the
/// results bit-identical to <see cref="IncrementalClassicEvaluator"/>, which keeps the same counters.
/// </summary>
public sealed class ClassicEvaluator
{
    private readonly ClassicSettings _settings;
    private readonly int _sizeX, _sizeY, _sizeZ;
    private readonly int[] _mults;
    private readonly int[] _rules;
    private readonly bool[] _isActive;
    private readonly bool[] _isModeratorInLine;
    private readonly bool[] _visited;
    private readonly int[] _stack;
    private readonly int[] _activeCount = new int[NumCoolerIds];
    private Grid3 _state = null!;
    private int[] _s = null!; // _state.Data

    public ClassicEvaluator(ClassicSettings settings)
    {
        _settings = settings;
        _sizeX = settings.SizeX;
        _sizeY = settings.SizeY;
        _sizeZ = settings.SizeZ;
        int n = settings.Volume;
        _mults = new int[n];
        _rules = new int[n];
        _isActive = new bool[n];
        _isModeratorInLine = new bool[n];
        _visited = new bool[n];
        _stack = new int[n];
    }

    private int Index(int x, int y, int z) => (x * _sizeY + y) * _sizeZ + z;

    private bool InBounds(int x, int y, int z) =>
        (uint)x < (uint)_sizeX && (uint)y < (uint)_sizeY && (uint)z < (uint)_sizeZ;

    private int GetTileSafe(int x, int y, int z) => InBounds(x, y, z) ? _s[Index(x, y, z)] : -1;

    private int GetMultSafe(int x, int y, int z) => InBounds(x, y, z) ? _mults[Index(x, y, z)] : 0;

    /// <summary>Walks from a cell along one axis; true if another cell is within reach through moderators only.</summary>
    private bool CountMult(int x, int y, int z, int dx, int dy, int dz)
    {
        for (int n = 0; n <= NeutronReach; ++n)
        {
            x += dx; y += dy; z += dz;
            int tile = GetTileSafe(x, y, z);
            if (tile == Cell)
            {
                for (int i = 0; i < n; ++i)
                {
                    x -= dx; y -= dy; z -= dz;
                    _isModeratorInLine[Index(x, y, z)] = true;
                }
                return true;
            }
            else if (tile != Moderator)
            {
                return false;
            }
        }
        return false;
    }

    private int CountMult(int x, int y, int z)
    {
        return 1
            + (CountMult(x, y, z, -1, 0, 0) ? 1 : 0)
            + (CountMult(x, y, z, +1, 0, 0) ? 1 : 0)
            + (CountMult(x, y, z, 0, -1, 0) ? 1 : 0)
            + (CountMult(x, y, z, 0, +1, 0) ? 1 : 0)
            + (CountMult(x, y, z, 0, 0, -1) ? 1 : 0)
            + (CountMult(x, y, z, 0, 0, +1) ? 1 : 0);
    }

    /// <summary>
    /// True if the tile at (x,y,z) has exactly the raw tile ID <paramref name="tile"/> and is active.
    /// Note this compares the raw ID, so e.g. <c>IsActiveSafe(Water, …)</c> matches passive Water only,
    /// never the active-cooler variant. This is how the C++ behaves.
    /// </summary>
    private bool IsActiveSafe(int tile, int x, int y, int z)
    {
        if (!InBounds(x, y, z)) return false;
        int i = Index(x, y, z);
        return _s[i] == tile && _isActive[i];
    }

    private int CountActiveNeighbors(int tile, int x, int y, int z)
    {
        return (IsActiveSafe(tile, x - 1, y, z) ? 1 : 0)
            + (IsActiveSafe(tile, x + 1, y, z) ? 1 : 0)
            + (IsActiveSafe(tile, x, y - 1, z) ? 1 : 0)
            + (IsActiveSafe(tile, x, y + 1, z) ? 1 : 0)
            + (IsActiveSafe(tile, x, y, z - 1) ? 1 : 0)
            + (IsActiveSafe(tile, x, y, z + 1) ? 1 : 0);
    }

    private bool IsTileSafe(int tile, int x, int y, int z) =>
        InBounds(x, y, z) && _s[Index(x, y, z)] == tile;

    private int CountNeighbors(int tile, int x, int y, int z)
    {
        return (IsTileSafe(tile, x - 1, y, z) ? 1 : 0)
            + (IsTileSafe(tile, x + 1, y, z) ? 1 : 0)
            + (IsTileSafe(tile, x, y - 1, z) ? 1 : 0)
            + (IsTileSafe(tile, x, y + 1, z) ? 1 : 0)
            + (IsTileSafe(tile, x, y, z - 1) ? 1 : 0)
            + (IsTileSafe(tile, x, y, z + 1) ? 1 : 0);
    }

    private int CountCasingNeighbors(int x, int y, int z)
    {
        return (InBounds(x - 1, y, z) ? 0 : 1)
            + (InBounds(x + 1, y, z) ? 0 : 1)
            + (InBounds(x, y - 1, z) ? 0 : 1)
            + (InBounds(x, y + 1, z) ? 0 : 1)
            + (InBounds(x, y, z - 1) ? 0 : 1)
            + (InBounds(x, y, z + 1) ? 0 : 1);
    }

    /// <summary>
    /// Mirrors the recursive <c>checkAccessibility</c>: is there a path from (x,y,z) to the casing
    /// (out of bounds) through tiles that are Air or <paramref name="compatibleTile"/>? The C++ is a
    /// DFS with a visited set that short-circuits on the first out-of-bounds neighbour; pure
    /// reachability is order-independent, so an explicit stack gives the identical boolean without
    /// risking a stack overflow at 24³.
    /// </summary>
    private bool CheckAccessibility(int compatibleTile, int x, int y, int z)
    {
        Array.Fill(_visited, false);
        int sp = 0;
        // The start tile is in bounds and equals compatibleTile by construction, but keep the
        // general test so the semantics match the C++ exactly.
        if (!Visit(compatibleTile, x, y, z, ref sp)) return true;
        while (sp > 0)
        {
            int i = _stack[--sp];
            int cz = i % _sizeZ;
            int t = i / _sizeZ;
            int cy = t % _sizeY;
            int cx = t / _sizeY;
            if (!Visit(compatibleTile, cx - 1, cy, cz, ref sp)) return true;
            if (!Visit(compatibleTile, cx + 1, cy, cz, ref sp)) return true;
            if (!Visit(compatibleTile, cx, cy - 1, cz, ref sp)) return true;
            if (!Visit(compatibleTile, cx, cy + 1, cz, ref sp)) return true;
            if (!Visit(compatibleTile, cx, cy, cz - 1, ref sp)) return true;
            if (!Visit(compatibleTile, cx, cy, cz + 1, ref sp)) return true;
        }
        return false;
    }

    /// <summary>Returns false when (x,y,z) is out of bounds (= casing reached); otherwise pushes it if traversable.</summary>
    private bool Visit(int compatibleTile, int x, int y, int z, ref int sp)
    {
        if (!InBounds(x, y, z)) return false;
        int i = Index(x, y, z);
        if (_visited[i]) return true;
        _visited[i] = true;
        int tile = _s[i];
        if (tile != Air && tile != compatibleTile) return true;
        _stack[sp++] = i;
        return true;
    }

    /// <summary>Mirrors <c>Evaluator::run</c>.</summary>
    public void Run(Grid3 state, ClassicEvaluation result)
    {
        if (state.SizeX != _sizeX || state.SizeY != _sizeY || state.SizeZ != _sizeZ)
            throw new ArgumentException("state shape does not match evaluator settings", nameof(state));
        result.InvalidTiles.Clear();
        result.PowerMult = 0.0;
        result.HeatMult = 0.0;
        result.Cooling = 0.0;
        result.Breed = 0;
        Array.Fill(_isActive, false);
        Array.Fill(_isModeratorInLine, false);
        _state = state;
        _s = state.Data;
        var settings = _settings;
        var rates = settings.CoolingRates;
        long cellMultSum = 0, cellHeatSum = 0, modMultSum = 0;
        var activeCount = _activeCount;
        Array.Clear(activeCount);

        // Pass 1: cell multipliers, and the rule (cooler type) governing each cooler tile.
        for (int x = 0; x < _sizeX; ++x)
        for (int y = 0; y < _sizeY; ++y)
        for (int z = 0; z < _sizeZ; ++z)
        {
            int i = Index(x, y, z);
            int tile = _s[i];
            if (tile == Cell)
            {
                int mult = CountMult(x, y, z);
                _mults[i] = mult;
                _rules[i] = -1;
                ++result.Breed;
                cellMultSum += mult;
                cellHeatSum += mult * (mult + 1) / 2;
            }
            else
            {
                _mults[i] = 0;
                if (tile < Active)
                {
                    _rules[i] = tile;
                }
                else if (tile < Cell)
                {
                    if (settings.EnsureActiveCoolerAccessible && !CheckAccessibility(tile, x, y, z))
                        _rules[i] = -1;
                    else
                        _rules[i] = tile - Active;
                }
                else
                {
                    _rules[i] = -1;
                }
            }
        }

        // Pass 2: moderators, and coolers that depend only on cells / casing.
        for (int x = 0; x < _sizeX; ++x)
        for (int y = 0; y < _sizeY; ++y)
        for (int z = 0; z < _sizeZ; ++z)
        {
            int i = Index(x, y, z);
            if (_s[i] == Moderator)
            {
                int mult = GetMultSafe(x - 1, y, z)
                    + GetMultSafe(x + 1, y, z)
                    + GetMultSafe(x, y - 1, z)
                    + GetMultSafe(x, y + 1, z)
                    + GetMultSafe(x, y, z - 1)
                    + GetMultSafe(x, y, z + 1);
                if (mult != 0)
                {
                    _isActive[i] = true;
                    modMultSum += mult;
                }
                else if (!_isModeratorInLine[i])
                {
                    result.InvalidTiles.Add(new Coord(x, y, z));
                }
            }
            else
            {
                switch (_rules[i])
                {
                    case Redstone:
                        _isActive[i] = CountNeighbors(Cell, x, y, z) != 0;
                        break;
                    case Lapis:
                        _isActive[i] = CountNeighbors(Cell, x, y, z) != 0
                            && CountCasingNeighbors(x, y, z) != 0;
                        break;
                    case Enderium:
                        _isActive[i] = CountCasingNeighbors(x, y, z) == 3
                            && (x == 0 || x == _sizeX - 1)
                            && (y == 0 || y == _sizeY - 1)
                            && (z == 0 || z == _sizeZ - 1);
                        break;
                    case Cryotheum:
                        _isActive[i] = CountNeighbors(Cell, x, y, z) >= 2;
                        break;
                }
            }
        }

        // Pass 3: coolers that depend on active moderators / pass-2 coolers.
        for (int x = 0; x < _sizeX; ++x)
        for (int y = 0; y < _sizeY; ++y)
        for (int z = 0; z < _sizeZ; ++z)
        {
            int i = Index(x, y, z);
            switch (_rules[i])
            {
                case Water:
                    _isActive[i] = CountNeighbors(Cell, x, y, z) != 0
                        || CountActiveNeighbors(Moderator, x, y, z) != 0;
                    break;
                case Quartz:
                    _isActive[i] = CountActiveNeighbors(Moderator, x, y, z) != 0;
                    break;
                case Glowstone:
                    _isActive[i] = CountActiveNeighbors(Moderator, x, y, z) >= 2;
                    break;
                case Helium:
                    _isActive[i] = CountActiveNeighbors(Redstone, x, y, z) == 1
                        && CountCasingNeighbors(x, y, z) != 0;
                    break;
                case Emerald:
                    _isActive[i] = CountActiveNeighbors(Moderator, x, y, z) != 0
                        && CountNeighbors(Cell, x, y, z) != 0;
                    break;
                case Tin:
                    // Opposing pairs of active Lapis on any one axis. && binds tighter than ||,
                    // exactly as in the C++; parenthesised here so it cannot be misread.
                    _isActive[i] =
                        (IsActiveSafe(Lapis, x - 1, y, z) && IsActiveSafe(Lapis, x + 1, y, z)) ||
                        (IsActiveSafe(Lapis, x, y - 1, z) && IsActiveSafe(Lapis, x, y + 1, z)) ||
                        (IsActiveSafe(Lapis, x, y, z - 1) && IsActiveSafe(Lapis, x, y, z + 1));
                    break;
                case Magnesium:
                    _isActive[i] = CountActiveNeighbors(Moderator, x, y, z) != 0
                        && CountCasingNeighbors(x, y, z) != 0;
                    break;
            }
        }

        // Pass 4: coolers that depend on pass-3 coolers.
        for (int x = 0; x < _sizeX; ++x)
        for (int y = 0; y < _sizeY; ++y)
        for (int z = 0; z < _sizeZ; ++z)
        {
            int i = Index(x, y, z);
            switch (_rules[i])
            {
                case Gold:
                    _isActive[i] = CountActiveNeighbors(Water, x, y, z) != 0
                        && CountActiveNeighbors(Redstone, x, y, z) != 0;
                    break;
                case Diamond:
                    _isActive[i] = CountActiveNeighbors(Water, x, y, z) != 0
                        && CountActiveNeighbors(Quartz, x, y, z) != 0;
                    break;
                case Copper:
                    _isActive[i] = CountActiveNeighbors(Glowstone, x, y, z) != 0;
                    break;
            }
        }

        // Pass 5: Iron (depends on pass-4 Gold), then tally cooling and invalid coolers.
        for (int x = 0; x < _sizeX; ++x)
        for (int y = 0; y < _sizeY; ++y)
        for (int z = 0; z < _sizeZ; ++z)
        {
            int i = Index(x, y, z);
            int tile = _s[i];
            if (tile < Cell)
            {
                if (_rules[i] == Iron)
                    _isActive[i] = CountActiveNeighbors(Gold, x, y, z) != 0;
                if (_isActive[i])
                    ++activeCount[tile];
                else
                    result.InvalidTiles.Add(new Coord(x, y, z));
            }
        }

        result.PowerMult = cellMultSum + modMultSum * (ModPower / 6.0);
        result.HeatMult = cellHeatSum + modMultSum * (ModHeat / 6.0);
        double cooling = 0.0;
        for (int t = 0; t < NumCoolerIds; ++t)
            if (activeCount[t] != 0)
                cooling += activeCount[t] * rates[t];
        result.Cooling = cooling;
        result.Compute(settings);
    }
}
