using ReceiptReader.Api.Models;

namespace ReceiptReader.Api.Services;

public interface IReceiptParser
{
    ReceiptParseResult Parse(OcrResult ocrResult);
}
