using ReceiptReader.Api.Models;

namespace ReceiptReader.Api.Services;

public sealed class DeterministicReceiptRepairService : IReceiptRepairService
{
    private readonly IReceiptConsistencyValidator _consistencyValidator;

    public DeterministicReceiptRepairService(IReceiptConsistencyValidator consistencyValidator)
    {
        _consistencyValidator = consistencyValidator;
    }

    public ReceiptRepairResult Repair(ReceiptSummary summary, IReadOnlyList<ReceiptItem> items)
    {
        var baselineItems = items.Select(CloneItem).ToList();
        var baselineConsistency = _consistencyValidator.Validate(summary, baselineItems);

        var originalDifference = Math.Abs(baselineConsistency.DifferenceToDeclaredTotal ?? decimal.MaxValue);
        if (baselineConsistency.ConsistencyStatus is ReceiptConsistencyStatus.Exact or ReceiptConsistencyStatus.ToleranceMatch)
        {
            return new ReceiptRepairResult
            {
                Summary = summary,
                Items = baselineItems,
                WasApplied = false,
                Details = "Deterministic repair skipped because the parser result already balances."
            };
        }

        var candidates = baselineItems
            .Select((item, index) => new RepairCandidate(index, BuildVariants(item)))
            .Where(entry => entry.Variants.Count > 1)
            .Take(8)
            .ToList();

        if (candidates.Count == 0)
        {
            return new ReceiptRepairResult
            {
                Summary = summary,
                Items = baselineItems,
                WasApplied = false,
                Details = "Deterministic repair found no safe local corrections."
            };
        }

        var bestItems = baselineItems;
        var bestConsistency = baselineConsistency;
        var bestDifference = originalDifference;
        var bestModificationCount = int.MaxValue;

        Search(candidates, 0, baselineItems.ToArray(), 0);

        return new ReceiptRepairResult
        {
            Summary = summary,
            Items = bestItems,
            WasApplied = !ReferenceEquals(bestItems, baselineItems) && bestDifference < originalDifference,
            Details = bestDifference < originalDifference
                ? $"Deterministic repair improved receipt balance from {originalDifference:0.00} to {bestDifference:0.00}."
                : "Deterministic repair explored safe variants but kept the parser result."
        };

        void Search(IReadOnlyList<RepairCandidate> entries, int position, ReceiptItem[] currentItems, int modificationCount)
        {
            if (position == entries.Count)
            {
                var evaluated = currentItems.Select(CloneItem).ToList();
                var consistency = _consistencyValidator.Validate(summary, evaluated);
                var difference = Math.Abs(consistency.DifferenceToDeclaredTotal ?? decimal.MaxValue);
                if (IsBetter(consistency, difference, modificationCount))
                {
                    bestItems = evaluated;
                    bestConsistency = consistency;
                    bestDifference = difference;
                    bestModificationCount = modificationCount;
                }

                return;
            }

            var entry = entries[position];
            foreach (var variant in entry.Variants)
            {
                var nextItems = (ReceiptItem[])currentItems.Clone();
                nextItems[entry.Index] = CloneItem(variant);
                Search(entries, position + 1, nextItems, modificationCount + (variant.RepairReason is null ? 0 : 1));
            }
        }

        bool IsBetter(ReceiptConsistencyResult consistency, decimal difference, int modificationCount)
        {
            var bestScore = _consistencyValidator.GetScore(bestConsistency.ConsistencyStatus);
            var currentScore = _consistencyValidator.GetScore(consistency.ConsistencyStatus);
            if (currentScore > bestScore)
            {
                return true;
            }

            if (currentScore < bestScore)
            {
                return false;
            }

            if (difference < bestDifference - 0.001m)
            {
                return true;
            }

            return Math.Abs(difference - bestDifference) <= 0.001m && modificationCount < bestModificationCount;
        }
    }

    private static List<ReceiptItem> BuildVariants(ReceiptItem item)
    {
        var variants = new List<ReceiptItem> { CloneItem(item) };
        if (item.ExcludedByBalancer || item.TotalPrice is null)
        {
            return variants;
        }

        if (item.Quantity.HasValue && item.UnitPrice.HasValue)
        {
            var recomputedTotal = decimal.Round(item.Quantity.Value * item.UnitPrice.Value, 2);
            if (recomputedTotal > 0 && recomputedTotal != item.TotalPrice.Value)
            {
                var adjusted = CloneItem(item);
                adjusted.TotalPrice = recomputedTotal;
                adjusted.CandidateKind = ReceiptItemCandidateKind.Repaired;
                adjusted.RepairReason = "Total price realigned from quantity and unit price.";
                variants.Add(adjusted);
            }
        }

        if (item.Quantity.HasValue && item.TotalPrice.HasValue && item.Quantity.Value > 0)
        {
            var derivedUnitPrice = decimal.Round(item.TotalPrice.Value / item.Quantity.Value, 2);
            if (!item.UnitPrice.HasValue || derivedUnitPrice != item.UnitPrice.Value)
            {
                var adjusted = CloneItem(item);
                adjusted.UnitPrice = derivedUnitPrice;
                adjusted.CandidateKind = ReceiptItemCandidateKind.Repaired;
                adjusted.RepairReason = "Unit price derived from quantity and trusted total.";
                variants.Add(adjusted);
            }
        }

        if (item.UnitPrice.HasValue && item.TotalPrice.HasValue && item.UnitPrice.Value > 0 && item.TotalPrice.Value > 0)
        {
            var swapped = CloneItem(item);
            swapped.UnitPrice = item.TotalPrice;
            swapped.TotalPrice = item.UnitPrice;
            swapped.CandidateKind = ReceiptItemCandidateKind.Repaired;
            swapped.RepairReason = "Unit and total prices were swapped for OCR repair evaluation.";
            variants.Add(swapped);
        }

        if (CanExclude(item))
        {
            var excluded = CloneItem(item);
            excluded.ExcludedByBalancer = true;
            excluded.CandidateKind = ReceiptItemCandidateKind.Excluded;
            excluded.RepairReason = "Low-confidence line excluded during deterministic balance repair.";
            excluded.ParseWarnings = excluded.ParseWarnings
                .Append("Excluded from balance calculation during deterministic repair.")
                .Distinct()
                .ToArray();
            variants.Add(excluded);
        }

        return variants
            .GroupBy(variant => $"{variant.Quantity}:{variant.UnitPrice}:{variant.TotalPrice}:{variant.ExcludedByBalancer}:{variant.RepairReason}")
            .Select(group => group.First())
            .ToList();
    }

    private static bool CanExclude(ReceiptItem item)
    {
        if (item.TotalPrice is null)
        {
            return false;
        }

        if (item.CandidateKind is ReceiptItemCandidateKind.MultiLine or ReceiptItemCandidateKind.Weighted)
        {
            return false;
        }

        return item.ParseWarnings.Count > 0
            || item.ArithmeticConfidence < 0.62
            || item.Confidence < 0.5;
    }

    private static ReceiptItem CloneItem(ReceiptItem item) =>
        new()
        {
            Name = item.Name,
            Quantity = item.Quantity,
            UnitPrice = item.UnitPrice,
            TotalPrice = item.ExcludedByBalancer ? null : item.TotalPrice,
            Discount = item.Discount,
            VatRate = item.VatRate,
            Confidence = item.Confidence,
            ArithmeticConfidence = item.ArithmeticConfidence,
            CandidateKind = item.CandidateKind,
            Section = item.Section,
            VatCode = item.VatCode,
            SourceLine = item.SourceLine,
            SourceLines = item.SourceLines.ToArray(),
            EvidenceLines = item.EvidenceLines.ToArray(),
            RecognitionHints = item.RecognitionHints.ToArray(),
            WasReconstructedFromMultipleLines = item.WasReconstructedFromMultipleLines,
            WasAiCorrected = item.WasAiCorrected,
            ExcludedByBalancer = item.ExcludedByBalancer,
            RepairReason = item.RepairReason,
            ParseWarnings = item.ParseWarnings.ToArray()
        };

    private sealed record RepairCandidate(int Index, List<ReceiptItem> Variants);
}
