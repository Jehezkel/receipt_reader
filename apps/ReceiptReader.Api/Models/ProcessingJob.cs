namespace ReceiptReader.Api.Models;

public sealed class ProcessingJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReceiptId { get; set; }
    public ProcessingStage Stage { get; set; } = ProcessingStage.Accepted;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string? ErrorCode { get; set; }
    public string Provider { get; set; } = "ocr-go";
}
