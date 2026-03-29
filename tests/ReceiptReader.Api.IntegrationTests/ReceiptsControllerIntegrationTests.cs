using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using ReceiptReader.Api.Contracts;
using ReceiptReader.Api.Controllers;
using ReceiptReader.Api.Models;
using ReceiptReader.Api.Repositories;
using ReceiptReader.Api.Services;

namespace ReceiptReader.Api.IntegrationTests;

public sealed class ReceiptsControllerIntegrationTests
{
    [Fact]
    public async Task UpdateAsync_ShouldPersistManualChangesAndKeepExtractedSnapshot()
    {
        var repository = new InMemoryReceiptRepository();
        var receipt = new ReceiptRecord
        {
            StoredFilePath = "/tmp/receipt-a.jpg",
            ImageUrl = "/uploads/receipt-a.jpg",
            ImagePreparation = new ImagePreparationArtifact
            {
                PreparedWidth = 1200,
                PreparedHeight = 1800
            },
            ReceiptSummary = new ReceiptSummary
            {
                MerchantName = "Sklep OCR",
                TaxId = "111-222-33-44",
                PurchaseDate = new DateOnly(2026, 3, 28),
                Currency = "PLN",
                TotalGross = 14.40m,
                Confidence = 0.61
            },
            ExtractedReceiptSummary = new ReceiptSummary
            {
                MerchantName = "Sklep OCR",
                TaxId = "111-222-33-44",
                PurchaseDate = new DateOnly(2026, 3, 28),
                Currency = "PLN",
                TotalGross = 14.40m,
                Confidence = 0.61
            },
            Items =
            [
                new ReceiptItem
                {
                    Name = "MLEKO",
                    Quantity = 1m,
                    UnitPrice = 4.80m,
                    TotalPrice = 4.80m,
                    Confidence = 0.55,
                    ArithmeticConfidence = 0.72,
                    CandidateKind = ReceiptItemCandidateKind.Standard,
                    SourceLine = "MLEKO 4,80",
                    SourceLines = ["MLEKO 4,80"],
                    SourceLineNumbers = [4],
                    ParseWarnings = ["Low OCR confidence."]
                }
            ],
            ExtractedItems =
            [
                new ReceiptItem
                {
                    Name = "MLEKO",
                    Quantity = 1m,
                    UnitPrice = 4.80m,
                    TotalPrice = 4.80m,
                    Confidence = 0.55,
                    ArithmeticConfidence = 0.72,
                    CandidateKind = ReceiptItemCandidateKind.Standard,
                    SourceLine = "MLEKO 4,80",
                    SourceLines = ["MLEKO 4,80"],
                    SourceLineNumbers = [4],
                    ParseWarnings = ["Low OCR confidence."]
                }
            ]
        };

        await repository.AddAsync(receipt, CancellationToken.None);
        var controller = BuildController(repository);
        var request = new UpdateReceiptRequest
        {
            ReceiptSummary = new ReceiptSummaryUpdateRequest
            {
                MerchantName = "Sklep Zweryfikowany",
                TaxId = "111-222-33-44",
                PurchaseDate = new DateOnly(2026, 3, 28),
                Currency = "PLN",
                TotalGross = 13.80m
            },
            Items =
            [
                new ReceiptItemUpdateRequest
                {
                    Name = "MLEKO",
                    Quantity = 1m,
                    UnitPrice = 4.80m,
                    TotalPrice = 4.80m,
                    SourceLine = "MLEKO 4,80",
                    SourceLines = ["MLEKO 4,80"],
                    SourceLineNumbers = [4],
                    CandidateKind = ReceiptItemCandidateKind.Standard
                },
                new ReceiptItemUpdateRequest
                {
                    Name = "CHLEB",
                    Quantity = 1m,
                    UnitPrice = 9.00m,
                    TotalPrice = 9.00m,
                    SourceLine = "",
                    SourceLines = [],
                    SourceLineNumbers = [],
                    CandidateKind = ReceiptItemCandidateKind.Standard
                }
            ]
        };

        var result = await controller.UpdateAsync(receipt.Id, request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<ReceiptResponse>(okResult.Value);
        Assert.Equal("Sklep Zweryfikowany", payload.ReceiptSummary.MerchantName);
        Assert.Equal("Sklep OCR", payload.ExtractedReceiptSummary.MerchantName);
        Assert.Equal(2, payload.Items.Count);
        Assert.Single(payload.ExtractedItems);
        Assert.Equal(ReceiptStatus.Completed, payload.Status);
        Assert.Equal(ReceiptConsistencyStatus.Exact, payload.Consistency.ConsistencyStatus);
        Assert.Equal(1200, payload.ImageMetadata.Width);
        Assert.Equal(1800, payload.ImageMetadata.Height);
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveReceiptAndDeleteStoredImage()
    {
        var repository = new InMemoryReceiptRepository();
        var receipt = new ReceiptRecord
        {
            StoredFilePath = "/tmp/receipt-b.jpg",
            ImageUrl = "/uploads/receipt-b.jpg"
        };

        await repository.AddAsync(receipt, CancellationToken.None);
        var storage = new FakeStorageService();
        var controller = new ReceiptsController(
            repository,
            storage,
            new ReceiptProcessingQueue(),
            new FakeReceiptImagePreparationClient(),
            new ReceiptConsistencyValidator());

        var result = await controller.DeleteAsync(receipt.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Contains(receipt.StoredFilePath, storage.DeletedPaths);
        Assert.Null(await repository.GetAsync(receipt.Id, CancellationToken.None));
    }

    private static ReceiptsController BuildController(IReceiptRepository repository) =>
        new(
            repository,
            new FakeStorageService(),
            new ReceiptProcessingQueue(),
            new FakeReceiptImagePreparationClient(),
            new ReceiptConsistencyValidator());

    private sealed class FakeStorageService : IStorageService
    {
        public List<string> DeletedPaths { get; } = [];

        public Task<(string StoredPath, string PublicUrl)> SaveReceiptImageAsync(
            ReadOnlyMemory<byte> fileContent,
            string fileExtension,
            CancellationToken cancellationToken) =>
            Task.FromResult<(string StoredPath, string PublicUrl)>(("/tmp/test.jpg", "/uploads/test.jpg"));

        public Task DeleteReceiptImageAsync(string storedPath, CancellationToken cancellationToken)
        {
            DeletedPaths.Add(storedPath);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeReceiptImagePreparationClient : IReceiptImagePreparationClient
    {
        public Task<ReceiptImagePreparationResult> PrepareAsync(IFormFile file, CancellationToken cancellationToken) =>
            Task.FromResult(new ReceiptImagePreparationResult());
    }
}
