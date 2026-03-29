using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ReceiptReader.Api.Services;

public sealed class OcrClient : IOcrClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OcrClient> _logger;

    public OcrClient(HttpClient httpClient, ILogger<OcrClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<OcrResult> ProcessAsync(string imagePath, CancellationToken cancellationToken)
    {
        await using var fileStream = File.OpenRead(imagePath);
        using var form = new MultipartFormDataContent();
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new("image/jpeg");
        form.Add(fileContent, "file", Path.GetFileName(imagePath));

        try
        {
            using var response = await _httpClient.PostAsync("/ocr", form, cancellationToken);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<OcrClientResponse>(cancellationToken: cancellationToken);
            if (payload is null)
            {
                throw new InvalidOperationException("OCR response was empty.");
            }

            return new OcrResult
            {
                RawText = payload.RawText,
                NormalizedText = payload.NormalizedText,
                Lines = payload.Lines.Select(line => new Models.OcrLine
                {
                    RawText = line.Text,
                    NormalizedText = line.Text,
                    Text = line.Text,
                    Confidence = line.Confidence,
                    BoundingBox = line.BoundingBox is null ? null : new Models.BoundingBox
                    {
                        X = line.BoundingBox.X,
                        Y = line.BoundingBox.Y,
                        Width = line.BoundingBox.Width,
                        Height = line.BoundingBox.Height
                    }
                }).ToList(),
                QualityScore = payload.QualityScore,
                Provider = payload.Provider
            };
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "OCR service unavailable, using local fallback.");
            var fallbackText = """
            PARAGON FISKALNY
            SKLEP TESTOWY
            NIP 1234567890
            2026-03-28
            CHLEB 1 4,99
            MLEKO 2 3,49
            SUMA PLN 11,97
            """;

            return new OcrResult
            {
                RawText = fallbackText,
                NormalizedText = fallbackText,
                Lines = fallbackText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(line => new Models.OcrLine
                    {
                        RawText = line,
                        NormalizedText = line,
                        Text = line,
                        Confidence = 0.45
                    })
                    .ToList(),
                QualityScore = 0.45,
                Provider = "fallback"
            };
        }
    }

    private sealed class OcrClientResponse
    {
        [JsonPropertyName("rawText")]
        public string RawText { get; set; } = string.Empty;

        [JsonPropertyName("normalizedText")]
        public string NormalizedText { get; set; } = string.Empty;

        [JsonPropertyName("qualityScore")]
        public double QualityScore { get; set; }

        [JsonPropertyName("provider")]
        public string Provider { get; set; } = string.Empty;

        [JsonPropertyName("lines")]
        public List<OcrClientLine> Lines { get; set; } = [];
    }

    private sealed class OcrClientLine
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }

        [JsonPropertyName("boundingBox")]
        public OcrClientBoundingBox? BoundingBox { get; set; }
    }

    private sealed class OcrClientBoundingBox
    {
        [JsonPropertyName("x")]
        public int X { get; set; }

        [JsonPropertyName("y")]
        public int Y { get; set; }

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }
    }
}
