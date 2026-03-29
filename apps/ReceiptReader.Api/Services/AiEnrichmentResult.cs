using ReceiptReader.Api.Models;

namespace ReceiptReader.Api.Services;

public sealed class AiEnrichmentResult
{
    public ReceiptSummary Summary { get; init; } = new();
    public IReadOnlyList<ReceiptItem> Items { get; init; } = [];
    public bool WasApplied { get; init; }
    public string Provider { get; init; } = "disabled";
    public string Details { get; init; } = string.Empty;
    public string? TriggerReason { get; init; }
}
