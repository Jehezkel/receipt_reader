namespace ReceiptReader.Api.Models;

public sealed class OcrVariantArtifact
{
    public string VariantId { get; set; } = string.Empty;
    public string VariantType { get; set; } = string.Empty;
    public string Section { get; set; } = "full";
    public int Psm { get; set; }
    public BoundingBox? CropBox { get; set; }
    public double RotationDegrees { get; set; }
    public string[] AppliedFilters { get; set; } = [];
    public double EstimatedReadabilityScore { get; set; }
    public double QualityScore { get; set; }
    public bool Selected { get; set; }
    public string RawText { get; set; } = string.Empty;
    public string NormalizedText { get; set; } = string.Empty;
}
