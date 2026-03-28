namespace ReceiptReader.Api.Contracts;

public sealed class CreateReceiptResponse
{
    public Guid ReceiptId { get; init; }
    public Guid JobId { get; init; }
    public string StatusUrl { get; init; } = string.Empty;
    public string ReceiptUrl { get; init; } = string.Empty;
}
