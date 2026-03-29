namespace ReceiptReader.Api.Models;

public enum ReceiptConsistencyStatus
{
    Exact,
    ToleranceMatch,
    Mismatch,
    InsufficientData
}
