namespace ReceiptReader.Api.Models;

public sealed class OcrLine
{
    public string RawText { get; set; } = string.Empty;
    public string NormalizedText { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public OcrLineType LineType { get; set; } = OcrLineType.Unknown;
    public BoundingBox? BoundingBox { get; set; }
}
