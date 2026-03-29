using ReceiptReader.Api.Models;
using ReceiptReader.Api.Services;
using Xunit.Abstractions;

namespace ReceiptReader.Api.IntegrationTests;

public sealed class ReceiptParsingIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public ReceiptParsingIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void LargeNettoReceipt_ShouldKeepCalculatedItemTotalCloseToDeclaredTotal()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "duzy_paragon.ocr.txt");
        var rawText = File.ReadAllText(fixturePath);

        var ocrResult = new OcrResult
        {
            RawText = rawText,
            NormalizedText = rawText,
            QualityScore = 0.82,
            Provider = "fixture",
            Lines = rawText
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => new OcrLine
                {
                    RawText = line,
                    NormalizedText = line,
                    Text = line,
                    Confidence = 0.82
                })
                .ToList()
        };

        var parser = new HeuristicReceiptParser();
        var validator = new ReceiptConsistencyValidator();

        var parsed = parser.Parse(ocrResult);
        var consistency = validator.Validate(parsed.Summary, parsed.Items);

        Assert.Equal("SKLEP NETTO NR 5521", parsed.Summary.MerchantName);
        Assert.Equal("852-10-21-463", parsed.Summary.TaxId);
        Assert.Equal(new DateOnly(2026, 1, 7), parsed.Summary.PurchaseDate);
        Assert.Equal(275.25m, parsed.Summary.TotalGross);
        Assert.Equal(275.25m, parsed.Summary.PaymentsTotal);
        Assert.Equal(275.25m, parsed.Summary.VatBreakdownTotal);
        Assert.Equal("Suma PLN 275,25", parsed.Summary.TotalSourceLine);
        Assert.Equal(2, parsed.Payments.Count);
        Assert.Contains(parsed.Payments, payment => payment.Method == "Bon" && payment.Amount == 100.00m);
        Assert.Contains(parsed.Payments, payment => payment.Method == "Karta" && payment.Amount == 175.25m);

        Assert.DoesNotContain(parsed.Items, item => item.Name.Contains("KARTA", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(parsed.Items, item => item.Name.Contains("WPLATA", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(parsed.Items, item => item.Name.Contains("SPRZEDAZ OPODATK", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(parsed.Items, item => item.Name.Contains("PTU", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(parsed.Items);
        Assert.True(consistency.CalculatedItemsTotalAfterDiscounts.HasValue, "Expected calculated total from parsed items.");

        var weightedChicken = Assert.Single(parsed.Items, item => item.Name.Contains("FILET Z KURCZ", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0.607m, weightedChicken.Quantity);
        Assert.Equal(25.98m, weightedChicken.UnitPrice);
        Assert.Equal(15.78m, weightedChicken.TotalPrice);
        Assert.True(weightedChicken.WasReconstructedFromMultipleLines);
        Assert.Contains("weighted-item", weightedChicken.RecognitionHints);

        var discountedCheese = Assert.Single(parsed.Items, item => item.Name.Contains("ZSEREK ROLMLE", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1.00m, discountedCheese.Discount);
        Assert.Contains("discount-applied", discountedCheese.RecognitionHints);

        _output.WriteLine($"Declared total: {consistency.DeclaredTotal:0.00}");
        _output.WriteLine($"Calculated total after discounts: {consistency.CalculatedItemsTotalAfterDiscounts:0.00}");
        _output.WriteLine($"Payments total: {consistency.PaymentsTotal:0.00}");
        _output.WriteLine($"VAT breakdown total: {consistency.VatBreakdownTotal:0.00}");
        _output.WriteLine($"Difference: {consistency.DifferenceToDeclaredTotal:0.00}");
        _output.WriteLine($"Status: {consistency.ConsistencyStatus}");
        _output.WriteLine("Parsed items breakdown:");

        foreach (var item in parsed.Items.OrderBy(item => item.Name))
        {
            var warnings = item.ParseWarnings.Count == 0
                ? "-"
                : string.Join(" | ", item.ParseWarnings);

            _output.WriteLine(
                $"{item.Name} || qty={item.Quantity?.ToString("0.###") ?? "null"} || unit={item.UnitPrice?.ToString("0.00") ?? "null"} || total={item.TotalPrice?.ToString("0.00") ?? "null"} || discount={item.Discount?.ToString("0.00") ?? "0.00"} || warnings={warnings}");
        }

        Assert.Equal(parsed.Summary.TotalGross, consistency.DeclaredTotal);
        Assert.NotNull(consistency.DifferenceToDeclaredTotal);
        Assert.Equal(0m, consistency.DifferenceToPaymentsTotal);
        Assert.Equal(0m, consistency.DifferenceToVatBreakdownTotal);
        Assert.InRange(Math.Abs(consistency.DifferenceToDeclaredTotal.Value), 0m, 4m);
        Assert.Equal(ReceiptConsistencyStatus.ToleranceMatch, consistency.ConsistencyStatus);
    }
}
