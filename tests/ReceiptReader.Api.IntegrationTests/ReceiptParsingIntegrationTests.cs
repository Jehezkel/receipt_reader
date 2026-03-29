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

        var discountedCheese = Assert.Single(parsed.Items, item => item.Name.Contains("ZSEREK ROLMLE", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1.00m, discountedCheese.Discount);

        _output.WriteLine($"Declared total: {consistency.DeclaredTotal:0.00}");
        _output.WriteLine($"Calculated total after discounts: {consistency.CalculatedItemsTotalAfterDiscounts:0.00}");
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
        Assert.InRange(Math.Abs(consistency.DifferenceToDeclaredTotal.Value), 0m, 4m);
        Assert.Equal(ReceiptConsistencyStatus.Mismatch, consistency.ConsistencyStatus);
    }
}
