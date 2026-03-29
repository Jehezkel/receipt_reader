using ReceiptReader.Api.Configuration;
using Microsoft.Extensions.Options;

namespace ReceiptReader.Api.Services;

public sealed class LocalFileStorageService : IStorageService
{
    private readonly StorageOptions _options;

    public LocalFileStorageService(IOptions<StorageOptions> options)
    {
        _options = options.Value;
    }

    public async Task<(string StoredPath, string PublicUrl)> SaveReceiptImageAsync(
        ReadOnlyMemory<byte> fileContent,
        string fileExtension,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.UploadRootPath);

        var normalizedExtension = string.IsNullOrWhiteSpace(fileExtension)
            ? ".jpg"
            : fileExtension.StartsWith('.') ? fileExtension : $".{fileExtension}";
        var fileName = $"{Guid.NewGuid():N}{normalizedExtension}";
        var storedPath = Path.Combine(_options.UploadRootPath, fileName);

        await File.WriteAllBytesAsync(storedPath, fileContent.ToArray(), cancellationToken);

        var publicUrl = $"{_options.PublicBaseUrl.TrimEnd('/')}/{fileName}";
        return (storedPath, publicUrl);
    }
}
