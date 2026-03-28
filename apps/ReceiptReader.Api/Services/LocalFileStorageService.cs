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

    public async Task<(string StoredPath, string PublicUrl)> SaveReceiptImageAsync(IFormFile file, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.UploadRootPath);

        var extension = Path.GetExtension(file.FileName);
        var fileName = $"{Guid.NewGuid():N}{extension}";
        var storedPath = Path.Combine(_options.UploadRootPath, fileName);

        await using var stream = File.Create(storedPath);
        await file.CopyToAsync(stream, cancellationToken);

        var publicUrl = $"{_options.PublicBaseUrl.TrimEnd('/')}/{fileName}";
        return (storedPath, publicUrl);
    }
}
