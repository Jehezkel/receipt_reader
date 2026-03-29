using ReceiptReader.Api.Models;

namespace ReceiptReader.Api.Services;

public interface IReceiptRepairService
{
    ReceiptRepairResult Repair(ReceiptSummary summary, IReadOnlyList<ReceiptItem> items);
}
