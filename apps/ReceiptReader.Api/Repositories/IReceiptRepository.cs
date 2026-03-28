using ReceiptReader.Api.Models;

namespace ReceiptReader.Api.Repositories;

public interface IReceiptRepository
{
    Task<ReceiptRecord> AddAsync(ReceiptRecord receipt, CancellationToken cancellationToken);
    Task<ReceiptRecord?> GetAsync(Guid receiptId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ReceiptRecord>> ListAsync(CancellationToken cancellationToken);
    Task UpdateAsync(ReceiptRecord receipt, CancellationToken cancellationToken);
}
