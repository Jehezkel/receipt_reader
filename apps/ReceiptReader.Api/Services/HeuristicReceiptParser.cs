using System.Globalization;
using System.Text.RegularExpressions;
using ReceiptReader.Api.Models;

namespace ReceiptReader.Api.Services;

public sealed partial class HeuristicReceiptParser : IReceiptParser
{
    public ReceiptParseResult Parse(OcrResult ocrResult)
    {
        var lines = ocrResult.NormalizedText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var summary = new ReceiptSummary
        {
            MerchantName = lines.FirstOrDefault(),
            TaxId = ExtractTaxId(lines),
            PurchaseDate = ExtractPurchaseDate(lines),
            Currency = "PLN",
            TotalGross = ExtractTotal(lines),
            Confidence = Math.Round(Math.Min(0.95, Math.Max(0.2, ocrResult.QualityScore + 0.1)), 2)
        };

        var items = lines
            .Select(ParseItem)
            .Where(item => item is not null)
            .Cast<ReceiptItem>()
            .ToList();

        var calculatedTotal = items.Sum(item => item.TotalPrice ?? 0m);
        summary.TotalMatchesItems = summary.TotalGross is null || calculatedTotal == 0m
            ? false
            : Math.Abs(summary.TotalGross.Value - calculatedTotal) < 0.02m;

        return new ReceiptParseResult
        {
            Summary = summary,
            Items = items,
            Steps =
            [
                new ProcessingStep
                {
                    Stage = ProcessingStage.Parsed,
                    Status = "Completed",
                    Details = $"Parsed {items.Count} potential line items.",
                    Timestamp = DateTimeOffset.UtcNow
                },
                new ProcessingStep
                {
                    Stage = ProcessingStage.Validated,
                    Status = summary.TotalMatchesItems ? "Completed" : "Warning",
                    Details = summary.TotalMatchesItems
                        ? "Receipt total matches parsed items."
                        : "Receipt total could not be confirmed against parsed items.",
                    Timestamp = DateTimeOffset.UtcNow
                }
            ]
        };
    }

    private static string? ExtractTaxId(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var match = TaxIdRegex().Match(line.Replace(" ", string.Empty));
            if (match.Success)
            {
                return match.Groups["nip"].Value;
            }
        }

        return null;
    }

    private static DateOnly? ExtractPurchaseDate(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var match = DateRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var normalized = match.Value.Replace('.', '-').Replace('/', '-');
            if (DateOnly.TryParseExact(normalized, ["yyyy-MM-dd", "dd-MM-yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return date;
            }
        }

        return null;
    }

    private static decimal? ExtractTotal(IEnumerable<string> lines)
    {
        foreach (var line in lines.Reverse())
        {
            if (!line.Contains("SUM", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("TOTAL", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var amountMatch = AmountRegex().Match(line);
            if (amountMatch.Success && TryParseDecimal(amountMatch.Value, out var amount))
            {
                return amount;
            }
        }

        return null;
    }

    private static ReceiptItem? ParseItem(string line)
    {
        var amountMatches = AmountRegex().Matches(line);
        if (amountMatches.Count == 0)
        {
            return null;
        }

        if (!TryParseDecimal(amountMatches[^1].Value, out var totalPrice))
        {
            return null;
        }

        decimal? quantity = null;
        decimal? unitPrice = null;

        if (amountMatches.Count > 1 && TryParseDecimal(amountMatches[^2].Value, out var possibleUnitPrice))
        {
            unitPrice = possibleUnitPrice;
        }

        var quantityMatch = QuantityRegex().Match(line);
        if (quantityMatch.Success && TryParseDecimal(quantityMatch.Groups["qty"].Value, out var parsedQuantity))
        {
            quantity = parsedQuantity;
        }

        var name = ItemNameRegex().Replace(line, string.Empty).Trim(" -:".ToCharArray());
        if (string.IsNullOrWhiteSpace(name))
        {
            name = line.Trim();
        }

        return new ReceiptItem
        {
            Name = name,
            Quantity = quantity,
            UnitPrice = unitPrice,
            TotalPrice = totalPrice,
            VatRate = null,
            Confidence = amountMatches.Count > 1 ? 0.72 : 0.55,
            SourceLine = line
        };
    }

    private static bool TryParseDecimal(string value, out decimal result)
    {
        var normalized = value.Replace(" ", string.Empty).Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out result);
    }

    [GeneratedRegex(@"(?<nip>\d{10})")]
    private static partial Regex TaxIdRegex();

    [GeneratedRegex(@"\b(\d{4}[-/.]\d{2}[-/.]\d{2}|\d{2}[-/.]\d{2}[-/.]\d{4})\b")]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"\d+(?:[.,]\d{2})")]
    private static partial Regex AmountRegex();

    [GeneratedRegex(@"(?<qty>\d+(?:[.,]\d+)?)\s*(x|X|\*)")]
    private static partial Regex QuantityRegex();

    [GeneratedRegex(@"[\d\s.,xX*]+$")]
    private static partial Regex ItemNameRegex();
}
