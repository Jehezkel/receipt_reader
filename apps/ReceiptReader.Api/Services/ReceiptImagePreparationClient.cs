using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ReceiptReader.Api.Models;

namespace ReceiptReader.Api.Services;

public sealed class ReceiptImagePreparationClient : IReceiptImagePreparationClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ReceiptImagePreparationClient> _logger;

    public ReceiptImagePreparationClient(HttpClient httpClient, ILogger<ReceiptImagePreparationClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<ReceiptImagePreparationResult> PrepareAsync(IFormFile file, CancellationToken cancellationToken)
    {
        await using var fileStream = file.OpenReadStream();
        using var form = new MultipartFormDataContent();
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new(file.ContentType is { Length: > 0 } ? file.ContentType : "application/octet-stream");
        form.Add(fileContent, "file", file.FileName);

        try
        {
            using var response = await _httpClient.PostAsync("/prepare", form, cancellationToken);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<PrepareClientResponse>(cancellationToken: cancellationToken);
            if (payload is null)
            {
                throw new InvalidOperationException("Image preparation response was empty.");
            }

            return new ReceiptImagePreparationResult
            {
                PreparedBytes = Convert.FromBase64String(payload.ImageBase64),
                Artifact = new ImagePreparationArtifact
                {
                    Provider = payload.Provider,
                    OutputContentType = payload.OutputContentType,
                    OutputExtension = payload.OutputExtension,
                    OriginalWidth = payload.Metadata.OriginalWidth,
                    OriginalHeight = payload.Metadata.OriginalHeight,
                    PreparedWidth = payload.Metadata.PreparedWidth,
                    PreparedHeight = payload.Metadata.PreparedHeight,
                    OriginalBytes = payload.Metadata.OriginalBytes,
                    PreparedBytes = payload.Metadata.PreparedBytes,
                    UsedFallback = payload.Metadata.FallbackUsed,
                    CropApplied = payload.Metadata.CropApplied,
                    AppliedFilters = payload.Metadata.AppliedFilters.ToArray(),
                    Notes = payload.Metadata.Notes,
                    CropBox = payload.Metadata.CropBox is null
                        ? null
                        : new BoundingBox
                        {
                            X = payload.Metadata.CropBox.X,
                            Y = payload.Metadata.CropBox.Y,
                            Width = payload.Metadata.CropBox.Width,
                            Height = payload.Metadata.CropBox.Height
                        },
                    BodyCropBox = payload.Metadata.BodyCropBox is null
                        ? null
                        : new BoundingBox
                        {
                            X = payload.Metadata.BodyCropBox.X,
                            Y = payload.Metadata.BodyCropBox.Y,
                            Width = payload.Metadata.BodyCropBox.Width,
                            Height = payload.Metadata.BodyCropBox.Height
                        },
                    FooterCropBox = payload.Metadata.FooterCropBox is null
                        ? null
                        : new BoundingBox
                        {
                            X = payload.Metadata.FooterCropBox.X,
                            Y = payload.Metadata.FooterCropBox.Y,
                            Width = payload.Metadata.FooterCropBox.Width,
                            Height = payload.Metadata.FooterCropBox.Height
                        }
                }
            };
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Image preparation service unavailable, falling back to original upload.");

            await using var fallbackStream = file.OpenReadStream();
            using var memoryStream = new MemoryStream();
            await fallbackStream.CopyToAsync(memoryStream, cancellationToken);

            var fileExtension = Path.GetExtension(file.FileName);
            if (string.IsNullOrWhiteSpace(fileExtension))
            {
                fileExtension = ".jpg";
            }

            return new ReceiptImagePreparationResult
            {
                PreparedBytes = memoryStream.ToArray(),
                Artifact = new ImagePreparationArtifact
                {
                    Provider = "api-fallback",
                    OutputContentType = file.ContentType is { Length: > 0 } ? file.ContentType : "application/octet-stream",
                    OutputExtension = fileExtension,
                    OriginalBytes = file.Length,
                    PreparedBytes = memoryStream.Length,
                    UsedFallback = true,
                    CropApplied = false,
                    AppliedFilters = ["original-upload"],
                    Notes = "Image preparation service unavailable; original upload was stored."
                }
            };
        }
    }

    private sealed class PrepareClientResponse
    {
        [JsonPropertyName("provider")]
        public string Provider { get; set; } = string.Empty;

        [JsonPropertyName("outputContentType")]
        public string OutputContentType { get; set; } = "image/jpeg";

        [JsonPropertyName("outputExtension")]
        public string OutputExtension { get; set; } = ".jpg";

        [JsonPropertyName("imageBase64")]
        public string ImageBase64 { get; set; } = string.Empty;

        [JsonPropertyName("metadata")]
        public PrepareClientMetadata Metadata { get; set; } = new();
    }

    private sealed class PrepareClientMetadata
    {
        [JsonPropertyName("originalWidth")]
        public int OriginalWidth { get; set; }

        [JsonPropertyName("originalHeight")]
        public int OriginalHeight { get; set; }

        [JsonPropertyName("preparedWidth")]
        public int PreparedWidth { get; set; }

        [JsonPropertyName("preparedHeight")]
        public int PreparedHeight { get; set; }

        [JsonPropertyName("originalBytes")]
        public long OriginalBytes { get; set; }

        [JsonPropertyName("preparedBytes")]
        public long PreparedBytes { get; set; }

        [JsonPropertyName("fallbackUsed")]
        public bool FallbackUsed { get; set; }

        [JsonPropertyName("cropApplied")]
        public bool CropApplied { get; set; }

        [JsonPropertyName("appliedFilters")]
        public List<string> AppliedFilters { get; set; } = [];

        [JsonPropertyName("notes")]
        public string? Notes { get; set; }

        [JsonPropertyName("cropBox")]
        public PrepareClientBoundingBox? CropBox { get; set; }

        [JsonPropertyName("bodyCropBox")]
        public PrepareClientBoundingBox? BodyCropBox { get; set; }

        [JsonPropertyName("footerCropBox")]
        public PrepareClientBoundingBox? FooterCropBox { get; set; }
    }

    private sealed class PrepareClientBoundingBox
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
