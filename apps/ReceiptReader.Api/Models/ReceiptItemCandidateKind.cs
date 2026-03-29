namespace ReceiptReader.Api.Models;

public enum ReceiptItemCandidateKind
{
    Standard,
    Weighted,
    MultiLine,
    DiscountAdjusted,
    Repaired,
    Excluded
}
