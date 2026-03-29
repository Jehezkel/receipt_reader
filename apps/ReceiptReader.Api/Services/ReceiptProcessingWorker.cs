using ReceiptReader.Api.Models;
using ReceiptReader.Api.Repositories;

namespace ReceiptReader.Api.Services;

public sealed class ReceiptProcessingWorker : BackgroundService
{
    private readonly IReceiptProcessingQueue _queue;
    private readonly IReceiptRepository _repository;
    private readonly IOcrClient _ocrClient;
    private readonly IReceiptParser _receiptParser;
    private readonly IReceiptConsistencyValidator _receiptConsistencyValidator;
    private readonly IReceiptRepairService _receiptRepairService;
    private readonly IAiEnrichmentService _aiEnrichmentService;
    private readonly ILogger<ReceiptProcessingWorker> _logger;

    public ReceiptProcessingWorker(
        IReceiptProcessingQueue queue,
        IReceiptRepository repository,
        IOcrClient ocrClient,
        IReceiptParser receiptParser,
        IReceiptConsistencyValidator receiptConsistencyValidator,
        IReceiptRepairService receiptRepairService,
        IAiEnrichmentService aiEnrichmentService,
        ILogger<ReceiptProcessingWorker> logger)
    {
        _queue = queue;
        _repository = repository;
        _ocrClient = ocrClient;
        _receiptParser = receiptParser;
        _receiptConsistencyValidator = receiptConsistencyValidator;
        _receiptRepairService = receiptRepairService;
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
                    QualityScore = ocrResult.QualityScore,
                    AppliedFilters = ocrResult.AppliedFilters.ToArray(),
                    PreprocessNotes = ocrResult.PreprocessNotes,
                    Variants = ocrResult.Variants,
                    SelectedVariantId = ocrResult.SelectedVariantId,
                    SectionConfidences = ocrResult.SectionConfidences,
                    ImagePreparation = receipt.ImagePreparation
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
                receipt.Payments = parsed.Payments.ToList();
                receipt.ProcessingSteps.AddRange(parsed.Steps);

                var parserConsistency = _receiptConsistencyValidator.Validate(receipt.ReceiptSummary, receipt.Items);
                ApplyConsistency(receipt, parserConsistency, "Parser consistency validation completed.");

                if (ShouldAttemptAiReconstruction(ocrResult, parserConsistency, receipt.Items))
                {
                    receipt.Job.Stage = ProcessingStage.AiReconstruction;
                    var aiInput = new ReceiptParseResult
                    {
                        Summary = receipt.ReceiptSummary,
                        Items = receipt.Items,
                        Payments = receipt.Payments,
                        Steps = parsed.Steps,
                        Sections = parsed.Sections
                    };
                    var aiEnriched = await _aiEnrichmentService.EnrichAsync(ocrResult, aiInput, stoppingToken);
                    receipt.AiWasTriggeredBecause = aiEnriched.TriggerReason;
                    var candidateItems = aiEnriched.Items.ToList();
                    var candidateSummary = aiEnriched.Summary;
                    var aiConsistency = _receiptConsistencyValidator.Validate(candidateSummary, candidateItems);

                    if (ShouldAcceptAiResult(receipt.Consistency, receipt.Items, aiConsistency, candidateItems))
                    {
                        receipt.ReceiptSummary = candidateSummary;
                        receipt.Items = candidateItems;
                        ApplyConsistency(receipt, aiConsistency, "AI reconstruction consistency validation completed.");
                    }

                    receipt.ProcessingSteps.Add(new ProcessingStep
                    {
                        Stage = ProcessingStage.AiReconstruction,
                        Status = aiEnriched.WasApplied ? "Completed" : "Skipped",
                        Details = aiEnriched.Details,
                        Timestamp = DateTimeOffset.UtcNow
                    });
                }
                else
                {
                    receipt.ProcessingSteps.Add(new ProcessingStep
                    {
                        Stage = ProcessingStage.AiReconstruction,
                        Status = "Skipped",
                        Details = "AI reconstruction skipped because OCR and parser signals were stable enough.",
                        Timestamp = DateTimeOffset.UtcNow
                    });
                }

                receipt.Job.Stage = ProcessingStage.DeterministicRepair;
                var repaired = _receiptRepairService.Repair(receipt.ReceiptSummary, receipt.Items);
                if (repaired.WasApplied)
                {
                    receipt.ReceiptSummary = repaired.Summary;
                    receipt.Items = repaired.Items.ToList();
                }

                var repairedConsistency = _receiptConsistencyValidator.Validate(receipt.ReceiptSummary, receipt.Items);
                ApplyConsistency(receipt, repairedConsistency, "Deterministic balance repair completed.");
                receipt.ProcessingSteps.Add(new ProcessingStep
                {
                    Stage = ProcessingStage.DeterministicRepair,
                    Status = repaired.WasApplied ? "Completed" : "Skipped",
                    Details = repaired.Details,
                    Timestamp = DateTimeOffset.UtcNow
                });

                receipt.Job.Stage = ProcessingStage.Completed;
                receipt.Job.FinishedAt = DateTimeOffset.UtcNow;
                receipt.Status = receipt.Consistency.NeedsReview
                    ? ReceiptStatus.CompletedWithWarnings
                    : receipt.ReceiptSummary.TotalMatchesItems
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

    private static void ApplyConsistency(ReceiptRecord receipt, ReceiptConsistencyResult consistency, string details)
    {
        receipt.Consistency = consistency;
        receipt.ReceiptSummary.TotalMatchesItems = consistency.ConsistencyStatus is ReceiptConsistencyStatus.Exact or ReceiptConsistencyStatus.ToleranceMatch;
        receipt.ReceiptSummary.NeedsReview = consistency.NeedsReview;
        receipt.ReceiptSummary.Confidence = AdjustSummaryConfidence(receipt.ReceiptSummary.Confidence, consistency.ConsistencyStatus, consistency.NeedsReview);
        receipt.ProcessingSteps.Add(new ProcessingStep
        {
            Stage = ProcessingStage.Validated,
            Status = consistency.NeedsReview ? "Warning" : "Completed",
            Details = $"{details} Status: {consistency.ConsistencyStatus}, difference: {consistency.DifferenceToDeclaredTotal?.ToString("0.00") ?? "n/a"}.",
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    private static double AdjustSummaryConfidence(double baseConfidence, ReceiptConsistencyStatus status, bool needsReview)
    {
        var adjusted = status switch
        {
            ReceiptConsistencyStatus.Exact => Math.Min(0.99, baseConfidence + 0.08),
            ReceiptConsistencyStatus.ToleranceMatch => Math.Min(0.94, baseConfidence + 0.03),
            ReceiptConsistencyStatus.InsufficientData => Math.Max(0.35, baseConfidence - 0.16),
            _ => Math.Max(0.25, baseConfidence - 0.22)
        };

        if (needsReview)
        {
            adjusted = Math.Max(0.2, adjusted - 0.05);
        }

        return Math.Round(adjusted, 2);
    }

    private static bool ShouldAcceptAiResult(
        ReceiptConsistencyResult currentConsistency,
        IReadOnlyList<ReceiptItem> currentItems,
        ReceiptConsistencyResult aiConsistency,
        IReadOnlyList<ReceiptItem> aiItems)
    {
        var currentScore = currentConsistency.ConsistencyStatus switch
        {
            ReceiptConsistencyStatus.Exact => 3,
            ReceiptConsistencyStatus.ToleranceMatch => 2,
            ReceiptConsistencyStatus.InsufficientData => 1,
            _ => 0
        };

        var aiScore = aiConsistency.ConsistencyStatus switch
        {
            ReceiptConsistencyStatus.Exact => 3,
            ReceiptConsistencyStatus.ToleranceMatch => 2,
            ReceiptConsistencyStatus.InsufficientData => 1,
            _ => 0
        };

        if (aiScore > currentScore)
        {
            return aiConsistency.ConsistencyStatus is ReceiptConsistencyStatus.Exact or ReceiptConsistencyStatus.ToleranceMatch;
        }

        if (aiScore < currentScore)
        {
            return false;
        }

        if (aiItems.Count < currentItems.Count)
        {
            return false;
        }

        var currentDifference = Math.Abs(currentConsistency.DifferenceToDeclaredTotal ?? decimal.MaxValue);
        var aiDifference = Math.Abs(aiConsistency.DifferenceToDeclaredTotal ?? decimal.MaxValue);

        return aiDifference <= currentDifference
            && (aiConsistency.ConsistencyStatus is ReceiptConsistencyStatus.Exact or ReceiptConsistencyStatus.ToleranceMatch);
    }

    private static bool ShouldAttemptAiReconstruction(
        OcrResult ocrResult,
        ReceiptConsistencyResult parserConsistency,
        IReadOnlyList<ReceiptItem> items)
    {
        if (parserConsistency.ConsistencyStatus is ReceiptConsistencyStatus.Exact or ReceiptConsistencyStatus.ToleranceMatch)
        {
            return false;
        }

        var lowQuality = ocrResult.QualityScore < 0.72;
        var lowSectionConfidence = ocrResult.SectionConfidences.Any(section => section.Confidence < 0.7 && section.Section is "items" or "totals" or "payments");
        var variantConflicts = ocrResult.Variants.Count > 1;
        var uncertainItems = items.Any(item => item.ParseWarnings.Count > 0 || item.Confidence < 0.65);

        return lowQuality || lowSectionConfidence || variantConflicts || uncertainItems;
    }
}
