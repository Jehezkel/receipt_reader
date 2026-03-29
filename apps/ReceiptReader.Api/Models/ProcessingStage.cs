namespace ReceiptReader.Api.Models;

public enum ProcessingStage
{
    Accepted,
    Stored,
    PreparedForOcr,
    Ocr,
    OcrSelection,
    Parsed,
    DeterministicRepair,
    AiReconstruction,
    AiEnrichment,
    Validated,
    Completed,
    Failed
}
