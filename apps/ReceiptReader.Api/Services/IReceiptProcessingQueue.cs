namespace ReceiptReader.Api.Services;

public interface IReceiptProcessingQueue
{
    ValueTask QueueAsync(Guid receiptId, CancellationToken cancellationToken);
    ValueTask<Guid> DequeueAsync(CancellationToken cancellationToken);
}
