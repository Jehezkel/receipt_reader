using Microsoft.AspNetCore.Mvc;
using ReceiptReader.Api.Contracts;
using ReceiptReader.Api.Models;
using ReceiptReader.Api.Repositories;
using ReceiptReader.Api.Services;

namespace ReceiptReader.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class ReceiptsController : ControllerBase
{
    private readonly IReceiptRepository _repository;
    private readonly IStorageService _storageService;
    private readonly IReceiptProcessingQueue _processingQueue;
    private readonly IReceiptImagePreparationClient _imagePreparationClient;
    private readonly IReceiptConsistencyValidator _consistencyValidator;

    public ReceiptsController(
        IReceiptRepository repository,
        IStorageService storageService,
        IReceiptProcessingQueue processingQueue,
        IReceiptImagePreparationClient imagePreparationClient,
        IReceiptConsistencyValidator consistencyValidator)
    {
        _repository = repository;
        _storageService = storageService;
        _processingQueue = processingQueue;
        _imagePreparationClient = imagePreparationClient;
        _consistencyValidator = consistencyValidator;
    }

    [HttpPost]
    [RequestSizeLimit(50_000_000)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<CreateReceiptResponse>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<CreateReceiptResponse>> CreateAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length == 0)
        {
            return BadRequest("Image file is required.");
        }

        var preparation = await _imagePreparationClient.PrepareAsync(file, cancellationToken);
        var (storedPath, publicUrl) = await _storageService.SaveReceiptImageAsync(
            preparation.PreparedBytes,
            preparation.Artifact.OutputExtension,
            cancellationToken);
        var receipt = new ReceiptRecord
        {
            ImageUrl = publicUrl,
            StoredFilePath = storedPath,
            OriginalFileName = file.FileName,
            ImagePreparation = preparation.Artifact,
            Job = new ProcessingJob
            {
                ReceiptId = Guid.Empty,
                Stage = ProcessingStage.PreparedForOcr,
                Provider = "ocr-go"
            },
            ProcessingSteps =
            [
                new ProcessingStep
                {
                    Stage = ProcessingStage.PreparedForOcr,
                    Status = preparation.Artifact.UsedFallback ? "Warning" : "Completed",
                    Details = BuildPreparationDetails(preparation.Artifact),
                    Timestamp = DateTimeOffset.UtcNow
                },
                new ProcessingStep
                {
                    Stage = ProcessingStage.Stored,
                    Status = "Completed",
                    Details = $"Stored image at {storedPath}.",
                    Timestamp = DateTimeOffset.UtcNow
                }
            ]
        };
        receipt.Job.ReceiptId = receipt.Id;

        await _repository.AddAsync(receipt, cancellationToken);
        await _processingQueue.QueueAsync(receipt.Id, cancellationToken);

        var response = new CreateReceiptResponse
        {
            ReceiptId = receipt.Id,
            JobId = receipt.Job.Id,
            StatusUrl = $"/api/jobs/{receipt.Job.Id}",
            ReceiptUrl = $"/api/receipts/{receipt.Id}"
        };

        return Accepted(response.StatusUrl, response);
    }

    private static string BuildPreparationDetails(ImagePreparationArtifact artifact)
    {
        var cropSummary = artifact.CropApplied && artifact.CropBox is not null
            ? $"Prepared receipt crop {artifact.CropBox.Width}x{artifact.CropBox.Height} at ({artifact.CropBox.X},{artifact.CropBox.Y})"
            : "Prepared image without crop";
        var dimensions = artifact.OriginalWidth > 0 && artifact.OriginalHeight > 0
            ? $"dimensions {artifact.OriginalWidth}x{artifact.OriginalHeight} -> {artifact.PreparedWidth}x{artifact.PreparedHeight}"
            : $"prepared dimensions {artifact.PreparedWidth}x{artifact.PreparedHeight}";
        var bytes = artifact.OriginalBytes > 0
            ? $"size {artifact.OriginalBytes}B -> {artifact.PreparedBytes}B"
            : $"prepared size {artifact.PreparedBytes}B";

        return $"{cropSummary}; {dimensions}; {bytes}. {artifact.Notes}".Trim();
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ReceiptListItemResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ReceiptListItemResponse>>> ListAsync(CancellationToken cancellationToken)
    {
        var receipts = await _repository.ListAsync(cancellationToken);
        return Ok(receipts.Select(MapListItem).ToList());
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<ReceiptResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReceiptResponse>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var receipt = await _repository.GetAsync(id, cancellationToken);
        if (receipt is null)
        {
            return NotFound();
        }

        return Ok(MapReceipt(receipt));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<ReceiptResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReceiptResponse>> UpdateAsync(Guid id, UpdateReceiptRequest request, CancellationToken cancellationToken)
    {
        var receipt = await _repository.GetAsync(id, cancellationToken);
        if (receipt is null)
        {
            return NotFound();
        }

        receipt.ReceiptSummary = new ReceiptSummary
        {
            MerchantName = NormalizeOptional(request.ReceiptSummary.MerchantName),
            TaxId = NormalizeOptional(request.ReceiptSummary.TaxId),
            PurchaseDate = request.ReceiptSummary.PurchaseDate,
            Currency = string.IsNullOrWhiteSpace(request.ReceiptSummary.Currency) ? "PLN" : request.ReceiptSummary.Currency.Trim().ToUpperInvariant(),
            TotalGross = request.ReceiptSummary.TotalGross,
            Confidence = 0.99
        };

        receipt.Items = request.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .Select(MapManualItem)
            .ToList();

        ApplyConsistency(receipt);
        receipt.Status = receipt.Consistency.NeedsReview || !receipt.ReceiptSummary.TotalMatchesItems
            ? ReceiptStatus.CompletedWithWarnings
            : ReceiptStatus.Completed;
        receipt.ProcessingSteps.Add(new ProcessingStep
        {
            Stage = ProcessingStage.Validated,
            Status = "Completed",
            Details = $"Receipt manually reviewed and updated with {receipt.Items.Count} item(s).",
            Timestamp = DateTimeOffset.UtcNow
        });

        await _repository.UpdateAsync(receipt, cancellationToken);
        return Ok(MapReceipt(receipt));
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var receipt = await _repository.GetAsync(id, cancellationToken);
        if (receipt is null)
        {
            return NotFound();
        }

        await _storageService.DeleteReceiptImageAsync(receipt.StoredFilePath, cancellationToken);
        await _repository.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    private static ReceiptListItemResponse MapListItem(ReceiptRecord receipt) =>
        new()
        {
            Id = receipt.Id,
            Status = receipt.Status,
            ImageUrl = receipt.ImageUrl,
            MerchantName = receipt.ReceiptSummary.MerchantName,
            PurchaseDate = receipt.ReceiptSummary.PurchaseDate,
            TotalGross = receipt.ReceiptSummary.TotalGross,
            NeedsReview = receipt.Consistency.NeedsReview,
            ConsistencyStatus = receipt.Consistency.ConsistencyStatus,
            Confidence = receipt.ReceiptSummary.Confidence,
            CreatedAt = receipt.CreatedAt
        };

    internal static ReceiptResponse MapReceipt(ReceiptRecord receipt) =>
        new()
        {
            Id = receipt.Id,
            Status = receipt.Status,
            ImageUrl = receipt.ImageUrl,
            ImageMetadata = new ReceiptImageMetadataResponse
            {
                Width = receipt.ImagePreparation?.PreparedWidth ?? receipt.OcrArtifact?.ImagePreparation?.PreparedWidth ?? 0,
                Height = receipt.ImagePreparation?.PreparedHeight ?? receipt.OcrArtifact?.ImagePreparation?.PreparedHeight ?? 0
            },
            CreatedAt = receipt.CreatedAt,
            RawOcrText = receipt.OcrArtifact?.RawText ?? string.Empty,
            NormalizedLines = receipt.OcrArtifact?.NormalizedText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [],
            OcrLines = receipt.OcrArtifact?.Lines ?? [],
            ReceiptSummary = receipt.ReceiptSummary,
            ExtractedReceiptSummary = receipt.ExtractedReceiptSummary ?? receipt.ReceiptSummary,
            Consistency = receipt.Consistency,
            Items = receipt.Items,
            ExtractedItems = receipt.ExtractedItems.Count > 0 ? receipt.ExtractedItems : receipt.Items,
            Confidence = receipt.ReceiptSummary.Confidence,
            ProcessingSteps = receipt.ProcessingSteps
        };

    private void ApplyConsistency(ReceiptRecord receipt)
    {
        var consistency = _consistencyValidator.Validate(receipt.ReceiptSummary, receipt.Items);
        receipt.Consistency = consistency;
        receipt.ReceiptSummary.TotalMatchesItems = consistency.ConsistencyStatus is ReceiptConsistencyStatus.Exact or ReceiptConsistencyStatus.ToleranceMatch;
        receipt.ReceiptSummary.NeedsReview = consistency.NeedsReview;
    }

    private static ReceiptItem MapManualItem(ReceiptItemUpdateRequest item)
    {
        var normalizedName = item.Name.Trim();
        var confidence = string.IsNullOrWhiteSpace(item.SourceLine) && item.SourceLines.Count == 0 ? 0.94 : 0.99;
        var arithmeticConfidence = CalculateArithmeticConfidence(item.Quantity, item.UnitPrice, item.TotalPrice);

        return new ReceiptItem
        {
            Name = normalizedName,
            Quantity = item.Quantity,
            UnitPrice = item.UnitPrice,
            TotalPrice = item.TotalPrice,
            Discount = item.Discount,
            VatRate = NormalizeOptional(item.VatRate),
            Confidence = confidence,
            ArithmeticConfidence = arithmeticConfidence,
            CandidateKind = item.CandidateKind,
            SourceLine = string.IsNullOrWhiteSpace(item.SourceLine) ? normalizedName : item.SourceLine.Trim(),
            SourceLines = item.SourceLines
                .Select(NormalizeOptional)
                .OfType<string>()
                .ToArray(),
            SourceLineNumbers = item.SourceLineNumbers.Distinct().Order().ToArray(),
            WasAiCorrected = item.WasAiCorrected,
            ExcludedByBalancer = false,
            RepairReason = NormalizeOptional(item.RepairReason),
            ParseWarnings = []
        };
    }

    private static double CalculateArithmeticConfidence(decimal? quantity, decimal? unitPrice, decimal? totalPrice)
    {
        if (totalPrice is null)
        {
            return 0.72;
        }

        if (quantity.HasValue && unitPrice.HasValue)
        {
            var calculated = decimal.Round(quantity.Value * unitPrice.Value, 2);
            return calculated == totalPrice.Value ? 0.99 : 0.78;
        }

        return 0.9;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
