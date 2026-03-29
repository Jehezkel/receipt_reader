namespace ReceiptReader.Api.Models;

public sealed class OcrArtifact
{
    public string RawText { get; set; } = string.Empty;
    public string NormalizedText { get; set; } = string.Empty;
    public IReadOnlyList<OcrLine> Lines { get; set; } = [];
    public string Provider { get; set; } = "receipt-ocr";
    public double QualityScore { get; set; }
    public string[] AppliedFilters { get; set; } = [];
    public string? PreprocessNotes { get; set; }
    public ImagePreparationArtifact? ImagePreparation { get; set; }
}
