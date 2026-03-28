using System.Threading.Channels;

namespace ReceiptReader.Api.Services;

public sealed class ReceiptProcessingQueue : IReceiptProcessingQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();

    public ValueTask QueueAsync(Guid receiptId, CancellationToken cancellationToken)
        => _channel.Writer.WriteAsync(receiptId, cancellationToken);

    public ValueTask<Guid> DequeueAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAsync(cancellationToken);
}
