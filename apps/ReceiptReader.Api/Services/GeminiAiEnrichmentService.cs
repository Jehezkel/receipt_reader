using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Diagnostics;
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

        var uncertainItems = parsedReceipt.Items
            .Where(item => item.ParseWarnings.Count > 0 || item.Confidence < 0.65)
            .Take(8)
            .ToList();

        var consistencyStatus = parsedReceipt.Summary.TotalMatchesItems
            ? ReceiptConsistencyStatus.ToleranceMatch
            : uncertainItems.Count > 0
                ? ReceiptConsistencyStatus.Mismatch
                : ReceiptConsistencyStatus.InsufficientData;

        var tooLittleStructuredData = parsedReceipt.Items.Count < 2;
        var requiresFallback = consistencyStatus is ReceiptConsistencyStatus.Mismatch or ReceiptConsistencyStatus.InsufficientData;
        var needsAi = !tooLittleStructuredData
            && requiresFallback
            && uncertainItems.Count > 0;

        if (!needsAi)
        {
            return new AiEnrichmentResult
            {
                Summary = parsedReceipt.Summary,
                Items = parsedReceipt.Items,
                WasApplied = false,
                Details = tooLittleStructuredData
                    ? "Gemini skipped because the parser did not extract enough structured items yet."
                    : "Gemini skipped because there is not enough stable data to refine safely.",
                Provider = "skipped"
            };
        }

        var prompt = BuildPrompt(ocrResult, parsedReceipt, uncertainItems);
        var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{_options.Model}:generateContent?key={_options.ApiKey}";
        var promptLength = prompt.Length;
        var relevantOcrLineCount = ocrResult.NormalizedText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(line => IsRelevantPromptLine(line, uncertainItems));

        _logger.LogInformation(
            "Gemini enrichment starting. Model={Model}, timeout={TimeoutSeconds}s, parsedItems={ParsedItems}, uncertainItems={UncertainItems}, relevantPromptLines={RelevantPromptLines}, promptChars={PromptChars}, apiKeyPresent={ApiKeyPresent}.",
            _options.Model,
            _options.TimeoutSeconds,
            parsedReceipt.Items.Count,
            uncertainItems.Count,
            relevantOcrLineCount,
            promptLength,
            !string.IsNullOrWhiteSpace(_options.ApiKey));

        for (var attempt = 0; attempt <= _options.MaxRetryCount; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();
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
                stopwatch.Stop();

                _logger.LogInformation(
                    "Gemini responded. Attempt={Attempt}, elapsedMs={ElapsedMs}, statusCode={StatusCode}.",
                    attempt + 1,
                    stopwatch.ElapsedMilliseconds,
                    (int)response.StatusCode);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(cancellationToken);

                var candidateText = ExtractText(json);
                if (string.IsNullOrWhiteSpace(candidateText))
                {
                    throw new InvalidOperationException("Gemini returned no text.");
                }

                var jsonPayload = ExtractJsonPayload(candidateText);
                GeminiPayload? enriched;
                try
                {
                    enriched = JsonSerializer.Deserialize<GeminiPayload>(jsonPayload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                }
                catch (JsonException exception)
                {
                    _logger.LogWarning(exception, "Gemini returned non-parseable JSON payload preview: {PayloadPreview}", TruncateForLog(jsonPayload));
                    throw;
                }
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
                        TotalGross = parsedReceipt.Summary.TotalGross,
                        Confidence = Math.Min(0.98, parsedReceipt.Summary.Confidence + 0.08),
                        TotalMatchesItems = parsedReceipt.Summary.TotalMatchesItems,
                        NeedsReview = parsedReceipt.Summary.NeedsReview
                    },
                    Items = MergeItems(parsedReceipt.Items, enriched.Items),
                    WasApplied = true,
                    Provider = _options.Model,
                    Details = "Gemini refinement applied."
                };
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                _logger.LogWarning(
                    exception,
                    "Gemini enrichment failed on attempt {Attempt}. Model={Model}, elapsedMs={ElapsedMs}, promptChars={PromptChars}, uncertainItems={UncertainItems}.",
                    attempt + 1,
                    _options.Model,
                    stopwatch.ElapsedMilliseconds,
                    promptLength,
                    uncertainItems.Count);
                if (attempt == _options.MaxRetryCount || !ShouldRetry(exception))
                {
                    break;
                }

                await Task.Delay(GetBackoff(attempt), cancellationToken);
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

    private static string BuildPrompt(OcrResult ocrResult, ReceiptParseResult parsedReceipt, IReadOnlyList<ReceiptItem> uncertainItems)
    {
        var schemaHint = """
        Return raw JSON only with this shape. Do not wrap the response in markdown code fences:
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
              "discount": number|null,
              "vatRate": "string|null",
              "sourceLine": "string|null",
              "sourceLines": ["string"],
              "reason": "string|null",
              "correctedFrom": "string|null"
            }
          ]
        }
        """;

        var relevantLines = ocrResult.NormalizedText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => IsRelevantPromptLine(line, uncertainItems))
            .Take(12)
            .Select(line => new
            {
                text = line
            })
            .ToList();

        if (relevantLines.Count == 0)
        {
            relevantLines = ocrResult.NormalizedText
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Take(8)
                .Select(line => new
                {
                    text = line
                })
                .ToList();
        }

        var currentDraft = JsonSerializer.Serialize(new
        {
            merchantName = parsedReceipt.Summary.MerchantName,
            taxId = parsedReceipt.Summary.TaxId,
            declaredTotalGross = parsedReceipt.Summary.TotalGross,
            currentDifference = parsedReceipt.Summary.TotalGross.HasValue
                ? (decimal?)(parsedReceipt.Summary.TotalGross.Value - uncertainItems.Sum(item => item.TotalPrice ?? 0m) + uncertainItems.Sum(item => item.Discount ?? 0m))
                : null,
            uncertainItems = uncertainItems.Select(item => new
            {
                item.Name,
                item.Quantity,
                item.UnitPrice,
                item.TotalPrice,
                item.Discount,
                item.ArithmeticConfidence,
                item.CandidateKind,
                item.RepairReason,
                item.SourceLines,
                item.ParseWarnings
            }),
            paymentAndSummaryLines = relevantLines,
            note = "Do not return payment lines, tax summary lines, totals or card/cash lines as receipt items."
        });

        return $"{schemaHint}\nCorrect only the uncertain items that are causing the receipt balance mismatch. Never invent missing products. Never classify payment methods, VAT summaries, subtotal lines, or terminal lines as items. Do not change merchant, tax id, purchase date or declared total. Use OCR lines as the source of truth. If a correction would worsen the balance, leave the draft unchanged.\nDraft:\n{currentDraft}";
    }

    private static bool ShouldRetry(Exception exception)
    {
        if (exception is TaskCanceledException)
        {
            return false;
        }

        if (exception is HttpRequestException httpRequestException && httpRequestException.StatusCode is { } statusCode)
        {
            var numericStatusCode = (int)statusCode;
            return numericStatusCode == 408 || numericStatusCode == 429 || numericStatusCode >= 500;
        }

        return false;
    }

    private static TimeSpan GetBackoff(int attempt) => attempt switch
    {
        0 => TimeSpan.FromSeconds(3),
        1 => TimeSpan.FromSeconds(10),
        _ => TimeSpan.FromSeconds(25)
    };

    private static bool IsRelevantPromptLine(string line, IReadOnlyList<ReceiptItem> uncertainItems)
    {
        var matchesItem = uncertainItems.Any(item =>
            item.SourceLines.Any(source => source.Contains(line, StringComparison.OrdinalIgnoreCase) || line.Contains(source, StringComparison.OrdinalIgnoreCase))
            || item.SourceLine.Contains(line, StringComparison.OrdinalIgnoreCase)
            || line.Contains(item.Name, StringComparison.OrdinalIgnoreCase));

        if (matchesItem)
        {
            return true;
        }

        return line.Contains("SUMA", StringComparison.OrdinalIgnoreCase)
            || line.Contains("TOTAL", StringComparison.OrdinalIgnoreCase)
            || line.Contains("PTU", StringComparison.OrdinalIgnoreCase)
            || line.Contains("VAT", StringComparison.OrdinalIgnoreCase)
            || line.Contains("KARTA", StringComparison.OrdinalIgnoreCase)
            || line.Contains("GOT", StringComparison.OrdinalIgnoreCase)
            || line.Contains("WP", StringComparison.OrdinalIgnoreCase)
            || line.Contains("SPRZED", StringComparison.OrdinalIgnoreCase);
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

    private static string ExtractJsonPayload(string responseText)
    {
        var sanitized = responseText.Trim();
        if (sanitized.StartsWith("```", StringComparison.Ordinal))
        {
            sanitized = StripCodeFence(sanitized);
        }

        var firstBrace = sanitized.IndexOf('{');
        var lastBrace = sanitized.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            sanitized = sanitized[firstBrace..(lastBrace + 1)];
        }

        return sanitized.Trim();
    }

    private static string StripCodeFence(string text)
    {
        var lines = text
            .Split('\n', StringSplitOptions.TrimEntries)
            .ToList();

        if (lines.Count == 0)
        {
            return text;
        }

        if (lines[0].StartsWith("```", StringComparison.Ordinal))
        {
            lines.RemoveAt(0);
        }

        if (lines.Count > 0 && lines[^1].StartsWith("```", StringComparison.Ordinal))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join('\n', lines);
    }

    private static string TruncateForLog(string value)
    {
        const int maxLength = 400;
        return value.Length <= maxLength
            ? value
            : value[..maxLength] + "...";
    }

    private static IReadOnlyList<ReceiptItem> MergeItems(
        IReadOnlyList<ReceiptItem> originalItems,
        IReadOnlyList<GeminiPayloadItem>? enrichedItems)
    {
        if (enrichedItems is null || enrichedItems.Count == 0)
        {
            return originalItems;
        }

        var mergedItems = originalItems
            .Select(CloneReceiptItem)
            .ToList();

        foreach (var enrichedItem in enrichedItems)
        {
            var mappedItem = MapGeminiItem(enrichedItem);
            var matchIndex = FindMatchingItemIndex(mergedItems, enrichedItem, mappedItem);

            if (matchIndex >= 0)
            {
                mergedItems[matchIndex] = mappedItem;
            }
        }

        return mergedItems;
    }

    private static int FindMatchingItemIndex(
        IReadOnlyList<ReceiptItem> items,
        GeminiPayloadItem enrichedItem,
        ReceiptItem mappedItem)
    {
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (SourcesMatch(item, enrichedItem) || NamesMatch(item, mappedItem))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool SourcesMatch(ReceiptItem item, GeminiPayloadItem enrichedItem)
    {
        var enrichedSources = enrichedItem.SourceLines ?? [];
        if (!string.IsNullOrWhiteSpace(enrichedItem.SourceLine))
        {
            enrichedSources = [.. enrichedSources, enrichedItem.SourceLine];
        }

        return enrichedSources.Any(source =>
            item.SourceLines.Any(existing => string.Equals(existing, source, StringComparison.OrdinalIgnoreCase))
            || string.Equals(item.SourceLine, source, StringComparison.OrdinalIgnoreCase));
    }

    private static bool NamesMatch(ReceiptItem original, ReceiptItem candidate)
    {
        return string.Equals(original.Name, candidate.Name, StringComparison.OrdinalIgnoreCase)
            && original.Quantity == candidate.Quantity;
    }

    private static ReceiptItem MapGeminiItem(GeminiPayloadItem item) =>
        new()
        {
            Name = item.Name,
            Quantity = item.Quantity,
            UnitPrice = item.UnitPrice,
            TotalPrice = item.TotalPrice,
            Discount = item.Discount,
            VatRate = item.VatRate,
            Confidence = 0.82,
            ArithmeticConfidence = 0.75,
            CandidateKind = ReceiptItemCandidateKind.Repaired,
            SourceLine = item.SourceLine ?? string.Join(" | ", item.SourceLines ?? []),
            SourceLines = item.SourceLines ?? (item.SourceLine is null ? [] : [item.SourceLine]),
            WasAiCorrected = !string.IsNullOrWhiteSpace(item.Reason) || !string.IsNullOrWhiteSpace(item.CorrectedFrom),
            RepairReason = item.Reason,
            ParseWarnings = BuildWarnings(item)
        };

    private static ReceiptItem CloneReceiptItem(ReceiptItem item) =>
        new()
        {
            Name = item.Name,
            Quantity = item.Quantity,
            UnitPrice = item.UnitPrice,
            TotalPrice = item.TotalPrice,
            Discount = item.Discount,
            VatRate = item.VatRate,
            Confidence = item.Confidence,
            ArithmeticConfidence = item.ArithmeticConfidence,
            CandidateKind = item.CandidateKind,
            SourceLine = item.SourceLine,
            SourceLines = item.SourceLines.ToArray(),
            WasAiCorrected = item.WasAiCorrected,
            ExcludedByBalancer = item.ExcludedByBalancer,
            RepairReason = item.RepairReason,
            ParseWarnings = item.ParseWarnings.ToArray()
        };

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
        public decimal? Discount { get; set; }
        public string? VatRate { get; set; }
        public string? SourceLine { get; set; }
        public List<string>? SourceLines { get; set; }
        public string? Reason { get; set; }
        public string? CorrectedFrom { get; set; }
    }

    private static IReadOnlyList<string> BuildWarnings(GeminiPayloadItem item)
    {
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Reason))
        {
            warnings.Add($"AI note: {item.Reason}");
        }

        if (!string.IsNullOrWhiteSpace(item.CorrectedFrom))
        {
            warnings.Add($"Corrected from: {item.CorrectedFrom}");
        }

        return warnings;
    }
}
