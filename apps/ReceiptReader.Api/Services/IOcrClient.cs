namespace ReceiptReader.Api.Services;

public interface IOcrClient
{
    Task<OcrResult> ProcessAsync(string imagePath, CancellationToken cancellationToken);
}
