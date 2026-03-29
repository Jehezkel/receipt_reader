using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ReceiptReader.Api.Models;

namespace ReceiptReader.Api.Services;

public sealed partial class HeuristicReceiptParser : IReceiptParser
{
    public ReceiptParseResult Parse(OcrResult ocrResult)
    {
        var normalizedLines = NormalizeAndClassifyLines(ocrResult.Lines, ocrResult.NormalizedText);
        var summary = new ReceiptSummary
        {
            MerchantName = ExtractMerchantName(normalizedLines),
            TaxId = ExtractTaxId(normalizedLines.Select(line => line.NormalizedText)),
            PurchaseDate = ExtractPurchaseDate(normalizedLines.Select(line => line.NormalizedText)),
            Currency = "PLN",
            TotalGross = ExtractTotal(normalizedLines),
            Confidence = Math.Round(Math.Min(0.95, Math.Max(0.2, ocrResult.QualityScore + 0.06)), 2)
        };

        var items = ExtractItems(normalizedLines);

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
                    Details = $"Parsed {items.Count} line item candidates after classifying OCR lines.",
                    Timestamp = DateTimeOffset.UtcNow
                }
            ]
        };
    }

    private static List<OcrLine> NormalizeAndClassifyLines(IReadOnlyList<OcrLine> ocrLines, string fallbackText)
    {
        var sourceLines = ocrLines.Count > 0
            ? ocrLines
            : fallbackText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(text => new OcrLine { RawText = text, Text = text, NormalizedText = text, Confidence = 0.4 })
                .ToList();

        var normalized = new List<OcrLine>(sourceLines.Count);
        foreach (var line in sourceLines)
        {
            var normalizedText = NormalizeLineText(line.RawText.Length > 0 ? line.RawText : line.Text);
            var lineType = ClassifyLine(normalizedText);
            normalized.Add(new OcrLine
            {
                LineNumber = line.LineNumber,
                RawText = string.IsNullOrWhiteSpace(line.RawText) ? line.Text : line.RawText,
                NormalizedText = normalizedText,
                Text = normalizedText,
                Confidence = line.Confidence,
                CharacterCount = line.CharacterCount == 0 ? normalizedText.Length : line.CharacterCount,
                BoundingBox = line.BoundingBox,
                LineType = lineType
            });
        }

        return normalized;
    }

    private static List<ReceiptItem> ExtractItems(IReadOnlyList<OcrLine> normalizedLines)
    {
        var items = new List<ReceiptItem>();
        var skipped = new HashSet<int>();
        var parsingStopped = false;
        var receiptBodyStarted = false;
        var itemSectionStarted = false;

        for (var index = 0; index < normalizedLines.Count; index++)
        {
            if (skipped.Contains(index))
            {
                continue;
            }

            var current = normalizedLines[index];
            if (!receiptBodyStarted)
            {
                if (ReceiptBodyStartRegex().IsMatch(current.NormalizedText))
                {
                    receiptBodyStarted = true;
                }

                continue;
            }

            if (itemSectionStarted && IsHardStopLine(current))
            {
                parsingStopped = true;
            }

            if (parsingStopped)
            {
                current.LineType = UpgradeToPostItemSectionType(current);
                continue;
            }

            if (current.LineType == OcrLineType.Discount)
            {
                if (itemSectionStarted && items.Count > 0 && TryExtractDiscountAmount(current.NormalizedText, out var discountAmount))
                {
                    var lastItem = items[^1];
                    lastItem.Discount = decimal.Round((lastItem.Discount ?? 0m) + discountAmount, 2);
                    lastItem.CandidateKind = ReceiptItemCandidateKind.DiscountAdjusted;
                    lastItem.ParseWarnings = lastItem.ParseWarnings
                        .Append("Standalone discount line applied to previous item.")
                        .Distinct()
                        .ToArray();
                }

                continue;
            }

            if (current.LineType != OcrLineType.ItemCandidate)
            {
                continue;
            }

            itemSectionStarted = true;

            var candidateLines = new List<OcrLine> { current };
            if (ShouldMergeWithNext(normalizedLines, index))
            {
                candidateLines.Add(normalizedLines[index + 1]);
                skipped.Add(index + 1);
            }

            var parsedItem = ParseItemCandidate(candidateLines);
            if (parsedItem is not null)
            {
                parsedItem.CandidateKind = ResolveCandidateKind(parsedItem, candidateLines, current);
                items.Add(parsedItem);
            }
        }

        return items;
    }

    private static bool IsHardStopLine(OcrLine line)
    {
        if (line.LineType is OcrLineType.Total or OcrLineType.Payment or OcrLineType.Subtotal or OcrLineType.Vat)
        {
            return true;
        }

        return HardStopRegex().IsMatch(line.NormalizedText);
    }

    private static OcrLineType UpgradeToPostItemSectionType(OcrLine line)
    {
        if (line.LineType != OcrLineType.ItemCandidate)
        {
            return line.LineType;
        }

        if (PaymentLineRegex().IsMatch(line.NormalizedText))
        {
            return OcrLineType.Payment;
        }

        if (SubtotalLineRegex().IsMatch(line.NormalizedText) || SummaryLineRegex().IsMatch(line.NormalizedText))
        {
            return OcrLineType.Subtotal;
        }

        if (VatLineRegex().IsMatch(line.NormalizedText))
        {
            return OcrLineType.Vat;
        }

        return OcrLineType.Technical;
    }

    private static bool ShouldMergeWithNext(IReadOnlyList<OcrLine> lines, int index)
    {
        if (index + 1 >= lines.Count)
        {
            return false;
        }

        var current = lines[index];
        var next = lines[index + 1];
        if (next.LineType != OcrLineType.ItemCandidate)
        {
            return false;
        }

        var currentAmounts = ExtractAmountTokens(current.NormalizedText).Count;
        var nextAmounts = ExtractAmountTokens(next.NormalizedText).Count;

        return currentAmounts == 0 && nextAmounts > 0
            || currentAmounts == 1 && nextAmounts == 1 && !ContainsQuantity(current.NormalizedText) && ContainsQuantity(next.NormalizedText);
    }

    private static ReceiptItem? ParseItemCandidate(IReadOnlyList<OcrLine> candidateLines)
    {
        var combinedLine = string.Join(" ", candidateLines.Select(line => line.NormalizedText));
        var amountTokens = ExtractAmountTokens(combinedLine);
        if (amountTokens.Count == 0)
        {
            return null;
        }

        var warnings = new List<string>();
        var parsedAmounts = amountTokens
            .Select(token => TryParseDecimal(token, out var value) ? value : (decimal?)null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();
        if (parsedAmounts.Count == 0)
        {
            return null;
        }

        var totalPrice = parsedAmounts[^1];
        if (totalPrice <= 0)
        {
            return null;
        }

        decimal? quantity = null;
        decimal? unitPrice = null;
        decimal? discount = null;

        var quantityMatch = QuantityRegex().Match(combinedLine);
        if (quantityMatch.Success && TryParseDecimal(quantityMatch.Groups["qty"].Value, out var parsedQuantity))
        {
            quantity = parsedQuantity;
        }
        else
        {
            var leadingQuantity = LeadingQuantityRegex().Match(combinedLine);
            if (leadingQuantity.Success && TryParseDecimal(leadingQuantity.Groups["qty"].Value, out parsedQuantity))
            {
                quantity = parsedQuantity;
                warnings.Add("Quantity inferred from noisy quantity marker.");
            }
        }

        if (parsedAmounts.Count > 1)
        {
            unitPrice = parsedAmounts[^2];
        }

        if (combinedLine.Contains("RABAT", StringComparison.OrdinalIgnoreCase))
        {
            discount = totalPrice;
            warnings.Add("Discount line merged into item candidate.");
        }

        if (quantity.HasValue && (quantity.Value <= 0 || quantity.Value > 100))
        {
            warnings.Add("Discarded implausible quantity extracted from OCR.");
            quantity = null;
        }

        if (!unitPrice.HasValue && quantity.HasValue && quantity.Value != 0)
        {
            unitPrice = decimal.Round(totalPrice / quantity.Value, 2);
            warnings.Add("Unit price inferred from quantity and total price.");
        }

        if (!quantity.HasValue && unitPrice.HasValue && totalPrice > unitPrice.Value)
        {
            var inferredQuantity = totalPrice / unitPrice.Value;
            if (inferredQuantity > 1 && inferredQuantity < 20)
            {
                quantity = Math.Round(inferredQuantity, 3);
                warnings.Add("Quantity inferred from unit price and total price.");
            }
        }

        if (!quantity.HasValue && unitPrice.HasValue && unitPrice.Value == totalPrice)
        {
            quantity = 1m;
            warnings.Add("Quantity inferred as 1.");
        }

        ReconcileAmounts(parsedAmounts, ref quantity, ref unitPrice, ref totalPrice, warnings);

        var name = ExtractItemName(combinedLine);
        if (string.IsNullOrWhiteSpace(name))
        {
            warnings.Add("Item name is noisy and requires review.");
            name = combinedLine.Trim();
        }

        var confidence = candidateLines.Average(line => line.Confidence);
        confidence = warnings.Count > 0 ? Math.Max(0.3, confidence - 0.15) : Math.Min(0.92, confidence + 0.05);
        var arithmeticConfidence = CalculateArithmeticConfidence(quantity, unitPrice, totalPrice, warnings);

        return new ReceiptItem
        {
            Name = name,
            Quantity = quantity,
            UnitPrice = unitPrice,
            TotalPrice = totalPrice,
            Discount = discount,
            VatRate = ExtractVatRate(combinedLine),
            Confidence = Math.Round(confidence, 2),
            ArithmeticConfidence = arithmeticConfidence,
            SourceLine = combinedLine,
            SourceLines = candidateLines.Select(line => line.RawText).ToArray(),
            ParseWarnings = warnings.ToArray()
        };
    }

    private static void ReconcileAmounts(
        IReadOnlyList<decimal> parsedAmounts,
        ref decimal? quantity,
        ref decimal? unitPrice,
        ref decimal totalPrice,
        List<string> warnings)
    {
        if (quantity.HasValue && quantity.Value > 1 && unitPrice.HasValue)
        {
            var expectedTotal = decimal.Round(quantity.Value * unitPrice.Value, 2);
            if (!WithinTolerance(totalPrice, expectedTotal, 0.35m))
            {
                if (WithinTolerance(totalPrice, unitPrice.Value, 0.08m))
                {
                    quantity = 1m;
                    totalPrice = unitPrice.Value;
                    warnings.Add("Quantity reduced to 1 because total already matched unit price.");
                }
                else
                {
                    unitPrice = decimal.Round(totalPrice / quantity.Value, 2);
                    warnings.Add("Unit price adjusted to preserve trusted item total.");
                }
            }
        }

        if (quantity == 1m && unitPrice.HasValue)
        {
            if (WithinTolerance(totalPrice, unitPrice.Value, 0.25m))
            {
                var normalized = Math.Max(totalPrice, unitPrice.Value);
                totalPrice = normalized;
                unitPrice = normalized;
            }
            else if (totalPrice > unitPrice.Value * 1.5m)
            {
                unitPrice = totalPrice;
                warnings.Add("Unit price aligned to trusted total for single quantity item.");
            }
        }

        if (!quantity.HasValue && unitPrice.HasValue && totalPrice > 0 && unitPrice > totalPrice)
        {
            var tmp = unitPrice.Value;
            unitPrice = totalPrice;
            totalPrice = tmp;
            warnings.Add("Swapped unit and total price after OCR sanity check.");
        }

        if (quantity.HasValue && quantity.Value > 1 && unitPrice.HasValue && totalPrice < unitPrice.Value)
        {
            unitPrice = decimal.Round(totalPrice / quantity.Value, 2);
            warnings.Add("Unit price reduced because total is treated as more reliable than OCR unit price.");
        }

        if (quantity.HasValue && quantity.Value > 0 && unitPrice.HasValue)
        {
            var recomputedTotal = decimal.Round(quantity.Value * unitPrice.Value, 2);
            var hasFractionalQuantity = quantity.Value != decimal.Truncate(quantity.Value);
            if (!hasFractionalQuantity && WithinTolerance(recomputedTotal, totalPrice, 0.01m))
            {
                unitPrice = decimal.Round(recomputedTotal / quantity.Value, 2);
            }
            else if (!hasFractionalQuantity && Math.Abs(recomputedTotal - totalPrice) > 0.01m)
            {
                warnings.Add("Arithmetic mismatch kept because explicit line total is treated as more reliable than OCR multiplication.");
            }
        }
    }

    private static ReceiptItemCandidateKind ResolveCandidateKind(ReceiptItem item, IReadOnlyList<OcrLine> candidateLines, OcrLine current)
    {
        if (item.ExcludedByBalancer)
        {
            return ReceiptItemCandidateKind.Excluded;
        }

        if (item.Discount.HasValue)
        {
            return ReceiptItemCandidateKind.DiscountAdjusted;
        }

        if (candidateLines.Count > 1)
        {
            return ReceiptItemCandidateKind.MultiLine;
        }

        if (item.Quantity.HasValue && item.Quantity.Value != decimal.Truncate(item.Quantity.Value))
        {
            return ReceiptItemCandidateKind.Weighted;
        }

        return current.LineType == OcrLineType.ItemCandidate
            ? ReceiptItemCandidateKind.Standard
            : ReceiptItemCandidateKind.Repaired;
    }

    private static double CalculateArithmeticConfidence(decimal? quantity, decimal? unitPrice, decimal totalPrice, IReadOnlyList<string> warnings)
    {
        var confidence = 0.92;
        if (!quantity.HasValue || !unitPrice.HasValue)
        {
            confidence -= 0.14;
        }
        else
        {
            var expectedTotal = decimal.Round(quantity.Value * unitPrice.Value, 2);
            var difference = Math.Abs(expectedTotal - totalPrice);
            if (difference > 0.01m)
            {
                confidence -= (double)Math.Min(0.4m, difference / 6m);
            }
        }

        if (warnings.Count > 0)
        {
            confidence -= Math.Min(0.28, warnings.Count * 0.08);
        }

        return Math.Round(Math.Max(0.2, confidence), 2);
    }

    private static bool WithinTolerance(decimal actual, decimal expected, decimal relativeTolerance)
    {
        var absoluteTolerance = Math.Max(0.15m, expected * relativeTolerance);
        return Math.Abs(actual - expected) <= absoluteTolerance;
    }

    private static OcrLineType ClassifyLine(string normalizedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return OcrLineType.Technical;
        }

        if (TotalLineRegex().IsMatch(normalizedText))
        {
            return OcrLineType.Total;
        }

        if (DiscountLineRegex().IsMatch(normalizedText))
        {
            return OcrLineType.Discount;
        }

        if (SubtotalLineRegex().IsMatch(normalizedText))
        {
            return OcrLineType.Subtotal;
        }

        if (VatLineRegex().IsMatch(normalizedText))
        {
            return OcrLineType.Vat;
        }

        if (PaymentLineRegex().IsMatch(normalizedText))
        {
            return OcrLineType.Payment;
        }

        if (TechnicalLineRegex().IsMatch(normalizedText))
        {
            return OcrLineType.Technical;
        }

        if (ContainsPotentialItem(normalizedText))
        {
            return OcrLineType.ItemCandidate;
        }

        return OcrLineType.Header;
    }

    private static bool ContainsPotentialItem(string line)
    {
        if (DiscardLineRegex().IsMatch(line))
        {
            return false;
        }

        if (AddressLineRegex().IsMatch(line) || PostalCodeRegex().IsMatch(line))
        {
            return false;
        }

        var amountCount = ExtractAmountTokens(line).Count;
        if (amountCount == 0 && !ContainsQuantity(line))
        {
            return AlphaRegex().IsMatch(line)
                && !TechnicalLineRegex().IsMatch(line)
                && line.Length >= 4;
        }

        if (PaymentLineRegex().IsMatch(line) || SummaryLineRegex().IsMatch(line))
        {
            return false;
        }

        return true;
    }

    private static bool ContainsQuantity(string line) => QuantityRegex().IsMatch(line);

    private static string NormalizeLineText(string rawText)
    {
        var trimmed = rawText.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(trimmed.Length);
        foreach (var character in trimmed)
        {
            builder.Append(character switch
            {
                '|' => '1',
                _ => character
            });
        }

        var normalized = builder.ToString();
        normalized = Regex.Replace(normalized, @"[^\p{L}\p{N}\s,.\-:/xX*]", " ");
        normalized = Regex.Replace(normalized, @"(?<=\d)\s*([.,])\s*(?=\d{2,3}\b)", "$1");
        normalized = Regex.Replace(normalized, @"(?<=\d)([A-Z])(?=\s|$)", "$1");
        normalized = multipleSpaces.Replace(normalized, " ").Trim();
        normalized = NormalizeDigitCandidates(normalized);
        normalized = NormalizeAmountFragments(normalized);
        normalized = normalized.Replace(" ,", ",").Replace(" .", ".");
        return normalized;
    }

    private static string NormalizeDigitCandidates(string value)
    {
        var chars = value.ToCharArray();
        for (var index = 0; index < chars.Length; index++)
        {
            var current = chars[index];
            var surroundedByDigits = index > 0 && index < chars.Length - 1
                && char.IsDigit(chars[index - 1]) && char.IsDigit(chars[index + 1]);

            if (!surroundedByDigits)
            {
                continue;
            }

            chars[index] = current switch
            {
                'O' or 'o' => '0',
                'B' => '8',
                'I' or 'l' => '1',
                _ => current
            };
        }

        return new string(chars);
    }

    private static string? ExtractTaxId(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (!line.Contains("NIP", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = TaxIdRegex().Match(line.Replace(" ", string.Empty));
            if (match.Success)
            {
                return match.Groups["nip"].Value;
            }
        }

        return null;
    }

    private static string? ExtractMerchantName(IReadOnlyList<OcrLine> lines)
    {
        var sklepLine = lines.FirstOrDefault(line =>
            line.NormalizedText.Contains("SKLEP", StringComparison.OrdinalIgnoreCase)
            || line.NormalizedText.Contains("NETTO NR", StringComparison.OrdinalIgnoreCase));
        if (sklepLine is not null)
        {
            return sklepLine.NormalizedText;
        }

        var businessHeader = lines.FirstOrDefault(line =>
            line.LineType == OcrLineType.Header
            && !line.NormalizedText.Contains("NIP", StringComparison.OrdinalIgnoreCase)
            && !DateRegex().IsMatch(line.NormalizedText)
            && !line.NormalizedText.Contains("PARAGON", StringComparison.OrdinalIgnoreCase)
            && !line.NormalizedText.Contains("UL.", StringComparison.OrdinalIgnoreCase));

        return businessHeader?.NormalizedText ?? lines.FirstOrDefault()?.NormalizedText;
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

    private static decimal? ExtractTotal(IEnumerable<OcrLine> lines)
    {
        foreach (var line in lines.Reverse())
        {
            if (line.LineType != OcrLineType.Total)
            {
                continue;
            }

            var amountTokens = ExtractAmountTokens(line.NormalizedText);
            for (var index = amountTokens.Count - 1; index >= 0; index--)
            {
                if (TryParseDecimal(amountTokens[index], out var amount))
                {
                    return amount;
                }
            }
        }

        return null;
    }

    private static string ExtractItemName(string line)
    {
        var withoutTrailingValues = TrailingAmountsRegex().Replace(line, string.Empty);
        withoutTrailingValues = QuantityFragmentRegex().Replace(withoutTrailingValues, " ");
        withoutTrailingValues = DiscountWordRegex().Replace(withoutTrailingValues, " ");
        withoutTrailingValues = SummaryCodeRegex().Replace(withoutTrailingValues, " ");
        return multipleSpaces.Replace(withoutTrailingValues, " ").Trim(" -:".ToCharArray());
    }

    private static string? ExtractVatRate(string line)
    {
        var match = VatRateRegex().Match(line);
        return match.Success ? match.Groups["vat"].Value : null;
    }

    private static bool TryParseDecimal(string value, out decimal result)
    {
        var normalized = SanitizeAmountToken(value).Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryExtractDiscountAmount(string line, out decimal discount)
    {
        foreach (var amountToken in ExtractAmountTokens(line))
        {
            if (TryParseDecimal(amountToken, out var amount) && amount > 0)
            {
                discount = amount;
                return true;
            }
        }

        discount = 0m;
        return false;
    }

    private static List<string> ExtractAmountTokens(string line)
    {
        return AmountRegex()
            .Matches(line)
            .Select(match => SanitizeAmountToken(match.Value))
            .Where(token => token.Length > 0)
            .ToList();
    }

    private static string SanitizeAmountToken(string value)
    {
        var sanitized = value.Trim();
        sanitized = Regex.Replace(sanitized, @"(?<=\d)\s*([.,])\s*(?=\d{2,3}\b)", "$1");
        sanitized = Regex.Replace(sanitized, @"[^\d,.\-]", string.Empty);

        if (sanitized.Count(character => character is ',' or '.') > 1)
        {
            var lastSeparatorIndex = sanitized.LastIndexOfAny([',', '.']);
            if (lastSeparatorIndex > 0)
            {
                var integerPart = Regex.Replace(sanitized[..lastSeparatorIndex], @"[^\d\-]", string.Empty);
                var decimalPart = Regex.Replace(sanitized[(lastSeparatorIndex + 1)..], @"[^\d]", string.Empty);
                if (decimalPart.Length > 2)
                {
                    decimalPart = decimalPart[..2];
                }

                sanitized = decimalPart.Length > 0 ? $"{integerPart}.{decimalPart}" : integerPart;
            }
        }

        if (sanitized.EndsWith('.') || sanitized.EndsWith(','))
        {
            sanitized = sanitized[..^1];
        }

        var separatorIndex = sanitized.LastIndexOfAny([',', '.']);
        if (separatorIndex > 0
            && sanitized.Length - separatorIndex - 1 == 3
            && sanitized[..separatorIndex] != "0")
        {
            sanitized = sanitized[..^1];
        }

        return sanitized;
    }

    private static string NormalizeAmountFragments(string value)
    {
        return LooseAmountRegex().Replace(value, match => SanitizeAmountToken(match.Value));
    }

    [GeneratedRegex(@"(?<nip>\d{3}-?\d{2}-?\d{2}-?\d{3}|\d{10})")]
    private static partial Regex TaxIdRegex();

    [GeneratedRegex(@"\b(\d{4}[-/.]\d{2}[-/.]\d{2}|\d{2}[-/.]\d{2}[-/.]\d{4})\b")]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"\d+(?:\s*[.,]\s*\d{2,3})[A-Z]?")]
    private static partial Regex AmountRegex();

    [GeneratedRegex(@"(?<qty>\d+(?:[.,]\d+)?)\s*([xX\*%]|x\.|\.x)")]
    private static partial Regex QuantityRegex();

    [GeneratedRegex(@"(?<qty>\d+(?:[.,]\d+)?)\s*(?:[xX\*%]|x\.|\.x)\s*\d")]
    private static partial Regex LeadingQuantityRegex();

    [GeneratedRegex(@"[\s:,-]*(\d+(?:[.,]\d+)?\s*(x|X|\*)\s*)?\d+(?:[.,]\d{2})(?:\s+\d+(?:[.,]\d{2}))*\s*$")]
    private static partial Regex TrailingAmountsRegex();

    [GeneratedRegex(@"\b\d+(?:[.,]\d+)?\s*(x|X|\*)\b")]
    private static partial Regex QuantityFragmentRegex();

    [GeneratedRegex(@"\b(rabat|upust)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DiscountWordRegex();

    [GeneratedRegex(@"\b(?<vat>\d{1,2}%|[A-G])\b")]
    private static partial Regex VatRateRegex();

    [GeneratedRegex(@"(SUMA|TOTAL|RAZEM|DO ZAPLATY)", RegexOptions.IgnoreCase)]
    private static partial Regex TotalLineRegex();

    [GeneratedRegex(@"(RABAT|UPUST)", RegexOptions.IgnoreCase)]
    private static partial Regex DiscountLineRegex();

    [GeneratedRegex(@"(SPRZ[E3]DA(Z|Ż|S)\w*\s+OPOD\w*|SPRZEDAZ OPODATK|SUMA PT[UUVC]|K(W|U)OTA PT[UUVC]|PODATEK|NETTO|BRUTTO|OPADATK|OPODATK)", RegexOptions.IgnoreCase)]
    private static partial Regex SubtotalLineRegex();

    [GeneratedRegex(@"(PTU|VAT)", RegexOptions.IgnoreCase)]
    private static partial Regex VatLineRegex();

    [GeneratedRegex(@"(KARTA|KARTA P(L|Ł)ATNICZA|GOTOWKA|GOTÓWKA|RESZTA|PLATNOSC|P(L|Ł)ATNO(S|Ś)C|WP(L|Ł)ATA|WPIATA|DO ZAP(L|Ł)ATY|BON|BLIK|SODEXO)", RegexOptions.IgnoreCase)]
    private static partial Regex PaymentLineRegex();

    [GeneratedRegex(@"(PARAGON|FISKALNY|NIP|TERMINAL|KASA|SPRZEDAZ|SPRZEDAŻ|GODZ|DATA|NR|#)", RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalLineRegex();

    [GeneratedRegex(@"(SUMA|TOTAL|RAZEM|DO ZAP(L|Ł)ATY|PT[UUVC]|VAT|KARTA|GOTOWKA|GOTÓWKA|RESZTA|RABAT|UPUST|PARAGON|FISKALNY|SPRZ[E3]DA(Z|Ż|S)\w*\s+OPOD\w*|K(W|U)OTA PT[UUVC]|WP(L|Ł)ATA|WPIATA|PODATEK|BON|BLIK|SODEXO)", RegexOptions.IgnoreCase)]
    private static partial Regex DiscardLineRegex();

    [GeneratedRegex(@"(SPRZ[E3]DA(Z|Ż|S)\w*\s+OPOD\w*|SUMA PT[UUVC]|K(W|U)OTA PT[UUVC]|PODATEK|NETTO|BRUTTO|RESZTA|WP(L|Ł)ATA|WPIATA|KARTA P(L|Ł)ATNICZA|SODEXO)", RegexOptions.IgnoreCase)]
    private static partial Regex SummaryLineRegex();

    [GeneratedRegex(@"\b([A-G]|A\/B|A\/C|B\/C)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SummaryCodeRegex();

    [GeneratedRegex(@"(SUMA PLN|TOTAL PLN|RAZEM|DO ZAP(L|Ł)ATY|SUMA PT[UUVC]|K(W|U)OTA PT[UUVC]|WP(L|Ł)ATA|WPIATA|KARTA P(L|Ł)ATNICZA|SODEXO|BLIK)", RegexOptions.IgnoreCase)]
    private static partial Regex HardStopRegex();

    [GeneratedRegex(@"\d+(?:\s*[.,]\s*\d{2,3})[A-Z]?", RegexOptions.IgnoreCase)]
    private static partial Regex LooseAmountRegex();

    [GeneratedRegex(@"PARAGON\s+FISKALNY", RegexOptions.IgnoreCase)]
    private static partial Regex ReceiptBodyStartRegex();

    [GeneratedRegex(@"(\bUL\.\b|\bULICA\b|KOBYLANKA|SEROCK|ADRES)", RegexOptions.IgnoreCase)]
    private static partial Regex AddressLineRegex();

    [GeneratedRegex(@"\b\d{2}-\d{3}\b")]
    private static partial Regex PostalCodeRegex();

    [GeneratedRegex(@"[A-Za-zĄĆĘŁŃÓŚŹŻąćęłńóśźż]{2,}")]
    private static partial Regex AlphaRegex();

    private static readonly Regex multipleSpaces = new(@"\s+", RegexOptions.Compiled);
}
