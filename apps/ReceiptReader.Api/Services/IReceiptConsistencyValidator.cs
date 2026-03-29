using ReceiptReader.Api.Models;

namespace ReceiptReader.Api.Services;

public interface IReceiptConsistencyValidator
{
    ReceiptConsistencyResult Validate(ReceiptSummary summary, IReadOnlyList<ReceiptItem> items);
    int GetScore(ReceiptConsistencyStatus status);
}
