using ReceiptReader.Api.Models;

namespace ReceiptReader.Api.Contracts;

public sealed class UpdateReceiptRequest
{
    public ReceiptSummaryUpdateRequest ReceiptSummary { get; init; } = new();
    public IReadOnlyList<ReceiptItemUpdateRequest> Items { get; init; } = [];
}

public sealed class ReceiptSummaryUpdateRequest
{
    public string? MerchantName { get; init; }
    public string? TaxId { get; init; }
    public DateOnly? PurchaseDate { get; init; }
    public string Currency { get; init; } = "PLN";
    public decimal? TotalGross { get; init; }
}

public sealed class ReceiptItemUpdateRequest
{
    public string Name { get; init; } = string.Empty;
    public decimal? Quantity { get; init; }
    public decimal? UnitPrice { get; init; }
    public decimal? TotalPrice { get; init; }
    public decimal? Discount { get; init; }
    public string? VatRate { get; init; }
    public string SourceLine { get; init; } = string.Empty;
    public IReadOnlyList<string> SourceLines { get; init; } = [];
    public IReadOnlyList<int> SourceLineNumbers { get; init; } = [];
    public bool WasAiCorrected { get; init; }
    public string? RepairReason { get; init; }
    public ReceiptItemCandidateKind CandidateKind { get; init; } = ReceiptItemCandidateKind.Standard;
}
