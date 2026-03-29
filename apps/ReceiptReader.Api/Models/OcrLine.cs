namespace ReceiptReader.Api.Models;

public sealed class OcrLine
{
    public int LineNumber { get; set; }
    public string RawText { get; set; } = string.Empty;
    public string NormalizedText { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public int CharacterCount { get; set; }
    public OcrLineType LineType { get; set; } = OcrLineType.Unknown;
    public string Section { get; set; } = "unknown";
    public string VariantId { get; set; } = "selected";
    public IReadOnlyList<string> AlternateTexts { get; set; } = [];
    public BoundingBox? BoundingBox { get; set; }
}
