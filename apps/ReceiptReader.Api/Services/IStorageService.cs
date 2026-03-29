namespace ReceiptReader.Api.Services;

public interface IStorageService
{
    Task<(string StoredPath, string PublicUrl)> SaveReceiptImageAsync(
        ReadOnlyMemory<byte> fileContent,
        string fileExtension,
        CancellationToken cancellationToken);
}
