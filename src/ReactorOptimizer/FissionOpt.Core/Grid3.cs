namespace FissionOpt.Core;

/// <summary>
/// A 3D int grid stored as a flat, row-major array (x-major, then y, then z),
/// the same memory layout as <c>xt::xtensor&lt;int, 3&gt;</c> with shape {sizeX, sizeY, sizeZ}.
/// This is the "state" type in both evaluators; only the value net files need real tensor ops.
/// </summary>
public sealed class Grid3
{
    public int SizeX { get; }
    public int SizeY { get; }
    public int SizeZ { get; }
    public int Length => Data.Length;

    /// <summary>Backing storage. Exposed so hot loops can index it directly.</summary>
    public int[] Data { get; }

    public Grid3(int sizeX, int sizeY, int sizeZ)
    {
        if (sizeX <= 0 || sizeY <= 0 || sizeZ <= 0)
            throw new ArgumentOutOfRangeException(nameof(sizeX), "grid dimensions must be positive");
        SizeX = sizeX;
        SizeY = sizeY;
        SizeZ = sizeZ;
        Data = new int[sizeX * sizeY * sizeZ];
    }

    public Grid3(int sizeX, int sizeY, int sizeZ, int fill) : this(sizeX, sizeY, sizeZ)
    {
        Fill(fill);
    }

    public int Index(int x, int y, int z) => (x * SizeY + y) * SizeZ + z;

    public int this[int x, int y, int z]
    {
        get => Data[Index(x, y, z)];
        set => Data[Index(x, y, z)] = value;
    }

    public int this[Coord c]
    {
        get => Data[Index(c.X, c.Y, c.Z)];
        set => Data[Index(c.X, c.Y, c.Z)] = value;
    }

    public bool InBounds(int x, int y, int z) =>
        (uint)x < (uint)SizeX && (uint)y < (uint)SizeY && (uint)z < (uint)SizeZ;

    public void Fill(int value) => Array.Fill(Data, value);

    public bool SameShape(Grid3 other) =>
        SizeX == other.SizeX && SizeY == other.SizeY && SizeZ == other.SizeZ;

    public void CopyFrom(Grid3 other)
    {
        if (!SameShape(other))
            throw new ArgumentException("grid shapes differ", nameof(other));
        Array.Copy(other.Data, Data, Data.Length);
    }

    public Grid3 Clone()
    {
        var g = new Grid3(SizeX, SizeY, SizeZ);
        Array.Copy(Data, g.Data, Data.Length);
        return g;
    }
}
