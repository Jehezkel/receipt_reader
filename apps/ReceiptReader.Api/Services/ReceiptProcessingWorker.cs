using ReceiptReader.Api.Models;
using ReceiptReader.Api.Repositories;

namespace ReceiptReader.Api.Services;

public sealed class ReceiptProcessingWorker : BackgroundService
{
    private readonly IReceiptProcessingQueue _queue;
    private readonly IReceiptRepository _repository;
    private readonly IOcrClient _ocrClient;
    private readonly IReceiptParser _receiptParser;
    private readonly IAiEnrichmentService _aiEnrichmentService;
    private readonly ILogger<ReceiptProcessingWorker> _logger;

    public ReceiptProcessingWorker(
        IReceiptProcessingQueue queue,
        IReceiptRepository repository,
        IOcrClient ocrClient,
        IReceiptParser receiptParser,
        IAiEnrichmentService aiEnrichmentService,
        ILogger<ReceiptProcessingWorker> logger)
    {
        _queue = queue;
        _repository = repository;
        _ocrClient = ocrClient;
        _receiptParser = receiptParser;
        _aiEnrichmentService = aiEnrichmentService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var receiptId = await _queue.DequeueAsync(stoppingToken);
            var receipt = await _repository.GetAsync(receiptId, stoppingToken);
            if (receipt is null)
            {
                continue;
            }

            try
            {
                receipt.Status = ReceiptStatus.Processing;
                receipt.Job.Stage = ProcessingStage.Ocr;
                receipt.ProcessingSteps.Add(new ProcessingStep
                {
                    Stage = ProcessingStage.Ocr,
                    Status = "Started",
                    Details = "OCR job queued for processing.",
                    Timestamp = DateTimeOffset.UtcNow
                });
                await _repository.UpdateAsync(receipt, stoppingToken);

                var ocrResult = await _ocrClient.ProcessAsync(receipt.StoredFilePath, stoppingToken);

                receipt.OcrArtifact = new OcrArtifact
                {
                    RawText = ocrResult.RawText,
                    NormalizedText = ocrResult.NormalizedText,
                    Lines = ocrResult.Lines,
                    Provider = ocrResult.Provider,
                    QualityScore = ocrResult.QualityScore
                };
                receipt.ProcessingSteps.Add(new ProcessingStep
                {
                    Stage = ProcessingStage.Ocr,
                    Status = "Completed",
                    Details = $"OCR completed with provider {ocrResult.Provider}.",
                    Timestamp = DateTimeOffset.UtcNow
                });

                var parsed = _receiptParser.Parse(ocrResult);
                receipt.ReceiptSummary = parsed.Summary;
                receipt.Items = parsed.Items.ToList();
                receipt.ProcessingSteps.AddRange(parsed.Steps);

                receipt.Job.Stage = ProcessingStage.AiEnrichment;
                var aiEnriched = await _aiEnrichmentService.EnrichAsync(ocrResult, parsed, stoppingToken);
                receipt.ReceiptSummary = aiEnriched.Summary;
                receipt.Items = aiEnriched.Items.ToList();
                receipt.ProcessingSteps.Add(new ProcessingStep
                {
                    Stage = ProcessingStage.AiEnrichment,
                    Status = aiEnriched.WasApplied ? "Completed" : "Skipped",
                    Details = aiEnriched.Details,
                    Timestamp = DateTimeOffset.UtcNow
                });

                receipt.Job.Stage = ProcessingStage.Completed;
                receipt.Job.FinishedAt = DateTimeOffset.UtcNow;
                receipt.Status = receipt.ReceiptSummary.TotalMatchesItems
                    ? ReceiptStatus.Completed
                    : ReceiptStatus.CompletedWithWarnings;
                receipt.ProcessingSteps.Add(new ProcessingStep
                {
                    Stage = ProcessingStage.Completed,
                    Status = "Completed",
                    Details = "Receipt processing finished.",
                    Timestamp = DateTimeOffset.UtcNow
                });

                await _repository.UpdateAsync(receipt, stoppingToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Receipt processing failed for receipt {ReceiptId}.", receiptId);
                receipt.Status = ReceiptStatus.Failed;
                receipt.Job.Stage = ProcessingStage.Failed;
                receipt.Job.ErrorCode = "PROCESSING_FAILED";
                receipt.Job.FinishedAt = DateTimeOffset.UtcNow;
                receipt.ProcessingSteps.Add(new ProcessingStep
                {
                    Stage = ProcessingStage.Failed,
                    Status = "Failed",
                    Details = exception.Message,
                    Timestamp = DateTimeOffset.UtcNow
                });
                await _repository.UpdateAsync(receipt, stoppingToken);
            }
        }
    }
}
