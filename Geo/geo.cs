using NetTopologySuite;
using NetTopologySuite.Geometries;

namespace BelediyeCBS;

/// <summary>Geometri fabrikası ve GeoJSON çıktı yardımcıları.</summary>
public static class Geo
{
    // SRID 4326 (WGS84): tüm geometriler aynı koordinat sisteminde tutulur.
    public static readonly GeometryFactory Factory =
        NtsGeometryServices.Instance.CreateGeometryFactory(4326);

    public static Point CreatePoint(double lat, double lon) =>
        Factory.CreatePoint(new Coordinate(lon, lat)); // GeoJSON/PostGIS sırası: x = boylam, y = enlem

    public static object Collection(IEnumerable<object> features) =>
        new { type = "FeatureCollection", features };

    public static object RequestFeature(ServiceRequest r) => new
    {
        type = "Feature",
        geometry = new { type = "Point", coordinates = new[] { r.Location.X, r.Location.Y } },
        properties = new
        {
            id = r.Id,
            title = r.Title,
            description = r.Description,
            category = r.Category.ToString(),
            status = r.Status.ToString(),
            neighborhood = r.Neighborhood?.Name,
            aiSuggested = r.AiSuggested,
            createdAt = r.CreatedAt
        }
    };

    public static object PolygonGeometry(Polygon p) => new
    {
        type = "Polygon",
        coordinates = new[]
        {
            p.ExteriorRing.Coordinates.Select(c => new[] { c.X, c.Y }).ToArray()
        }
    };
}