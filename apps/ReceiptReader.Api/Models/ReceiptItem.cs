namespace ReceiptReader.Api.Models;

public sealed class ReceiptItem
{
    public string Name { get; set; } = string.Empty;
    public decimal? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? TotalPrice { get; set; }
    public decimal? Discount { get; set; }
    public string? VatRate { get; set; }
    public double Confidence { get; set; }
    public double ArithmeticConfidence { get; set; }
    public ReceiptItemCandidateKind CandidateKind { get; set; } = ReceiptItemCandidateKind.Standard;
    public string Section { get; set; } = "items";
    public string? VatCode { get; set; }
    public string SourceLine { get; set; } = string.Empty;
    public IReadOnlyList<string> SourceLines { get; set; } = [];
    public IReadOnlyList<string> EvidenceLines { get; set; } = [];
    public IReadOnlyList<string> RecognitionHints { get; set; } = [];
    public bool WasReconstructedFromMultipleLines { get; set; }
    public bool WasAiCorrected { get; set; }
    public bool ExcludedByBalancer { get; set; }
    public string? RepairReason { get; set; }
    public IReadOnlyList<string> ParseWarnings { get; set; } = [];
}
