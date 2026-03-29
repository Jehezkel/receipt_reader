using ReceiptReader.Api.Models;

namespace ReceiptReader.Api.Services;

public sealed class ReceiptImagePreparationResult
{
    public byte[] PreparedBytes { get; init; } = [];
    public ImagePreparationArtifact Artifact { get; init; } = new();
}
