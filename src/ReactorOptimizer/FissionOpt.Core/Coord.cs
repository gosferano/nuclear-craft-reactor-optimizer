namespace FissionOpt.Core;

/// <summary>A tile position. Mirrors <c>std::tuple&lt;int,int,int&gt;</c> in the C++.</summary>
public readonly record struct Coord(int X, int Y, int Z);
