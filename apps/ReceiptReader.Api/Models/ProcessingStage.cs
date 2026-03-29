namespace ReceiptReader.Api.Models;

public enum ProcessingStage
{
    Accepted,
    Stored,
    PreparedForOcr,
    Ocr,
    Parsed,
    DeterministicRepair,
    AiEnrichment,
    Validated,
    Completed,
    Failed
}
