using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReceiptReader.Api.Configuration;
using ReceiptReader.Api.Models;
using ReceiptReader.Api.Services;

namespace ReceiptReader.Api.IntegrationTests;

public sealed class DeterministicRepairIntegrationTests
{
    [Fact]
    public void Parser_ShouldKeepExplicitTotalsForWeightedAndDiscountedLines()
    {
        var rawText = """
        SKLEP TESTOWY
        NIP 123-45-67-890
        2026-03-28
        PARAGON FISKALNY
        JOGURT NAT 2 x 1,83 3,78C
        FILET Z KURCZ. LUZ
        0,607 x 25,98 15,78C
        SER TOPIONY 3 x 3,29 9,87C
        rabat -1,00
        SUMA PLN 28,43
        """;

        var parser = new HeuristicReceiptParser();
        var parsed = parser.Parse(BuildOcrResult(rawText));

        var yogurt = Assert.Single(parsed.Items, item => item.Name.Contains("JOGURT", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3.78m, yogurt.TotalPrice);
        Assert.InRange(yogurt.ArithmeticConfidence, 0.2, 0.9);

        var weighted = Assert.Single(parsed.Items, item => item.Name.Contains("FILET Z KURCZ", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ReceiptItemCandidateKind.MultiLine, weighted.CandidateKind);
        Assert.Equal(15.78m, weighted.TotalPrice);
        Assert.Equal(0.607m, weighted.Quantity);

        var discounted = Assert.Single(parsed.Items, item => item.Name.Contains("SER TOPIONY", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1.00m, discounted.Discount);
        Assert.Equal(ReceiptItemCandidateKind.DiscountAdjusted, discounted.CandidateKind);
    }

    [Fact]
    public void DeterministicRepair_ShouldUseSafeArithmeticVariantBeforeAi()
    {
        var validator = new ReceiptConsistencyValidator();
        var service = new DeterministicReceiptRepairService(validator);
        var summary = new ReceiptSummary
        {
            Currency = "PLN",
            TotalGross = 7.50m,
            Confidence = 0.7
        };

        var items = new[]
        {
            new ReceiptItem
            {
                Name = "JOGURT",
                Quantity = 2m,
                UnitPrice = 1.50m,
                TotalPrice = 3.50m,
                Confidence = 0.6,
                ArithmeticConfidence = 0.45,
                CandidateKind = ReceiptItemCandidateKind.Standard,
                SourceLine = "JOGURT 2 x 1,50 3,50",
                SourceLines = ["JOGURT 2 x 1,50 3,50"],
                ParseWarnings = ["Arithmetic mismatch kept because explicit line total is treated as more reliable than OCR multiplication."]
            },
            new ReceiptItem
            {
                Name = "MLEKO",
                Quantity = 1m,
                UnitPrice = 4.50m,
                TotalPrice = 4.50m,
                Confidence = 0.8,
                ArithmeticConfidence = 0.9,
                CandidateKind = ReceiptItemCandidateKind.Standard,
                SourceLine = "MLEKO 1 x 4,50 4,50",
                SourceLines = ["MLEKO 1 x 4,50 4,50"]
            }
        };

        var baseline = validator.Validate(summary, items);
        Assert.Equal(ReceiptConsistencyStatus.Mismatch, baseline.ConsistencyStatus);

        var repaired = service.Repair(summary, items);
        var repairedConsistency = validator.Validate(summary, repaired.Items);

        Assert.True(repaired.WasApplied);
        Assert.Equal(ReceiptConsistencyStatus.Exact, repairedConsistency.ConsistencyStatus);

        var repairedYogurt = Assert.Single(repaired.Items, item => item.Name == "JOGURT");
        Assert.Equal(3.00m, repairedYogurt.TotalPrice);
        Assert.Equal(ReceiptItemCandidateKind.Repaired, repairedYogurt.CandidateKind);
        Assert.Equal("Total price realigned from quantity and unit price.", repairedYogurt.RepairReason);
    }

    [Fact]
    public async Task Gemini_ShouldSkipWhenDeterministicResultAlreadyBalancesAsync()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        using var client = new HttpClient(handler);
        var service = new GeminiAiEnrichmentService(
            client,
            Options.Create(new GeminiOptions
            {
                Enabled = true,
                ApiKey = "test-key",
                Model = "gemini-2.5-flash"
            }),
            NullLogger<GeminiAiEnrichmentService>.Instance);

        var parsed = new ReceiptParseResult
        {
            Summary = new ReceiptSummary
            {
                MerchantName = "Sklep",
                Currency = "PLN",
                TotalGross = 8.00m,
                TotalMatchesItems = true,
                Confidence = 0.8
            },
            Items =
            [
                new ReceiptItem
                {
                    Name = "CHLEB",
                    Quantity = 1m,
                    UnitPrice = 4.00m,
                    TotalPrice = 4.00m,
                    Confidence = 0.9,
                    ArithmeticConfidence = 0.9,
                    CandidateKind = ReceiptItemCandidateKind.Standard,
                    SourceLine = "CHLEB 1 x 4,00 4,00",
                    SourceLines = ["CHLEB 1 x 4,00 4,00"]
                },
                new ReceiptItem
                {
                    Name = "MLEKO",
                    Quantity = 1m,
                    UnitPrice = 4.00m,
                    TotalPrice = 4.00m,
                    Confidence = 0.9,
                    ArithmeticConfidence = 0.9,
                    CandidateKind = ReceiptItemCandidateKind.Standard,
                    SourceLine = "MLEKO 1 x 4,00 4,00",
                    SourceLines = ["MLEKO 1 x 4,00 4,00"]
                }
            ]
        };

        var result = await service.EnrichAsync(BuildOcrResult("PARAGON FISKALNY\nCHLEB 1 x 4,00 4,00\nMLEKO 1 x 4,00 4,00\nSUMA PLN 8,00"), parsed, CancellationToken.None);

        Assert.False(result.WasApplied);
        Assert.Equal("skipped", result.Provider);
        Assert.Equal(0, handler.CallCount);
    }

    private static OcrResult BuildOcrResult(string rawText) =>
        new()
        {
            RawText = rawText,
            NormalizedText = rawText,
            QualityScore = 0.82,
            Provider = "fixture",
            Lines = rawText
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select((line, index) => new OcrLine
                {
                    LineNumber = index,
                    RawText = line,
                    NormalizedText = line,
                    Text = line,
                    Confidence = 0.82,
                    CharacterCount = line.Length
                })
                .ToList()
        };

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_responseFactory(request));
        }
    }
}
