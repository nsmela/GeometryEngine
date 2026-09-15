using System.Collections.Immutable;

namespace GeometryEngine.Core.Geometry;

/// <summary>
/// A polygon in the plane: one outer boundary and any number of holes. Winding is not part
/// of the contract - every operation that cares about orientation settles it itself, because
/// the outlines that arrive here (a mesh's shadow, a glyph, a convex hull) do not agree.
/// </summary>
public sealed record PlanarPolygon(ImmutableArray<Vec2> Outer, ImmutableArray<ImmutableArray<Vec2>> Holes)
{
    public static PlanarPolygon FromOuter(ImmutableArray<Vec2> outer) => new(outer, ImmutableArray<ImmutableArray<Vec2>>.Empty);

    /// <summary>Signed area of the outer boundary: positive when it winds counter-clockwise.</summary>
    public double SignedArea => SignedAreaOf(Outer);

    public static double SignedAreaOf(ImmutableArray<Vec2> ring)
    {
        var sum = 0.0;
        for (var i = 0; i < ring.Length; i++)
        {
            sum += ring[i].Cross(ring[(i + 1) % ring.Length]);
        }

        return sum * 0.5;
    }

    // Records compare ImmutableArray by reference; compare the points instead so two polygons
    // with the same outline are equal, as a value object should be.
    public bool Equals(PlanarPolygon? other) =>
        other is not null &&
        Outer.SequenceEqual(other.Outer) &&
        Holes.Length == other.Holes.Length &&
        Holes.Zip(other.Holes).All(pair => pair.First.SequenceEqual(pair.Second));

    public override int GetHashCode() => HashCode.Combine(Outer.Length, Holes.Length);
}
