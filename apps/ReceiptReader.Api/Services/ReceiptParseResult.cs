using ReceiptReader.Api.Models;

namespace ReceiptReader.Api.Services;

public sealed class ReceiptParseResult
{
    public ReceiptSummary Summary { get; init; } = new();
    public IReadOnlyList<ReceiptItem> Items { get; init; } = [];
    public IReadOnlyList<ProcessingStep> Steps { get; init; } = [];
}
