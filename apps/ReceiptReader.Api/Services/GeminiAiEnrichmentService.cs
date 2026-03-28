using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ReceiptReader.Api.Configuration;
using ReceiptReader.Api.Models;
using Microsoft.Extensions.Options;

namespace ReceiptReader.Api.Services;

public sealed class GeminiAiEnrichmentService : IAiEnrichmentService
{
    private readonly HttpClient _httpClient;
    private readonly GeminiOptions _options;
    private readonly ILogger<GeminiAiEnrichmentService> _logger;

    public GeminiAiEnrichmentService(
        HttpClient httpClient,
        IOptions<GeminiOptions> options,
        ILogger<GeminiAiEnrichmentService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AiEnrichmentResult> EnrichAsync(OcrResult ocrResult, ReceiptParseResult parsedReceipt, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return new AiEnrichmentResult
            {
                Summary = parsedReceipt.Summary,
                Items = parsedReceipt.Items,
                WasApplied = false,
                Details = "Gemini disabled.",
                Provider = "disabled"
            };
        }

        var prompt = BuildPrompt(ocrResult, parsedReceipt);
        var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{_options.Model}:generateContent?key={_options.ApiKey}";

        for (var attempt = 0; attempt <= _options.MaxRetryCount; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = JsonContent.Create(new
                {
                    contents = new[]
                    {
                        new
                        {
                            parts = new[]
                            {
                                new { text = prompt }
                            }
                        }
                    }
                });

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(cancellationToken);

                var candidateText = ExtractText(json);
                if (string.IsNullOrWhiteSpace(candidateText))
                {
                    throw new InvalidOperationException("Gemini returned no text.");
                }

                var enriched = JsonSerializer.Deserialize<GeminiPayload>(candidateText, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (enriched is null)
                {
                    throw new InvalidOperationException("Gemini returned invalid JSON.");
                }

                return new AiEnrichmentResult
                {
                    Summary = new ReceiptSummary
                    {
                        MerchantName = enriched.MerchantName ?? parsedReceipt.Summary.MerchantName,
                        TaxId = enriched.TaxId ?? parsedReceipt.Summary.TaxId,
                        PurchaseDate = parsedReceipt.Summary.PurchaseDate,
                        Currency = parsedReceipt.Summary.Currency,
                        TotalGross = enriched.TotalGross ?? parsedReceipt.Summary.TotalGross,
                        Confidence = Math.Min(0.98, parsedReceipt.Summary.Confidence + 0.08),
                        TotalMatchesItems = parsedReceipt.Summary.TotalMatchesItems
                    },
                    Items = enriched.Items?.Count > 0
                        ? enriched.Items.Select(item => new ReceiptItem
                        {
                            Name = item.Name,
                            Quantity = item.Quantity,
                            UnitPrice = item.UnitPrice,
                            TotalPrice = item.TotalPrice,
                            VatRate = item.VatRate,
                            Confidence = 0.82,
                            SourceLine = item.SourceLine ?? string.Empty
                        }).ToList()
                        : parsedReceipt.Items,
                    WasApplied = true,
                    Provider = _options.Model,
                    Details = "Gemini refinement applied."
                };
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Gemini enrichment failed on attempt {Attempt}.", attempt + 1);
                if (attempt == _options.MaxRetryCount)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), cancellationToken);
            }
        }

        return new AiEnrichmentResult
        {
            Summary = parsedReceipt.Summary,
            Items = parsedReceipt.Items,
            WasApplied = false,
            Provider = "fallback",
            Details = "Gemini unavailable, OCR-only result kept."
        };
    }

    private static string BuildPrompt(OcrResult ocrResult, ReceiptParseResult parsedReceipt)
    {
        var schemaHint = """
        Return only valid JSON with this shape:
        {
          "merchantName": "string|null",
          "taxId": "string|null",
          "totalGross": number|null,
          "items": [
            {
              "name": "string",
              "quantity": number|null,
              "unitPrice": number|null,
              "totalPrice": number|null,
              "vatRate": "string|null",
              "sourceLine": "string|null"
            }
          ]
        }
        """;

        var currentDraft = JsonSerializer.Serialize(new
        {
            merchantName = parsedReceipt.Summary.MerchantName,
            taxId = parsedReceipt.Summary.TaxId,
            totalGross = parsedReceipt.Summary.TotalGross,
            items = parsedReceipt.Items
        });

        return $"{schemaHint}\nUse OCR text below to refine uncertain fields. Keep values conservative.\nOCR:\n{ocrResult.NormalizedText}\nDraft:\n{currentDraft}";
    }

    private static string? ExtractText(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
        {
            return null;
        }

        var parts = candidates[0]
            .GetProperty("content")
            .GetProperty("parts");
        if (parts.GetArrayLength() == 0)
        {
            return null;
        }

        return parts[0].GetProperty("text").GetString();
    }

    private sealed class GeminiPayload
    {
        public string? MerchantName { get; set; }
        public string? TaxId { get; set; }
        public decimal? TotalGross { get; set; }
        public List<GeminiPayloadItem>? Items { get; set; }
    }

    private sealed class GeminiPayloadItem
    {
        public string Name { get; set; } = string.Empty;
        public decimal? Quantity { get; set; }
        public decimal? UnitPrice { get; set; }
        public decimal? TotalPrice { get; set; }
        public string? VatRate { get; set; }
        public string? SourceLine { get; set; }
    }
}
