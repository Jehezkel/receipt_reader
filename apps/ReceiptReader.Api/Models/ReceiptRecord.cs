namespace ReceiptReader.Api.Models;

public sealed class ReceiptRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ReceiptStatus Status { get; set; } = ReceiptStatus.Pending;
    public string ImageUrl { get; set; } = string.Empty;
    public string StoredFilePath { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ProcessingJob Job { get; set; } = new();
    public ReceiptSummary ReceiptSummary { get; set; } = new();
    public List<ReceiptItem> Items { get; set; } = [];
    public OcrArtifact? OcrArtifact { get; set; }
    public List<ProcessingStep> ProcessingSteps { get; set; } = [];
}
