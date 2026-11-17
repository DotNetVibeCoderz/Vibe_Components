using NetTopologySuite.Geometries.Prepared;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Geometry;

/// <summary>Binary spatial predicates (DE-9IM based) used by spatial joins and queries.</summary>
public enum SpatialPredicate
{
    Intersects, Contains, Within, Touches, Crosses, Overlaps, Covers, CoveredBy, Disjoint, Equals
}

public static class SpatialPredicateExtensions
{
    /// <summary>Evaluates <paramref name="predicate"/> as <c>a.predicate(b)</c>.</summary>
    public static bool Evaluate(this SpatialPredicate predicate, NtsGeometry a, NtsGeometry b) => predicate switch
    {
        SpatialPredicate.Intersects => a.Intersects(b),
        SpatialPredicate.Contains => a.Contains(b),
        SpatialPredicate.Within => a.Within(b),
        SpatialPredicate.Touches => a.Touches(b),
        SpatialPredicate.Crosses => a.Crosses(b),
        SpatialPredicate.Overlaps => a.Overlaps(b),
        SpatialPredicate.Covers => a.Covers(b),
        SpatialPredicate.CoveredBy => a.CoveredBy(b),
        SpatialPredicate.Disjoint => a.Disjoint(b),
        SpatialPredicate.Equals => a.EqualsTopologically(b),
        _ => throw new ArgumentOutOfRangeException(nameof(predicate))
    };

    /// <summary>Evaluates using a prepared geometry for <c>a</c> (much faster when <c>a</c> is reused).</summary>
    public static bool Evaluate(this SpatialPredicate predicate, IPreparedGeometry a, NtsGeometry b) => predicate switch
    {
        SpatialPredicate.Intersects => a.Intersects(b),
        SpatialPredicate.Contains => a.Contains(b),
        SpatialPredicate.Within => a.Within(b),
        SpatialPredicate.Touches => a.Touches(b),
        SpatialPredicate.Crosses => a.Crosses(b),
        SpatialPredicate.Overlaps => a.Overlaps(b),
        SpatialPredicate.Covers => a.Covers(b),
        SpatialPredicate.CoveredBy => a.CoveredBy(b),
        SpatialPredicate.Disjoint => a.Disjoint(b),
        SpatialPredicate.Equals => a.Geometry.EqualsTopologically(b),
        _ => throw new ArgumentOutOfRangeException(nameof(predicate))
    };

    /// <summary>The inverse predicate: <c>a.P(b) == b.Inverse(P)(a)</c>.</summary>
    public static SpatialPredicate Inverse(this SpatialPredicate predicate) => predicate switch
    {
        SpatialPredicate.Contains => SpatialPredicate.Within,
        SpatialPredicate.Within => SpatialPredicate.Contains,
        SpatialPredicate.Covers => SpatialPredicate.CoveredBy,
        SpatialPredicate.CoveredBy => SpatialPredicate.Covers,
        _ => predicate
    };

    public static SpatialPredicate Parse(string name) => name.Replace("_", "").ToLowerInvariant() switch
    {
        "intersects" => SpatialPredicate.Intersects,
        "contains" => SpatialPredicate.Contains,
        "within" => SpatialPredicate.Within,
        "touches" => SpatialPredicate.Touches,
        "crosses" => SpatialPredicate.Crosses,
        "overlaps" => SpatialPredicate.Overlaps,
        "covers" => SpatialPredicate.Covers,
        "coveredby" => SpatialPredicate.CoveredBy,
        "disjoint" => SpatialPredicate.Disjoint,
        "equals" => SpatialPredicate.Equals,
        _ => throw new ArgumentException($"Unknown spatial predicate '{name}'.")
    };
}
