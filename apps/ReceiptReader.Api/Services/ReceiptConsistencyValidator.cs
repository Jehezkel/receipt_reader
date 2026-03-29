using ReceiptReader.Api.Models;

namespace ReceiptReader.Api.Services;

public sealed class ReceiptConsistencyValidator : IReceiptConsistencyValidator
{
    private const decimal Tolerance = 0.05m;

    public ReceiptConsistencyResult Validate(ReceiptSummary summary, IReadOnlyList<ReceiptItem> items)
    {
        var declaredTotal = summary.TotalGross;
        var calculatedTotal = CalculateItemsTotal(items, includeDiscounts: false);
        var calculatedAfterDiscounts = CalculateItemsTotal(items, includeDiscounts: true);
        var bestCalculated = calculatedAfterDiscounts ?? calculatedTotal;
        decimal? difference = declaredTotal.HasValue && bestCalculated.HasValue
            ? decimal.Round(declaredTotal.Value - bestCalculated.Value, 2)
            : null;

        var status = ResolveStatus(declaredTotal, bestCalculated, difference);
        var needsReview = status is ReceiptConsistencyStatus.Mismatch or ReceiptConsistencyStatus.InsufficientData
            || items.Any(item => item.ParseWarnings.Count > 0 || item.Confidence < 0.6);

        var penalizedItems = status is ReceiptConsistencyStatus.Mismatch or ReceiptConsistencyStatus.InsufficientData;
        if (penalizedItems)
        {
            foreach (var item in items)
            {
                if (item.TotalPrice is null)
                {
                    continue;
                }

                item.Confidence = Math.Max(0.2, item.Confidence - 0.12);
            }
        }

        return new ReceiptConsistencyResult
        {
            DeclaredTotal = declaredTotal,
            CalculatedItemsTotal = calculatedTotal,
            CalculatedItemsTotalAfterDiscounts = calculatedAfterDiscounts,
            DifferenceToDeclaredTotal = difference,
            ConsistencyStatus = status,
            NeedsReview = needsReview
        };
    }

    public int GetScore(ReceiptConsistencyStatus status) => status switch
    {
        ReceiptConsistencyStatus.Exact => 3,
        ReceiptConsistencyStatus.ToleranceMatch => 2,
        ReceiptConsistencyStatus.InsufficientData => 1,
        _ => 0
    };

    private static decimal? CalculateItemsTotal(IReadOnlyList<ReceiptItem> items, bool includeDiscounts)
    {
        decimal total = 0m;
        var foundAny = false;

        foreach (var item in items)
        {
            var lineTotal = item.TotalPrice;
            if (!lineTotal.HasValue && item.Quantity.HasValue && item.UnitPrice.HasValue)
            {
                lineTotal = decimal.Round(item.Quantity.Value * item.UnitPrice.Value, 2);
                item.ParseWarnings = item.ParseWarnings
                    .Append("Total price inferred from quantity and unit price.")
                    .Distinct()
                    .ToArray();
            }

            if (!lineTotal.HasValue)
            {
                continue;
            }

            foundAny = true;
            total += lineTotal.Value;

            if (includeDiscounts && item.Discount.HasValue)
            {
                total -= item.Discount.Value;
            }
        }

        return foundAny ? decimal.Round(total, 2) : null;
    }

    private static ReceiptConsistencyStatus ResolveStatus(decimal? declaredTotal, decimal? calculatedTotal, decimal? difference)
    {
        if (!declaredTotal.HasValue || !calculatedTotal.HasValue || !difference.HasValue)
        {
            return ReceiptConsistencyStatus.InsufficientData;
        }

        var absoluteDifference = Math.Abs(difference.Value);
        if (absoluteDifference == 0m)
        {
            return ReceiptConsistencyStatus.Exact;
        }

        if (absoluteDifference <= Tolerance)
        {
            return ReceiptConsistencyStatus.ToleranceMatch;
        }

        return ReceiptConsistencyStatus.Mismatch;
    }
}
