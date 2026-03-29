namespace ReceiptReader.Api.Services;

public interface IReceiptImagePreparationClient
{
    Task<ReceiptImagePreparationResult> PrepareAsync(IFormFile file, CancellationToken cancellationToken);
}
