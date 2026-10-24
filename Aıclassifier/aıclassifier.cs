using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BelediyeCBS;

public class AiClassifier
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ILogger<AiClassifier> _logger;

    public AiClassifier(HttpClient httpClient, IConfiguration config, ILogger<AiClassifier> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        var key = config["Groq:ApiKey"];
        if (string.IsNullOrWhiteSpace(key))
            key = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        _apiKey = key ?? string.Empty;
        _model = config["Groq:Model"] ?? "openai/gpt-oss-20b";
    }

    public async Task<(Category, bool)> ClassifyAsync(string title, string? description)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogWarning("[AI-CLASSIFIER] Groq API Key bulunamadı! Kural tabanlı sınıflandırıcıya düşülüyor.");
            return (GetRuleBasedCategory(title, description), false);
        }

        var sw = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation("[AI-CLASSIFIER] İstek Groq ({Model})'a gönderiliyor... Başlık: \"{Title}\"", _model, title);

            var systemPrompt = @"Sen bir belediye talep sınıflandırıcısısın. 
Vatandaşın gönderdiği başlık ve açıklamayı değerlendirerek en uygun kategoriyi seç.

Kategoriler ve Kapsamları:
- Su: Su baskını, sel, boru patlağı, kanalizasyon, su kesintisi, rögarlar, göllenme.
- Yol: Asfalt bozulması, çukur, kaldırım, yol çökmesi, yol yapım/onarım.
- Aydinlatma: Sokak lambası, sokak aydınlatması, karanlık sokaklar, elektrik direği.
- Temizlik: Çöp konteyneri, çöp toplama, moloz, sokak süpürme, çevre kirliliği.
- Park: Park bakımı, çocuk oyun alanları, ağaç budama, banklar, çim biçme.
- Diger: Yukarıdakilere girmeyen tüm diğer konular.

Cevap olarak YALNIZCA KATEGORİ ADINI (Yol, Su, Aydinlatma, Temizlik, Park, Diger) yaz. Başka hiçbir açıklama ekleme."; ;

            var userPrompt = $"Başlık: {title}\nAçıklama: {description ?? "Yok"}";

            var requestBody = new
            {
                model = _model,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                temperature = 0.0,
                max_tokens = 100
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("[AI-CLASSIFIER] Groq API Hatası (HTTP {StatusCode}): {ErrorBody}",
                    (int)response.StatusCode, errContent);
                return (GetRuleBasedCategory(title, description), false);
            }

            var responseString = await response.Content.ReadAsStringAsync();
            using var jsonDoc = JsonDocument.Parse(responseString);

            var messageObj = jsonDoc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message");

            // 1. Standart content alanını kontrol et
            string rawCategory = string.Empty;
            if (messageObj.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
            {
                rawCategory = contentProp.GetString()?.Trim().Trim('"', '.', '\n', '\r') ?? string.Empty;
            }

            // 2. Eğer content boşsa, model reasoning (akıl yürütme) bloğuna yazmış demektir
            if (string.IsNullOrWhiteSpace(rawCategory) && messageObj.TryGetProperty("reasoning", out var reasoningProp) && reasoningProp.ValueKind == JsonValueKind.String)
            {
                rawCategory = reasoningProp.GetString()?.Trim().Trim('"', '.', '\n', '\r') ?? string.Empty;
            }

            sw.Stop();

            // 3. Doğrudan tek kelime tam eşleşmesi
            if (Enum.TryParse<Category>(rawCategory, true, out var parsedCategory))
            {
                _logger.LogInformation("[AI-CLASSIFIER] Başarılı! Groq: {Category} döndü ({ElapsedMilliseconds} ms)", parsedCategory, sw.ElapsedMilliseconds);
                return (parsedCategory, true);
            }

            // 4. Model cümle/açıklama kurduysa içindeki kategori adını ara
            foreach (var catName in Enum.GetNames<Category>())
            {
                if (rawCategory.Contains(catName, StringComparison.OrdinalIgnoreCase))
                {
                    if (Enum.TryParse<Category>(catName, true, out var matchedCategory))
                    {
                        _logger.LogInformation("[AI-CLASSIFIER] Metin içinden başarıyla yakalandı! Groq: {Category} döndü ({ElapsedMilliseconds} ms)", matchedCategory, sw.ElapsedMilliseconds);
                        return (matchedCategory, true);
                    }
                }
            }

            _logger.LogWarning("[AI-CLASSIFIER] Geçersiz kategori döndü ('{RawResponse}'). Kural tabanlıya düşülüyor.", rawCategory);
            return (GetRuleBasedCategory(title, description), false);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[AI-CLASSIFIER] Groq API çağrısı başarısız oldu ({ElapsedMilliseconds} ms). Graceful degradation devrede.", sw.ElapsedMilliseconds);
            return (GetRuleBasedCategory(title, description), false);
        }
    }

    private Category GetRuleBasedCategory(string title, string? description)
    {
        var text = $"{title} {description}".ToLower();
        if (text.Contains("lamba") || text.Contains("karanlık") || text.Contains("ışık") || text.Contains("direk")) return Category.Aydinlatma;
        if (text.Contains("çukur") || text.Contains("asfalt") || text.Contains("kaldırım") || text.Contains("yol")) return Category.Yol;
        if (text.Contains("çöp") || text.Contains("süpürge") || text.Contains("pis") || text.Contains("konteyner")) return Category.Temizlik;
        if (text.Contains("boru") || text.Contains("patlak") || text.Contains("akıyor") || text.Contains("su")) return Category.Su;
        if (text.Contains("bank") || text.Contains("park") || text.Contains("ağaç") || text.Contains("çim") || text.Contains("salıncak")) return Category.Park;

        return Category.Diger;
    }
}