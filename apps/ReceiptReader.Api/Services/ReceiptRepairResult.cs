using ReceiptReader.Api.Models;

namespace ReceiptReader.Api.Services;

public sealed class ReceiptRepairResult
{
    public ReceiptSummary Summary { get; init; } = new();
    public IReadOnlyList<ReceiptItem> Items { get; init; } = [];
    public bool WasApplied { get; init; }
    public string Details { get; init; } = string.Empty;
}
