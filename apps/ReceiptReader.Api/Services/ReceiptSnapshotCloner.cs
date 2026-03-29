using ReceiptReader.Api.Models;

namespace ReceiptReader.Api.Services;

internal static class ReceiptSnapshotCloner
{
    public static ReceiptSummary CloneSummary(ReceiptSummary summary) =>
        new()
        {
            MerchantName = summary.MerchantName,
            TaxId = summary.TaxId,
            PurchaseDate = summary.PurchaseDate,
            Currency = summary.Currency,
            TotalGross = summary.TotalGross,
            Confidence = summary.Confidence,
            TotalMatchesItems = summary.TotalMatchesItems,
            NeedsReview = summary.NeedsReview
        };

    public static List<ReceiptItem> CloneItems(IReadOnlyList<ReceiptItem> items) =>
        items.Select(CloneItem).ToList();

    public static ReceiptItem CloneItem(ReceiptItem item) =>
        new()
        {
            Name = item.Name,
            Quantity = item.Quantity,
            UnitPrice = item.UnitPrice,
            TotalPrice = item.TotalPrice,
            Discount = item.Discount,
            VatRate = item.VatRate,
            Confidence = item.Confidence,
            ArithmeticConfidence = item.ArithmeticConfidence,
            CandidateKind = item.CandidateKind,
            SourceLine = item.SourceLine,
            SourceLines = item.SourceLines.ToArray(),
            SourceLineNumbers = item.SourceLineNumbers.ToArray(),
            WasAiCorrected = item.WasAiCorrected,
            ExcludedByBalancer = item.ExcludedByBalancer,
            RepairReason = item.RepairReason,
            ParseWarnings = item.ParseWarnings.ToArray()
        };
}
