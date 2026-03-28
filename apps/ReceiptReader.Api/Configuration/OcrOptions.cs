namespace ReceiptReader.Api.Configuration;

public sealed class OcrOptions
{
    public const string SectionName = "Ocr";

    public string BaseUrl { get; set; } = "http://receipt-ocr:8080";
    public string LanguageHint { get; set; } = "pol+eng";
}
