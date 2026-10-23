# Belediye CBS - Talep Haritası

Vatandaşların harita üzerinden altyapı/hizmet taleplerini (yol, aydınlatma, su, temizlik, park) bildirdiği,
belediye ekibinin ise bu talepleri mekansal olarak sorgulayıp yönettiği küçük bir CBS web uygulaması.

**Teknolojiler:** ASP.NET Core (.NET 10) · Razor Pages · jQuery Ajax · HTML/CSS/JS · EF Core ·
PostgreSQL + PostGIS · NetTopologySuite · Leaflet · REST API (GeoJSON) · Claude API (yapay zeka)

## Çalıştırma

```bash
docker compose up -d db          # PostGIS veritabanı
dotnet run                       # http://localhost:5080
```

İlk açılışta tablolar, PostGIS eklentisi ve demo veri otomatik oluşur.

Yapay zeka ile otomatik kategori için (isteğe bağlı):

```bash
export ANTHROPIC_API_KEY=...     # yoksa kural tabanlı sınıflandırıcı devreye girer
```

## Proje yapısı

| Dosya | Sorumluluk |
|-------|------------|
| `Program.cs` | Sadece uygulama kurulumu: servis kayıtları, veritabanı başlatma, uç noktaların bağlanması |
| `Api.cs` | REST API uç noktaları ve istek/yanıt modelleri (DTO) |
| `Data.cs` | Entity'ler, `DbContext`, demo veri (seed) ve veritabanı başlatma |
| `Geo.cs` | Geometri fabrikası (SRID 4326) ve GeoJSON çıktı yardımcıları |
| `AiClassifier.cs` | Claude API ile kategori tahmini, yedek olarak kural tabanlı sınıflandırma |
| `Pages/Index.cshtml` | Harita arayüzü (Razor Page, Leaflet, jQuery Ajax) |

## REST API

| Metot | Yol | Açıklama |
|-------|-----|----------|
| GET | `/api/requests?category=&status=&bbox=minLon,minLat,maxLon,maxLat` | Talepler (GeoJSON FeatureCollection) |
| GET | `/api/requests/nearby?lat=&lon=&radius=` | Metre cinsinden yarıçap araması (`ST_DWithin` + `geography`) |
| POST | `/api/requests` | Yeni talep; mahalle otomatik bulunur, kategori boşsa AI belirler |
| PATCH | `/api/requests/{id}/status` | Durum güncelle |
| GET | `/api/neighborhoods` | Mahalle poligonları + açık/toplam talep sayısı |

## Mülakatta anlatılabilecekler

- **Mekansal veri modeli:** `geometry(Point,4326)` ve `geometry(Polygon,4326)` kolonları, GIST indeksler,
  NetTopologySuite ile EF Core entegrasyonu.
- **Mekansal sorgular:** point-in-polygon (`Boundary.Contains`), bbox filtresi (`Within`),
  metre bazlı yakınlık (`ST_DWithin` ile `geography` cast), mahalle bazlı toplulaştırma.
- **Neden `geography`?** `geometry` 4326'da mesafeler derece cinsindendir; metre için `geography` kullanıldı.
- **REST/GeoJSON:** Standart GeoJSON çıktısı sayesinde Leaflet, QGIS veya başka bir kurum sistemiyle kolay entegrasyon.
- **Yapay zeka:** Serbest metinli talebi kategoriye ayırma; API anahtarı yoksa ya da çağrı hata verirse
  kural tabanlı yedek (graceful degradation). Hangi kaydın AI önerisi olduğu `AiSuggested` ile saklanır.
- **Güvenlik:** Arayüzde kullanıcı girdisi HTML-escape edilir, girdi doğrulaması API tarafında yapılır,
  SQL sorgusu parametreli (`FromSqlInterpolated`).

## Geliştirme fikirleri (konuşmayı uzatmak için)

- `EnsureCreated` yerine EF Core migration'ları
- Mahalle sınırlarını gerçek idari sınır verisiyle (GeoJSON/Shapefile) yüklemek
- Fotoğraf yükleme, kimlik doğrulama ve rol bazlı yetki (vatandaş / personel)
- Vector tile (MVT) ile yüksek hacimli veri, GeoServer/WMS-WFS entegrasyonu
- xUnit ile API ve mekansal sorgu testleri, Docker ile uygulamanın da containerize edilmesi