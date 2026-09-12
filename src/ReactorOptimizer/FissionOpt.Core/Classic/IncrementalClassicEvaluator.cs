using static FissionOpt.Core.Classic.ClassicTiles;

namespace FissionOpt.Core.Classic;

/// <summary>
/// Incremental counterpart of <see cref="ClassicEvaluator"/>. It is bound to one grid, keeps every
/// per-tile intermediate of the five scalar passes (cell multipliers, moderator sums, cooler
/// activation, in-line counts) and running counters, and updates only the tiles whose inputs a
/// mutation can reach. Every write is journaled so a mutation can be undone in O(dirty set).
///
/// Semantics are those of the scalar evaluator; the totals are assembled from integer counters,
/// so they equal the scalar sums mathematically but not necessarily in the last bit (the scalar
/// evaluator accumulates in scan order). Active-cooler accessibility is not modelled: callers must
/// only use this when no active coolers can appear or accessibility is not enforced
/// (see <see cref="Supports"/>).
/// </summary>
public sealed class IncrementalClassicEvaluator
{
    private readonly ClassicSettings _settings;
    private readonly int _sizeX, _sizeY, _sizeZ, _n;
    private readonly int[] _s;        // state.Data
    public Grid3 State { get; }

    // Per-tile cache of the scalar passes.
    private readonly int[] _mult;     // cells: multiplier; others 0
    private readonly int[] _modMult;  // moderators: sum of adjacent cell multipliers
    private readonly int[] _rules;    // cooler type governing the tile, or -1
    private readonly bool[] _isActive;
    private readonly int[] _inLine;   // number of successful cell rays covering this position
    private readonly bool[] _invalid;

    // Counters.
    private int _breed;
    private long _cellMultSum, _cellHeatSum, _modMultSum;
    private readonly int[] _countByTile = new int[Air + 1];
    private readonly int[] _activeCountByTile = new int[NumCoolerIds];
    private readonly int[] _invalidByTile = new int[Air + 1];
    private int _invalidCount;

    // Scratch.
    private readonly int[] _stamp;
    private int _gen;
    private readonly List<int> _dirtyCells = new(), _modDirty = new(), _changed = new(), _candidates = new();
    private readonly List<(int index, int oldTile, int newTile)> _pending = new();

    // Undo journal.
    private const byte ArrState = 0, ArrMult = 1, ArrModMult = 2, ArrIsActive = 3, ArrInLine = 4, ArrInvalid = 5, ArrRules = 6;
    private readonly List<(byte arr, int index, int old)> _journal = new();
    private int _uBreed;
    private long _uCellMultSum, _uCellHeatSum, _uModMultSum;
    private readonly int[] _uCountByTile = new int[Air + 1], _uActiveCountByTile = new int[NumCoolerIds], _uInvalidByTile = new int[Air + 1];
    private int _uInvalidCount;
    private bool _canUndo;

    public int Breed => _breed;
    public int InvalidCount => _invalidCount;
    /// <summary>Tile counts by tile ID (Air included).</summary>
    public ReadOnlySpan<int> CountByTile => _countByTile;
    /// <summary>Invalid-tile counts by tile ID.</summary>
    public ReadOnlySpan<int> InvalidByTile => _invalidByTile;
    public double PowerMult => _cellMultSum + _modMultSum * (ModPower / 6.0);
    public double HeatMult => _cellHeatSum + _modMultSum * (ModHeat / 6.0);

    public double Cooling
    {
        get
        {
            double c = 0.0;
            var rates = _settings.CoolingRates;
            for (int t = 0; t < NumCoolerIds; ++t)
                if (_activeCountByTile[t] != 0)
                    c += _activeCountByTile[t] * rates[t];
            return c;
        }
    }

    /// <summary>True when the incremental evaluator gives the same answers as the scalar one for these settings.</summary>
    public static bool Supports(ClassicSettings settings)
    {
        if (!settings.EnsureActiveCoolerAccessible) return true;
        for (int t = Active; t < Cell; ++t)
            if (settings.Limit[t] != 0)
                return false;
        return true;
    }

    public IncrementalClassicEvaluator(ClassicSettings settings, Grid3 state)
    {
        if (state.SizeX != settings.SizeX || state.SizeY != settings.SizeY || state.SizeZ != settings.SizeZ)
            throw new ArgumentException("state shape does not match settings", nameof(state));
        _settings = settings;
        State = state;
        _s = state.Data;
        _sizeX = settings.SizeX; _sizeY = settings.SizeY; _sizeZ = settings.SizeZ;
        _n = settings.Volume;
        _mult = new int[_n]; _modMult = new int[_n]; _rules = new int[_n];
        _isActive = new bool[_n]; _inLine = new int[_n]; _invalid = new bool[_n];
        _stamp = new int[_n];
        Rebuild();
    }

    private int Index(int x, int y, int z) => (x * _sizeY + y) * _sizeZ + z;
    private bool InBounds(int x, int y, int z) => (uint)x < (uint)_sizeX && (uint)y < (uint)_sizeY && (uint)z < (uint)_sizeZ;
    /// <summary>Flat index of a possibly out-of-range position, or −1 when it is outside the grid (casing).</summary>
    private int At(int x, int y, int z) => InBounds(x, y, z) ? Index(x, y, z) : -1;
    private (int x, int y, int z) Coords(int i) => (i / (_sizeY * _sizeZ), i / _sizeZ % _sizeY, i % _sizeZ);

    private static readonly (int dx, int dy, int dz)[] Dirs = { (-1, 0, 0), (1, 0, 0), (0, -1, 0), (0, 1, 0), (0, 0, -1), (0, 0, 1) };

    private static int RuleOf(int tile) => tile < Active ? tile : tile < Cell ? tile - Active : -1;

    private static int PassOf(int rule) => rule switch
    {
        Redstone or Lapis or Enderium or Cryotheum => 2,
        Water or Quartz or Glowstone or Helium or Emerald or Tin or Magnesium => 3,
        Gold or Diamond or Copper => 4,
        Iron => 5,
        _ => 0,
    };

    // ---------------------------------------------------------------- journaled writes

    private void SetState(int i, int v) { _journal.Add((ArrState, i, _s[i])); _s[i] = v; }
    private void SetMult(int i, int v) { _journal.Add((ArrMult, i, _mult[i])); _mult[i] = v; }
    private void SetModMult(int i, int v) { _journal.Add((ArrModMult, i, _modMult[i])); _modMult[i] = v; }
    private void SetActive(int i, bool v) { _journal.Add((ArrIsActive, i, _isActive[i] ? 1 : 0)); _isActive[i] = v; }
    private void SetRules(int i, int v) { _journal.Add((ArrRules, i, _rules[i])); _rules[i] = v; }
    private void AddInLine(int i, int d) { _journal.Add((ArrInLine, i, _inLine[i])); _inLine[i] += d; }

    private void SetInvalid(int i, bool v)
    {
        if (_invalid[i] == v) return;
        _journal.Add((ArrInvalid, i, _invalid[i] ? 1 : 0));
        _invalid[i] = v;
        int d = v ? 1 : -1;
        _invalidCount += d;
        _invalidByTile[_s[i]] += d;
    }

    // ---------------------------------------------------------------- full rebuild

    /// <summary>Recomputes everything from the grid (used after a restart). O(N), same cost as one scalar evaluation.</summary>
    public void Rebuild()
    {
        _journal.Clear();
        _canUndo = false;
        Array.Clear(_mult); Array.Clear(_modMult); Array.Clear(_isActive); Array.Clear(_inLine); Array.Clear(_invalid);
        Array.Clear(_countByTile); Array.Clear(_activeCountByTile); Array.Clear(_invalidByTile);
        _breed = 0; _cellMultSum = 0; _cellHeatSum = 0; _modMultSum = 0; _invalidCount = 0;
        for (int i = 0; i < _n; ++i)
        {
            int tile = _s[i];
            ++_countByTile[tile];
            _rules[i] = RuleOf(tile);
        }
        for (int i = 0; i < _n; ++i)
            if (_s[i] == Cell)
                AddCell(i);
        for (int i = 0; i < _n; ++i)
            if (_s[i] == Moderator)
                RecomputeModerator(i);
        for (int pass = 2; pass <= 5; ++pass)
            for (int i = 0; i < _n; ++i)
                if (PassOf(_rules[i]) == pass)
                    RecomputeCooler(i);
        _journal.Clear();
    }

    // ---------------------------------------------------------------- per-tile recomputation

    /// <summary>Walks a ray from a cell; true if another cell is reached through moderators within reach. Adds <paramref name="delta"/> to the in-line counts along a successful ray.</summary>
    private bool Ray(int x, int y, int z, int dx, int dy, int dz, int delta)
    {
        for (int n = 0; n <= NeutronReach; ++n)
        {
            x += dx; y += dy; z += dz;
            int at = At(x, y, z);
            if (at < 0) return false;
            int tile = _s[at];
            if (tile == Cell)
            {
                if (delta != 0)
                    for (int i = 0; i < n; ++i)
                    {
                        x -= dx; y -= dy; z -= dz;
                        int p = At(x, y, z);
                        AddInLine(p, delta);
                        _modDirty.Add(p);
                    }
                return true;
            }
            if (tile != Moderator) return false;
        }
        return false;
    }

    private int CountMult(int i, int delta)
    {
        var (x, y, z) = Coords(i);
        int m = 1;
        foreach (var (dx, dy, dz) in Dirs)
            if (Ray(x, y, z, dx, dy, dz, delta)) ++m;
        return m;
    }

    /// <summary>Accounts a cell at i (which must be a Cell in the current grid): multiplier, rays, totals.</summary>
    private void AddCell(int i)
    {
        int m = CountMult(i, +1);
        SetMult(i, m);
        ++_breed;
        _cellMultSum += m;
        _cellHeatSum += m * (m + 1) / 2;
    }

    /// <summary>Removes a cell's accounting; the grid must still show the cell (rays are walked in the current grid).</summary>
    private void RemoveCell(int i)
    {
        int m = _mult[i];
        CountMult(i, -1);
        SetMult(i, 0);
        --_breed;
        _cellMultSum -= m;
        _cellHeatSum -= m * (m + 1) / 2;
    }

    private int NeighbourMultSum(int i)
    {
        var (x, y, z) = Coords(i);
        int sum = 0;
        foreach (var (dx, dy, dz) in Dirs)
        {
            int c = At(x + dx, y + dy, z + dz);
            if (c >= 0) sum += _mult[c];
        }
        return sum;
    }

    /// <summary>Recomputes a moderator's contribution and validity; returns true if its active state flipped.</summary>
    private bool RecomputeModerator(int i)
    {
        int m = NeighbourMultSum(i);
        int old = _modMult[i];
        if (m != old)
        {
            SetModMult(i, m);
            _modMultSum += m - old;
        }
        bool active = m != 0;
        bool flipped = active != _isActive[i];
        if (flipped) SetActive(i, active);
        SetInvalid(i, !active && _inLine[i] == 0);
        return flipped;
    }

    private bool IsActiveTile(int tile, int x, int y, int z)
    {
        int i = At(x, y, z);
        return i >= 0 && _s[i] == tile && _isActive[i];
    }

    private int CountActiveNeighbours(int tile, int x, int y, int z)
    {
        int c = 0;
        foreach (var (dx, dy, dz) in Dirs)
            if (IsActiveTile(tile, x + dx, y + dy, z + dz)) ++c;
        return c;
    }

    private bool IsTile(int tile, int x, int y, int z)
    {
        int i = At(x, y, z);
        return i >= 0 && _s[i] == tile;
    }

    private int CountNeighbours(int tile, int x, int y, int z)
    {
        int c = 0;
        foreach (var (dx, dy, dz) in Dirs)
            if (IsTile(tile, x + dx, y + dy, z + dz)) ++c;
        return c;
    }

    private int CountCasing(int x, int y, int z)
    {
        int c = 0;
        foreach (var (dx, dy, dz) in Dirs)
            if (!InBounds(x + dx, y + dy, z + dz)) ++c;
        return c;
    }

    /// <summary>The scalar cooler rules, one tile at a time (see ClassicEvaluator passes 2–5).</summary>
    private bool CoolerRule(int rule, int x, int y, int z) => rule switch
    {
        Redstone => CountNeighbours(Cell, x, y, z) != 0,
        Lapis => CountNeighbours(Cell, x, y, z) != 0 && CountCasing(x, y, z) != 0,
        Enderium => CountCasing(x, y, z) == 3 && (x == 0 || x == _sizeX - 1) && (y == 0 || y == _sizeY - 1) && (z == 0 || z == _sizeZ - 1),
        Cryotheum => CountNeighbours(Cell, x, y, z) >= 2,
        Water => CountNeighbours(Cell, x, y, z) != 0 || CountActiveNeighbours(Moderator, x, y, z) != 0,
        Quartz => CountActiveNeighbours(Moderator, x, y, z) != 0,
        Glowstone => CountActiveNeighbours(Moderator, x, y, z) >= 2,
        Helium => CountActiveNeighbours(Redstone, x, y, z) == 1 && CountCasing(x, y, z) != 0,
        Emerald => CountActiveNeighbours(Moderator, x, y, z) != 0 && CountNeighbours(Cell, x, y, z) != 0,
        Tin => (IsActiveTile(Lapis, x - 1, y, z) && IsActiveTile(Lapis, x + 1, y, z)) ||
               (IsActiveTile(Lapis, x, y - 1, z) && IsActiveTile(Lapis, x, y + 1, z)) ||
               (IsActiveTile(Lapis, x, y, z - 1) && IsActiveTile(Lapis, x, y, z + 1)),
        Magnesium => CountActiveNeighbours(Moderator, x, y, z) != 0 && CountCasing(x, y, z) != 0,
        Gold => CountActiveNeighbours(Water, x, y, z) != 0 && CountActiveNeighbours(Redstone, x, y, z) != 0,
        Diamond => CountActiveNeighbours(Water, x, y, z) != 0 && CountActiveNeighbours(Quartz, x, y, z) != 0,
        Copper => CountActiveNeighbours(Glowstone, x, y, z) != 0,
        Iron => CountActiveNeighbours(Gold, x, y, z) != 0,
        _ => false,
    };

    /// <summary>Recomputes a cooler's activation; returns true if it flipped.</summary>
    private bool RecomputeCooler(int i)
    {
        var (x, y, z) = Coords(i);
        bool active = CoolerRule(_rules[i], x, y, z);
        bool flipped = active != _isActive[i];
        if (flipped)
        {
            SetActive(i, active);
            _activeCountByTile[_s[i]] += active ? 1 : -1;
        }
        SetInvalid(i, !active);
        return flipped;
    }

    // ---------------------------------------------------------------- mutation

    /// <summary>Queues a tile change for the next <see cref="Apply"/>. Positions may repeat; the last value wins.</summary>
    public void Set(int x, int y, int z, int tile)
    {
        int i = Index(x, y, z);
        for (int k = 0; k < _pending.Count; ++k)
            if (_pending[k].index == i)
            {
                _pending[k] = (i, _pending[k].oldTile, tile);
                return;
            }
        _pending.Add((i, _s[i], tile));
    }

    private void Touch(List<int> list, int i)
    {
        if (_stamp[i] == _gen) return;
        _stamp[i] = _gen;
        list.Add(i);
    }

    private void TouchNeighbours(List<int> list, int i)
    {
        var (x, y, z) = Coords(i);
        foreach (var (dx, dy, dz) in Dirs)
        {
            int c = At(x + dx, y + dy, z + dz);
            if (c >= 0) Touch(list, c);
        }
    }

    /// <summary>Adds to <paramref name="list"/> every cell whose ray passes through position i (in the current grid).</summary>
    private void CellsThrough(List<int> list, int i)
    {
        var (x, y, z) = Coords(i);
        foreach (var (dx, dy, dz) in Dirs)
        {
            int cx = x, cy = y, cz = z;
            for (int n = 0; n < NeutronReach + 1; ++n)
            {
                cx += dx; cy += dy; cz += dz;
                int c = At(cx, cy, cz);
                if (c < 0) break;
                int tile = _s[c];
                if (tile == Cell) { Touch(list, c); break; }
                if (tile != Moderator) break;
            }
        }
    }

    /// <summary>
    /// Applies the queued changes incrementally and records an undo journal. Afterwards the counters
    /// and per-tile caches equal what <see cref="Rebuild"/> would produce for the new grid.
    /// </summary>
    public void Apply()
    {
        _journal.Clear();
        _canUndo = true;
        _uBreed = _breed; _uCellMultSum = _cellMultSum; _uCellHeatSum = _cellHeatSum; _uModMultSum = _modMultSum; _uInvalidCount = _invalidCount;
        Array.Copy(_countByTile, _uCountByTile, _countByTile.Length);
        Array.Copy(_activeCountByTile, _uActiveCountByTile, _activeCountByTile.Length);
        Array.Copy(_invalidByTile, _uInvalidByTile, _invalidByTile.Length);
        _pending.RemoveAll(p => p.oldTile == p.newTile);
        if (_pending.Count == 0) return;

        // Phase A (old grid): find every cell whose multiplier can change and remove its accounting;
        // remove the old contributions of the changed tiles themselves.
        ++_gen;
        _dirtyCells.Clear(); _modDirty.Clear(); _changed.Clear();
        foreach (var (i, _, _) in _pending)
        {
            if (_s[i] == Cell) Touch(_dirtyCells, i);
            CellsThrough(_dirtyCells, i);
        }
        foreach (int c in _dirtyCells)
            RemoveCell(c);
        foreach (var (i, oldTile, _) in _pending)
        {
            if (oldTile == Moderator)
            {
                if (_modMult[i] != 0) { _modMultSum -= _modMult[i]; SetModMult(i, 0); }
                if (_isActive[i]) SetActive(i, false);
            }
            else if (oldTile < Cell && _isActive[i])
            {
                --_activeCountByTile[oldTile];
                SetActive(i, false);
            }
            SetInvalid(i, false);
            --_countByTile[oldTile];
        }

        // Write the new tiles.
        foreach (var (i, _, newTile) in _pending)
        {
            SetState(i, newTile);
            SetRules(i, RuleOf(newTile));
            ++_countByTile[newTile];
            _changed.Add(i);
        }

        // Phase B (new grid): re-account every cell that is dirty or reachable, then the moderators
        // around them, then the coolers pass by pass.
        ++_gen;
        var cells = _candidates; cells.Clear();
        foreach (int c in _dirtyCells)
            if (_s[c] == Cell) Touch(cells, c);
        foreach (var (i, _, newTile) in _pending)
        {
            if (newTile == Cell) Touch(cells, i);
            CellsThrough(cells, i);
        }
        foreach (int c in cells)
            AddCell(c);

        ++_gen;
        var mods = _dirtyCells; mods.Clear(); // reuse
        foreach (int p in _modDirty) Touch(mods, p);
        foreach (int c in cells) { Touch(mods, c); TouchNeighbours(mods, c); }
        foreach (int i in _changed) { Touch(mods, i); TouchNeighbours(mods, i); }
        foreach (int m in mods)
            if (_s[m] == Moderator && RecomputeModerator(m))
                _changed.Add(m);

        for (int pass = 2; pass <= 5; ++pass)
        {
            ++_gen;
            cells.Clear();
            int count = _changed.Count;
            for (int k = 0; k < count; ++k)
            {
                int i = _changed[k];
                Touch(cells, i);
                TouchNeighbours(cells, i);
            }
            foreach (int i in cells)
                if (PassOf(_rules[i]) == pass && RecomputeCooler(i))
                    _changed.Add(i);
        }
        _pending.Clear();
    }

    /// <summary>Reverts the last <see cref="Apply"/> (grid, caches and counters).</summary>
    public void Undo()
    {
        if (!_canUndo) throw new InvalidOperationException("nothing to undo");
        for (int k = _journal.Count - 1; k >= 0; --k)
        {
            var (arr, i, old) = _journal[k];
            switch (arr)
            {
                case ArrState: _s[i] = old; break;
                case ArrMult: _mult[i] = old; break;
                case ArrModMult: _modMult[i] = old; break;
                case ArrIsActive: _isActive[i] = old != 0; break;
                case ArrInLine: _inLine[i] = old; break;
                case ArrInvalid: _invalid[i] = old != 0; break;
                case ArrRules: _rules[i] = old; break;
            }
        }
        _journal.Clear();
        _breed = _uBreed; _cellMultSum = _uCellMultSum; _cellHeatSum = _uCellHeatSum; _modMultSum = _uModMultSum; _invalidCount = _uInvalidCount;
        Array.Copy(_uCountByTile, _countByTile, _countByTile.Length);
        Array.Copy(_uActiveCountByTile, _activeCountByTile, _activeCountByTile.Length);
        Array.Copy(_uInvalidByTile, _invalidByTile, _invalidByTile.Length);
        _canUndo = false;
        _pending.Clear();
    }

    /// <summary>Discards the undo journal, committing the last <see cref="Apply"/>.</summary>
    public void Commit()
    {
        _journal.Clear();
        _canUndo = false;
    }

    // ---------------------------------------------------------------- results

    /// <summary>Fills the raw totals (and optionally the invalid-tile list, in scan order) and computes the derived metrics.</summary>
    public void WriteTo(ClassicEvaluation e, bool withInvalidList)
    {
        e.Breed = _breed;
        e.PowerMult = PowerMult;
        e.HeatMult = HeatMult;
        e.Cooling = Cooling;
        e.InvalidTiles.Clear();
        if (withInvalidList)
            for (int i = 0; i < _n; ++i)
                if (_invalid[i])
                    e.InvalidTiles.Add(new Coord(i / (_sizeY * _sizeZ), i / _sizeZ % _sizeY, i % _sizeZ));
        e.Compute(_settings);
    }

    /// <summary>Per-tile intermediates, exposed for the differential tests.</summary>
    public int MultAt(int i) => _mult[i];
    public bool IsActiveAt(int i) => _isActive[i];
    public bool IsInvalidAt(int i) => _invalid[i];
    public bool IsModeratorInLineAt(int i) => _inLine[i] != 0;
}
