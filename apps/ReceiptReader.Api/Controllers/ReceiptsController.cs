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

    public ReceiptsController(
        IReceiptRepository repository,
        IStorageService storageService,
        IReceiptProcessingQueue processingQueue)
    {
        _repository = repository;
        _storageService = storageService;
        _processingQueue = processingQueue;
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

        var (storedPath, publicUrl) = await _storageService.SaveReceiptImageAsync(file, cancellationToken);
        var receipt = new ReceiptRecord
        {
            ImageUrl = publicUrl,
            StoredFilePath = storedPath,
            OriginalFileName = file.FileName,
            Job = new ProcessingJob
            {
                ReceiptId = Guid.Empty,
                Stage = ProcessingStage.Stored,
                Provider = "ocr-go"
            },
            ProcessingSteps =
            [
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
            CreatedAt = receipt.CreatedAt,
            RawOcrText = receipt.OcrArtifact?.RawText ?? string.Empty,
            NormalizedLines = receipt.OcrArtifact?.NormalizedText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [],
            OcrLines = receipt.OcrArtifact?.Lines ?? [],
            ReceiptSummary = receipt.ReceiptSummary,
            Consistency = receipt.Consistency,
            Items = receipt.Items,
            Confidence = receipt.ReceiptSummary.Confidence,
            ProcessingSteps = receipt.ProcessingSteps
        };
}
