using ReceiptReader.Api.Models;

namespace ReceiptReader.Api.Contracts;

public sealed class ReceiptListItemResponse
{
    public Guid Id { get; init; }
    public ReceiptStatus Status { get; init; }
    public string ImageUrl { get; init; } = string.Empty;
    public string? MerchantName { get; init; }
    public DateOnly? PurchaseDate { get; init; }
    public decimal? TotalGross { get; init; }
    public bool NeedsReview { get; init; }
    public ReceiptConsistencyStatus ConsistencyStatus { get; init; }
    public double Confidence { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
