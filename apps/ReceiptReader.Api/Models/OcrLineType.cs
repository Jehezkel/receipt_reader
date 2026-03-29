namespace ReceiptReader.Api.Models;

public enum OcrLineType
{
    Unknown,
    Header,
    ItemCandidate,
    Subtotal,
    Total,
    Vat,
    Discount,
    Payment,
    Technical
}
