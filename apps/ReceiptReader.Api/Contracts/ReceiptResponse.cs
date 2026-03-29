using ReceiptReader.Api.Models;

namespace ReceiptReader.Api.Contracts;

public sealed class ReceiptResponse
{
    public Guid Id { get; init; }
    public ReceiptStatus Status { get; init; }
    public string ImageUrl { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public string RawOcrText { get; init; } = string.Empty;
    public IReadOnlyList<string> NormalizedLines { get; init; } = [];
    public IReadOnlyList<OcrLine> OcrLines { get; init; } = [];
    public IReadOnlyList<OcrVariantArtifact> OcrVariants { get; init; } = [];
    public string? SelectedOcrVariant { get; init; }
    public IReadOnlyList<SectionConfidenceArtifact> SectionConfidences { get; init; } = [];
    public ReceiptSummary ReceiptSummary { get; init; } = new();
    public ReceiptConsistencyResult Consistency { get; init; } = new();
    public IReadOnlyList<ReceiptItem> Items { get; init; } = [];
    public IReadOnlyList<ReceiptPayment> Payments { get; init; } = [];
    public double Confidence { get; init; }
    public string? AiWasTriggeredBecause { get; init; }
    public string? TotalEvidence { get; init; }
    public IReadOnlyList<ProcessingStep> ProcessingSteps { get; init; } = [];
}
