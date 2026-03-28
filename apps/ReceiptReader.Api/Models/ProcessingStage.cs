namespace ReceiptReader.Api.Models;

public enum ProcessingStage
{
    Accepted,
    Stored,
    Ocr,
    Parsed,
    AiEnrichment,
    Validated,
    Completed,
    Failed
}
