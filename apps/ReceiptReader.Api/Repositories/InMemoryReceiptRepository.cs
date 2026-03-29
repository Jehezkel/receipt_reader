using System.Collections.Concurrent;
using ReceiptReader.Api.Models;

namespace ReceiptReader.Api.Repositories;

public sealed class InMemoryReceiptRepository : IReceiptRepository
{
    private readonly ConcurrentDictionary<Guid, ReceiptRecord> _receipts = new();

    public Task<ReceiptRecord> AddAsync(ReceiptRecord receipt, CancellationToken cancellationToken)
    {
        _receipts[receipt.Id] = receipt;
        return Task.FromResult(receipt);
    }

    public Task<ReceiptRecord?> GetAsync(Guid receiptId, CancellationToken cancellationToken)
    {
        _receipts.TryGetValue(receiptId, out var receipt);
        return Task.FromResult(receipt);
    }

    public Task<IReadOnlyList<ReceiptRecord>> ListAsync(CancellationToken cancellationToken)
    {
        var items = _receipts.Values
            .OrderByDescending(item => item.CreatedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<ReceiptRecord>>(items);
    }

    public Task UpdateAsync(ReceiptRecord receipt, CancellationToken cancellationToken)
    {
        _receipts[receipt.Id] = receipt;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(Guid receiptId, CancellationToken cancellationToken)
    {
        return Task.FromResult(_receipts.TryRemove(receiptId, out _));
    }
}
