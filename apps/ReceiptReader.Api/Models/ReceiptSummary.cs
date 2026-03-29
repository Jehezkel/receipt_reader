namespace ReceiptReader.Api.Models;

public sealed class ReceiptSummary
{
    public string? MerchantName { get; set; }
    public string? TaxId { get; set; }
    public DateOnly? PurchaseDate { get; set; }
    public string Currency { get; set; } = "PLN";
    public decimal? TotalGross { get; set; }
    public double Confidence { get; set; }
    public bool TotalMatchesItems { get; set; }
    public bool NeedsReview { get; set; }
}
