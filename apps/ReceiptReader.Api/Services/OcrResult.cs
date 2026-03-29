using ReceiptReader.Api.Models;

namespace ReceiptReader.Api.Services;

public sealed class OcrResult
{
    public string RawText { get; init; } = string.Empty;
    public string NormalizedText { get; init; } = string.Empty;
    public IReadOnlyList<OcrLine> Lines { get; init; } = [];
    public double QualityScore { get; init; }
    public string Provider { get; init; } = "receipt-ocr";
    public IReadOnlyList<string> AppliedFilters { get; init; } = [];
    public string? PreprocessNotes { get; init; }
    public IReadOnlyList<OcrVariantArtifact> Variants { get; init; } = [];
    public string? SelectedVariantId { get; init; }
    public IReadOnlyList<SectionConfidenceArtifact> SectionConfidences { get; init; } = [];
}
