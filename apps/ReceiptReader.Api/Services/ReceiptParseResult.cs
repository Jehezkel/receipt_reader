using ReceiptReader.Api.Models;

namespace ReceiptReader.Api.Services;

public sealed class ReceiptParseResult
{
    public ReceiptSummary Summary { get; init; } = new();
    public IReadOnlyList<ReceiptItem> Items { get; init; } = [];
    public IReadOnlyList<ReceiptPayment> Payments { get; init; } = [];
    public IReadOnlyList<ProcessingStep> Steps { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<OcrLine>> Sections { get; init; } =
        new Dictionary<string, IReadOnlyList<OcrLine>>();
}
