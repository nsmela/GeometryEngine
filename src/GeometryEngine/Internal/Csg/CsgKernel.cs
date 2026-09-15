namespace GeometryEngine.Internal.Csg;

/// <summary>
/// The regularised boolean operations, following Thibault and Naylor's BSP
/// formulation. Each operation is a short sequence of value transformations:
/// clip the two solids against one another, invert where the set algebra calls
/// for a complement, and merge what survives.
///
/// Every operation is run through <see cref="DeepStack"/> because the tree walks
/// beneath it recurse to the depth of the BSP, which can outgrow the default
/// thread stack on finely tessellated input.
/// </summary>
internal static class CsgKernel
{
    public static List<CsgPolygon> Union(
        IReadOnlyList<CsgPolygon> left,
        IReadOnlyList<CsgPolygon> right,
        Tolerance tolerance) =>
        DeepStack.Run(() => UnionCore(left, right, tolerance));

    public static List<CsgPolygon> Subtract(
        IReadOnlyList<CsgPolygon> left,
        IReadOnlyList<CsgPolygon> right,
        Tolerance tolerance) =>
        DeepStack.Run(() => SubtractCore(left, right, tolerance));

    public static List<CsgPolygon> Intersect(
        IReadOnlyList<CsgPolygon> left,
        IReadOnlyList<CsgPolygon> right,
        Tolerance tolerance) =>
        DeepStack.Run(() => IntersectCore(left, right, tolerance));

    private static List<CsgPolygon> UnionCore(
        IReadOnlyList<CsgPolygon> left,
        IReadOnlyList<CsgPolygon> right,
        Tolerance tolerance)
    {
        var a = BspNode.Build(left, tolerance);
        var b = BspNode.Build(right, tolerance);

        a = a.ClippedTo(b, tolerance);
        b = b.ClippedTo(a, tolerance);

        // Remove the faces of B that merely coincide with faces of A.
        b = b.Inverted().ClippedTo(a, tolerance).Inverted();

        return a.Insert(b.AllPolygons(), tolerance).AllPolygons();
    }

    private static List<CsgPolygon> SubtractCore(
        IReadOnlyList<CsgPolygon> left,
        IReadOnlyList<CsgPolygon> right,
        Tolerance tolerance)
    {
        // A minus B is the complement of (complement of A, united with B).
        var a = BspNode.Build(left, tolerance).Inverted();
        var b = BspNode.Build(right, tolerance);

        a = a.ClippedTo(b, tolerance);
        b = b.ClippedTo(a, tolerance);
        b = b.Inverted().ClippedTo(a, tolerance).Inverted();

        return a.Insert(b.AllPolygons(), tolerance).Inverted().AllPolygons();
    }

    private static List<CsgPolygon> IntersectCore(
        IReadOnlyList<CsgPolygon> left,
        IReadOnlyList<CsgPolygon> right,
        Tolerance tolerance)
    {
        // A and B is the complement of (complement of A, united with complement of B).
        var a = BspNode.Build(left, tolerance).Inverted();
        var b = BspNode.Build(right, tolerance).ClippedTo(a, tolerance).Inverted();

        a = a.ClippedTo(b, tolerance);
        b = b.ClippedTo(a, tolerance);

        return a.Insert(b.AllPolygons(), tolerance).Inverted().AllPolygons();
    }
}
