using ReceiptReader.Api.Models;

namespace ReceiptReader.Api.Contracts;

public sealed class JobResponse
{
    public Guid Id { get; init; }
    public Guid ReceiptId { get; init; }
    public ProcessingStage Stage { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string? ErrorCode { get; init; }
    public string Provider { get; init; } = string.Empty;
}
