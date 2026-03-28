namespace ReceiptReader.Api.Models;

public enum ReceiptStatus
{
    Pending,
    Processing,
    Completed,
    CompletedWithWarnings,
    Failed
}
