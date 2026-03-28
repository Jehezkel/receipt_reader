namespace ReceiptReader.Api.Models;

public sealed class OcrLine
{
    public string Text { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public BoundingBox? BoundingBox { get; set; }
}
