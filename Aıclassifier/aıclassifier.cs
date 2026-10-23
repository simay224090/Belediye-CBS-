using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace BelediyeCBS;

/// <summary>
/// Vatandaş talebini kategoriye ayırır. ANTHROPIC_API_KEY (veya Anthropic:ApiKey) varsa
/// Claude API'yi kullanır; yoksa ya da çağrı başarısız olursa kural tabanlı sınıflandırıcıya düşer.
/// </summary>
public class AiClassifier(HttpClient http, IConfiguration config, ILogger<AiClassifier> log)
{
    private static readonly CultureInfo Tr = new("tr-TR");

    private const string SystemPrompt =
        "Bir belediyenin talep/şikayet sınıflandırıcısısın. Verilen talebi şu kategorilerden " +
        "tam olarak birine ata: Yol, Aydinlatma, Su, Temizlik, Park, Diger. " +
        "Yalnızca kategori adını yaz, başka hiçbir şey yazma.";

    /// <returns>Kategori ve sonucun yapay zekadan gelip gelmediği.</returns>
    public async Task<(Category Category, bool UsedAi)> ClassifyAsync(string title, string? description)
    {
        var apiKey = GetApiKey();

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                var category = await AskClaudeAsync(apiKey, title, description);
                if (category is { } found) return (found, true);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "AI sınıflandırma başarısız, kural tabanlı yönteme geçildi.");
            }
        }

        return (RuleBased($"{title} {description}"), false);
    }

    private string? GetApiKey()
    {
        var key = config["Anthropic:ApiKey"];
        return string.IsNullOrWhiteSpace(key)
            ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            : key;
    }

    private async Task<Category?> AskClaudeAsync(string apiKey, string title, string? description)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = JsonContent.Create(new
        {
            model = config["Anthropic:Model"] ?? "claude-sonnet-5-5",
            max_tokens = 10,
            system = SystemPrompt,
            messages = new[]
            {
                new { role = "user", content = $"Başlık: {title}\nAçıklama: {description}" }
            }
        });

        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var text = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString();

        return Enum.TryParse<Category>(text?.Trim(), ignoreCase: true, out var category)
            ? category
            : null;
    }

    private static Category RuleBased(string text)
    {
        var t = text.ToLower(Tr);

        bool Has(params string[] words) => words.Any(t.Contains);

        if (Has("çukur", "asfalt", "yol", "kaldırım", "kavşak", "trafik")) return Category.Yol;
        if (Has("lamba", "aydınlat", "elektrik", "kablo", "direk")) return Category.Aydinlatma;
        if (Has("su ", "boru", "kanalizasyon", "mazgal", "sızıntı")) return Category.Su;
        if (Has("çöp", "konteyner", "moloz", "temizlik", "koku")) return Category.Temizlik;
        if (Has("park", "bank", "salıncak", "yeşil alan", "ağaç")) return Category.Park;
        return Category.Diger;
    }
}