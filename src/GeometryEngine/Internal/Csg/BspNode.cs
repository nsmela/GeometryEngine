namespace GeometryEngine.Internal.Csg;

/// <summary>
/// A node of a binary space partition tree, holding the polygons that lie on its
/// dividing plane and the sub-trees in front of and behind it.
///
/// The node is immutable: <see cref="Insert"/>, <see cref="Inverted"/> and
/// <see cref="ClippedTo"/> all return new trees and leave the receiver alone.
/// That is what lets the boolean operations read as a sequence of values rather
/// than a sequence of in-place mutations, and it is why the same operand can be
/// fed to several operations without defensive copying.
/// </summary>
internal sealed class BspNode
{
    public static readonly BspNode Empty = new(
        Maybe<Plane>.None(),
        ImmutableArray<CsgPolygon>.Empty,
        Maybe<BspNode>.None(),
        Maybe<BspNode>.None());

    private readonly Maybe<Plane> _divider;
    private readonly ImmutableArray<CsgPolygon> _polygons;
    private readonly Maybe<BspNode> _front;
    private readonly Maybe<BspNode> _back;

    private BspNode(Maybe<Plane> divider, ImmutableArray<CsgPolygon> polygons, Maybe<BspNode> front, Maybe<BspNode> back)
    {
        _divider = divider;
        _polygons = polygons;
        _front = front;
        _back = back;
    }

    public static BspNode Build(IReadOnlyList<CsgPolygon> polygons, Tolerance tolerance) =>
        Empty.Insert(polygons, tolerance);

    /// <summary>Adds polygons to the tree, returning the enlarged tree.</summary>
    public BspNode Insert(IReadOnlyList<CsgPolygon> polygons, Tolerance tolerance)
    {
        if (polygons.Count == 0)
        {
            return this;
        }

        var divider = _divider.HasValue ? _divider.Value : ChooseDivider(polygons, tolerance);

        var here = ImmutableArray.CreateBuilder<CsgPolygon>();
        here.AddRange(_polygons);

        var coplanarFront = new List<CsgPolygon>();
        var coplanarBack = new List<CsgPolygon>();
        var inFront = new List<CsgPolygon>();
        var behind = new List<CsgPolygon>();

        foreach (var polygon in polygons)
        {
            PolygonSplitter.Split(divider, polygon, tolerance, coplanarFront, coplanarBack, inFront, behind);
        }

        here.AddRange(coplanarFront);
        here.AddRange(coplanarBack);

        var front = inFront.Count == 0
            ? _front
            : Maybe<BspNode>.Some(_front.GetValueOrDefault(Empty).Insert(inFront, tolerance));

        var back = behind.Count == 0
            ? _back
            : Maybe<BspNode>.Some(_back.GetValueOrDefault(Empty).Insert(behind, tolerance));

        return new BspNode(Maybe<Plane>.Some(divider), here.ToImmutable(), front, back);
    }

    /// <summary>Turns the solid inside out: every face flips and front and back swap.</summary>
    public BspNode Inverted()
    {
        var flipped = ImmutableArray.CreateBuilder<CsgPolygon>(_polygons.Length);
        foreach (var polygon in _polygons)
        {
            flipped.Add(polygon.Flipped());
        }

        return new BspNode(
            _divider.Map(plane => plane.Flipped()),
            flipped.MoveToImmutable(),
            _back.Map(node => node.Inverted()),
            _front.Map(node => node.Inverted()));
    }

    /// <summary>Discards the parts of <paramref name="polygons"/> that lie inside this solid.</summary>
    public List<CsgPolygon> ClipPolygons(IReadOnlyList<CsgPolygon> polygons, Tolerance tolerance)
    {
        if (!_divider.HasValue)
        {
            return [.. polygons];
        }

        var inFront = new List<CsgPolygon>();
        var behind = new List<CsgPolygon>();

        foreach (var polygon in polygons)
        {
            // Coplanar pieces follow the side their own normal agrees with.
            PolygonSplitter.Split(_divider.Value, polygon, tolerance, inFront, behind, inFront, behind);
        }

        var kept = _front.HasValue ? _front.Value.ClipPolygons(inFront, tolerance) : inFront;

        if (_back.HasValue)
        {
            kept.AddRange(_back.Value.ClipPolygons(behind, tolerance));
        }

        return kept;
    }

    /// <summary>Removes everything of this solid that lies inside <paramref name="other"/>.</summary>
    public BspNode ClippedTo(BspNode other, Tolerance tolerance) =>
        new(
            _divider,
            [.. other.ClipPolygons(_polygons, tolerance)],
            _front.Map(node => node.ClippedTo(other, tolerance)),
            _back.Map(node => node.ClippedTo(other, tolerance)));

    /// <summary>Every polygon still held anywhere in the tree.</summary>
    public List<CsgPolygon> AllPolygons()
    {
        var collected = new List<CsgPolygon>();
        Collect(collected);
        return collected;
    }

    private void Collect(List<CsgPolygon> into)
    {
        into.AddRange(_polygons);

        if (_front.HasValue)
        {
            _front.Value.Collect(into);
        }

        if (_back.HasValue)
        {
            _back.Value.Collect(into);
        }
    }

    /// <summary>
    /// Picks a dividing plane. Taking the first polygon's plane, as the textbook
    /// version does, degenerates into a linked list on structured input such as a
    /// lathe-ordered sphere. Sampling a handful of candidates and preferring the
    /// one that splits fewest polygons and balances best keeps the tree shallow,
    /// and sampling by index rather than at random keeps the result reproducible.
    /// </summary>
    private static Plane ChooseDivider(IReadOnlyList<CsgPolygon> polygons, Tolerance tolerance)
    {
        const int Candidates = 8;
        var stride = Math.Max(1, polygons.Count / Candidates);

        var best = polygons[0].Plane;
        var bestScore = int.MaxValue;

        for (var i = 0; i < polygons.Count; i += stride)
        {
            var candidate = polygons[i].Plane;
            var score = ScoreDivider(candidate, polygons, stride, tolerance);

            if (score >= bestScore)
            {
                continue;
            }

            bestScore = score;
            best = candidate;
        }

        return best;
    }

    private static int ScoreDivider(Plane candidate, IReadOnlyList<CsgPolygon> polygons, int stride, Tolerance tolerance)
    {
        var front = 0;
        var back = 0;
        var spanning = 0;

        for (var i = 0; i < polygons.Count; i += stride)
        {
            var combined = PointSide.Coplanar;
            foreach (var vertex in polygons[i].Vertices)
            {
                combined |= candidate.Classify(vertex, tolerance);
            }

            switch (combined)
            {
                case PointSide.Front:
                    front++;
                    break;
                case PointSide.Back:
                    back++;
                    break;
                case PointSide.Coplanar:
                    break;
                default:
                    spanning++;
                    break;
            }
        }

        return (spanning * 4) + Math.Abs(front - back);
    }
}
