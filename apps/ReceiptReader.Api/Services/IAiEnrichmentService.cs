using ReceiptReader.Api.Models;

namespace ReceiptReader.Api.Services;

public interface IAiEnrichmentService
{
    Task<AiEnrichmentResult> EnrichAsync(OcrResult ocrResult, ReceiptParseResult parsedReceipt, CancellationToken cancellationToken);
}
