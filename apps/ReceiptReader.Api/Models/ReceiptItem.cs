namespace ReceiptReader.Api.Models;

public sealed class ReceiptItem
{
    public string Name { get; set; } = string.Empty;
    public decimal? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? TotalPrice { get; set; }
    public string? VatRate { get; set; }
    public double Confidence { get; set; }
    public string SourceLine { get; set; } = string.Empty;
}
