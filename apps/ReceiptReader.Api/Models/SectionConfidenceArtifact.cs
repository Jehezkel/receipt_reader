namespace ReceiptReader.Api.Models;

public sealed class SectionConfidenceArtifact
{
    public string Section { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public string? SelectedVariantId { get; set; }
    public string? Notes { get; set; }
}
