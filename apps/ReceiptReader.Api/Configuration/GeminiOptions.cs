namespace ReceiptReader.Api.Configuration;

public sealed class GeminiOptions
{
    public const string SectionName = "Gemini";

    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gemini-2.5-flash";
    public int MaxRetryCount { get; set; } = 2;
    public int TimeoutSeconds { get; set; } = 25;
}
