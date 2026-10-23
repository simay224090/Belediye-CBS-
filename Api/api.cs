using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace BelediyeCBS;

public record NewRequest(string Title, string? Description, double Lat, double Lon, Category? Category);
public record StatusDto(Status Status);

public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/requests", GetRequests);
        api.MapGet("/requests/nearby", GetNearby);
        api.MapPost("/requests", CreateRequest);
        api.MapPatch("/requests/{id:int}/status", UpdateStatus);
        api.MapGet("/neighborhoods", GetNeighborhoods);

        return app;
    }

    // Talepleri GeoJSON olarak getirir (kategori / durum / bbox filtresi).
    private static async Task<IResult> GetRequests(AppDb db, Category? category, Status? status, string? bbox)
    {
        var query = db.Requests.Include(r => r.Neighborhood).AsQueryable();

        if (category.HasValue) query = query.Where(r => r.Category == category.Value);
        if (status.HasValue) query = query.Where(r => r.Status == status.Value);

        if (!string.IsNullOrWhiteSpace(bbox))
        {
            if (!TryParseBbox(bbox, out var box))
                return Results.BadRequest(new { error = "bbox formatı: minLon,minLat,maxLon,maxLat" });

            query = query.Where(r => r.Location.Within(box));
        }

        var items = await query.OrderByDescending(r => r.CreatedAt).ToListAsync();
        return Results.Ok(Geo.Collection(items.Select(Geo.RequestFeature)));
    }

    // Verilen noktaya X metre yakınlıktaki talepler (PostGIS ST_DWithin, geography => metre).
    private static async Task<IResult> GetNearby(AppDb db, double lat, double lon, double radius = 500)
    {
        if (radius is <= 0 or > 20000)
            return Results.BadRequest(new { error = "radius 1-20000 metre olmalı" });

        var center = Geo.CreatePoint(lat, lon);

        var items = await db.Requests
            .FromSqlInterpolated($@"
                SELECT * FROM ""Requests""
                WHERE ST_DWithin(
                    ""Location""::geography,
                    ST_SetSRID(ST_MakePoint({lon}, {lat}), 4326)::geography,
                    {radius})")
            .Include(r => r.Neighborhood)
            .OrderBy(r => r.Location.Distance(center))
            .ToListAsync();

        return Results.Ok(Geo.Collection(items.Select(Geo.RequestFeature)));
    }

    // Yeni talep: mahalle tespiti (point-in-polygon) + kategori boşsa AI ile sınıflandırma.
    private static async Task<IResult> CreateRequest(NewRequest dto, AppDb db, AiClassifier ai)
    {
        if (string.IsNullOrWhiteSpace(dto.Title))
            return Results.BadRequest(new { error = "Başlık zorunludur." });
        if (dto.Lat is < -90 or > 90 || dto.Lon is < -180 or > 180)
            return Results.BadRequest(new { error = "Geçersiz koordinat." });

        var point = Geo.CreatePoint(dto.Lat, dto.Lon);
        var neighborhood = await db.Neighborhoods.FirstOrDefaultAsync(n => n.Boundary.Contains(point));

        var (category, usedAi) = dto.Category is { } given
            ? (given, false)
            : await ai.ClassifyAsync(dto.Title, dto.Description);

        var entity = new ServiceRequest
        {
            Title = dto.Title.Trim(),
            Description = dto.Description?.Trim(),
            Category = category,
            AiSuggested = usedAi,
            Location = point,
            NeighborhoodId = neighborhood?.Id
        };

        db.Requests.Add(entity);
        await db.SaveChangesAsync();
        entity.Neighborhood = neighborhood;

        return Results.Created($"/api/requests/{entity.Id}", Geo.RequestFeature(entity));
    }

    private static async Task<IResult> UpdateStatus(int id, StatusDto dto, AppDb db)
    {
        var entity = await db.Requests.FindAsync(id);
        if (entity is null) return Results.NotFound();

        entity.Status = dto.Status;
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    // Mahalle poligonları + açık/toplam talep sayısı (koroplet harita için).
    private static async Task<IResult> GetNeighborhoods(AppDb db)
    {
        var list = await db.Neighborhoods
            .Select(n => new
            {
                n.Id,
                n.Name,
                n.Boundary,
                Total = n.Requests.Count,
                Open = n.Requests.Count(r => r.Status != Status.Cozuldu)
            })
            .ToListAsync();

        var features = list.Select(n => new
        {
            type = "Feature",
            geometry = Geo.PolygonGeometry(n.Boundary),
            properties = new { n.Id, n.Name, n.Total, n.Open }
        });

        return Results.Ok(Geo.Collection(features));
    }

    // "minLon,minLat,maxLon,maxLat" metnini geometriye çevirir.
    private static bool TryParseBbox(string bbox, out Geometry box)
    {
        box = null!;

        var parts = bbox.Split(',');
        if (parts.Length != 4) return false;

        var values = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                return false;
        }

        box = Geo.Factory.ToGeometry(new Envelope(values[0], values[2], values[1], values[3]));
        return true;
    }
}