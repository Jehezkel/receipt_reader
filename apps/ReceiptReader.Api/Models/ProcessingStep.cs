namespace ReceiptReader.Api.Models;

public sealed class ProcessingStep
{
    public ProcessingStage Stage { get; set; }
    public string Status { get; set; } = "Pending";
    public string Details { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
