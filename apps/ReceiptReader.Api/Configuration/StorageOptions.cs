namespace ReceiptReader.Api.Configuration;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string UploadRootPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "AppData", "uploads");
    public string PublicBaseUrl { get; set; } = "/uploads";
}
