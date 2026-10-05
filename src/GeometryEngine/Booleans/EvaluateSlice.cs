namespace GeometryEngine.Booleans;

/// <summary>Stub: the walk both interpreters will share.</summary>
internal static class SolidWalk
{
    public static IReadOnlyList<Solid> PostOrder(Solid root) => [];

    public static IReadOnlyList<IMesh> Leaves(Solid root) => [];
}
