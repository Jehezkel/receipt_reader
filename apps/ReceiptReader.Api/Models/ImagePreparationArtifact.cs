namespace ReceiptReader.Api.Models;

public sealed class ImagePreparationArtifact
{
    public string Provider { get; set; } = "receipt-ocr";
    public string OutputContentType { get; set; } = "image/jpeg";
    public string OutputExtension { get; set; } = ".jpg";
    public int OriginalWidth { get; set; }
    public int OriginalHeight { get; set; }
    public int PreparedWidth { get; set; }
    public int PreparedHeight { get; set; }
    public long OriginalBytes { get; set; }
    public long PreparedBytes { get; set; }
    public bool UsedFallback { get; set; }
    public bool CropApplied { get; set; }
    public string[] AppliedFilters { get; set; } = [];
    public string? Notes { get; set; }
    public BoundingBox? CropBox { get; set; }
    public BoundingBox? BodyCropBox { get; set; }
    public BoundingBox? FooterCropBox { get; set; }
}
