namespace ReceiptReader.Api.Models;

public sealed class ReceiptConsistencyResult
{
    public decimal? DeclaredTotal { get; set; }
    public decimal? CalculatedItemsTotal { get; set; }
    public decimal? CalculatedItemsTotalAfterDiscounts { get; set; }
    public decimal? DifferenceToDeclaredTotal { get; set; }
    public ReceiptConsistencyStatus ConsistencyStatus { get; set; } = ReceiptConsistencyStatus.InsufficientData;
    public bool NeedsReview { get; set; }
}
