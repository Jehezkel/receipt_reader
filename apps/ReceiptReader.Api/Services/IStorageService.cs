namespace ReceiptReader.Api.Services;

public interface IStorageService
{
    Task<(string StoredPath, string PublicUrl)> SaveReceiptImageAsync(IFormFile file, CancellationToken cancellationToken);
}
