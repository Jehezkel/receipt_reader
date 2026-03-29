namespace ReceiptReader.Api.Models;

public sealed class ReceiptPayment
{
    public string Method { get; set; } = string.Empty;
    public decimal? Amount { get; set; }
    public string SourceLine { get; set; } = string.Empty;
}
