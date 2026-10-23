using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace BelediyeCBS;

public enum Category { Yol, Aydinlatma, Su, Temizlik, Park, Diger }
public enum Status { Acik, Inceleniyor, Cozuldu }

public class Neighborhood
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public Polygon Boundary { get; set; } = null!;
    public List<ServiceRequest> Requests { get; set; } = [];
}

public class ServiceRequest
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public Category Category { get; set; }
    public Status Status { get; set; } = Status.Acik;
    public Point Location { get; set; } = null!;
    public int? NeighborhoodId { get; set; }
    public Neighborhood? Neighborhood { get; set; }
    public bool AiSuggested { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Neighborhood> Neighborhoods => Set<Neighborhood>();
    public DbSet<ServiceRequest> Requests => Set<ServiceRequest>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("postgis");

        b.Entity<Neighborhood>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.Boundary).HasColumnType("geometry(Polygon,4326)");
            e.HasIndex(x => x.Boundary).HasMethod("gist");
        });

        b.Entity<ServiceRequest>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Category).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Location).HasColumnType("geometry(Point,4326)");
            e.HasIndex(x => x.Location).HasMethod("gist"); // mekansal indeks
            e.HasIndex(x => new { x.Category, x.Status });
            e.HasOne(x => x.Neighborhood)
             .WithMany(n => n.Requests)
             .HasForeignKey(x => x.NeighborhoodId)
             .OnDelete(DeleteBehavior.SetNull);
        });
    }
}

public static class Seed
{
    // Demo veri: Bursa çevresinde yaklaşık dikdörtgen bölgeler (gerçek idari sınır değildir).
    public static async Task RunAsync(AppDb db)
    {
        if (await db.Neighborhoods.AnyAsync()) return;

        var f = Geo.Factory;
        Polygon Rect(double minLon, double minLat, double maxLon, double maxLat) =>
            f.CreatePolygon(new Coordinate[]
            {
                new(minLon, minLat),
                new(maxLon, minLat),
                new(maxLon, maxLat),
                new(minLon, maxLat),
                new(minLon, minLat)
            });

        var hoods = new List<Neighborhood>
        {
            new() { Name = "Batı Demo Bölgesi",    Boundary = Rect(28.95, 40.17, 29.02, 40.23) },
            new() { Name = "Merkez Demo Bölgesi",  Boundary = Rect(29.02, 40.17, 29.09, 40.23) },
            new() { Name = "Doğu Demo Bölgesi",    Boundary = Rect(29.09, 40.17, 29.16, 40.23) }
        };
        db.Neighborhoods.AddRange(hoods);

        var samples = new (string Title, string Desc, Category Cat, Status St)[]
        {
            ("Ana caddede çukur", "Yolun ortasında araçlara zarar veren derin çukur var.", Category.Yol, Status.Acik),
            ("Sokak lambası yanmıyor", "Üç gündür sokağın lambası yanmıyor, akşam çok karanlık.", Category.Aydinlatma, Status.Inceleniyor),
            ("Su borusu patlamış", "Kaldırımda sürekli su akıyor, boru patlamış olabilir.", Category.Su, Status.Acik),
            ("Çöp konteyneri taşmış", "Konteyner birkaç gündür boşaltılmadı, koku yayılıyor.", Category.Temizlik, Status.Acik),
            ("Parktaki oyun grubu kırık", "Çocuk parkında salıncak zinciri kopmuş.", Category.Park, Status.Inceleniyor),
            ("Kaldırım taşları sökülmüş", "Yayalar için tehlikeli hale gelmiş.", Category.Yol, Status.Cozuldu),
            ("Trafo yanında kablo sarkıyor", "Aydınlatma direğinden sarkan kablo çocuklar için tehlike.", Category.Aydinlatma, Status.Acik),
            ("Kanalizasyon taşması", "Yağmurdan sonra mazgal taşıyor.", Category.Su, Status.Acik),
            ("Boş arsada moloz yığını", "Arsaya moloz dökülmüş, temizlenmesi gerekiyor.", Category.Temizlik, Status.Inceleniyor),
            ("Park bankları boyasız", "Banklar çürümüş, yenilenmesi lazım.", Category.Park, Status.Cozuldu),
            ("Kavşakta asfalt çökmesi", "Kavşakta asfalt çökmüş, araçlar yavaşlıyor.", Category.Yol, Status.Acik),
            ("Meydan aydınlatması yetersiz", "Meydanın bir bölümü geceleri tamamen karanlık.", Category.Aydinlatma, Status.Inceleniyor)
        };

        var rnd = new Random(42);
        var requests = new List<ServiceRequest>();
        for (int i = 0; i < samples.Length; i++)
        {
            var hood = hoods[i % hoods.Count];
            var env = hood.Boundary.EnvelopeInternal;
            var lon = env.MinX + (env.Width * (0.1 + rnd.NextDouble() * 0.8));
            var lat = env.MinY + (env.Height * (0.1 + rnd.NextDouble() * 0.8));
            var s = samples[i];

            requests.Add(new ServiceRequest
            {
                Title = s.Title,
                Description = s.Desc,
                Category = s.Cat,
                Status = s.St,
                Location = f.CreatePoint(new Coordinate(lon, lat)),
                Neighborhood = hood,
                CreatedAt = DateTime.UtcNow.AddDays(-rnd.Next(1, 30))
            });
        }

        db.Requests.AddRange(requests);
        await db.SaveChangesAsync();
    }
}

public static class DatabaseInitializer
{
    // Veritabanı konteyneri henüz hazır değilse birkaç kez yeniden dener.
    // Not: Üretimde EnsureCreated yerine EF Core migration kullanılmalıdır.
    public static async Task InitializeDatabaseAsync(this WebApplication app, int maxAttempts = 10)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.Database.EnsureCreatedAsync();
                await Seed.RunAsync(db);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                app.Logger.LogWarning(
                    "Veritabanına bağlanılamadı (deneme {Attempt}/{Max}): {Message}",
                    attempt, maxAttempts, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }
}