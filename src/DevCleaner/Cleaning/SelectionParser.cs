namespace DevCleaner.Cleaning;

public sealed record SelectionParseResult(
    bool IsSuccess,
    IReadOnlyList<int> SelectedIndices,
    string? Error);

public static class SelectionParser
{
    public static SelectionParseResult Parse(
        string? input,
        int itemCount,
        IReadOnlyList<int> defaultIndices)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(itemCount);
        ArgumentNullException.ThrowIfNull(defaultIndices);
        if (defaultIndices.Any(index => index < 0 || index >= itemCount))
        {
            throw new ArgumentOutOfRangeException(nameof(defaultIndices), "Default selections must identify available items.");
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            return Success(defaultIndices);
        }

        var text = input.Trim();
        if (string.Equals(text, "all", StringComparison.OrdinalIgnoreCase))
        {
            return Success(Enumerable.Range(0, itemCount));
        }

        var selected = new SortedSet<int>();
        foreach (var token in text.Split(',', StringSplitOptions.None))
        {
            var part = token.Trim();
            if (part.Length == 0)
            {
                return Failure("Selection contains an empty item.");
            }

            var rangeSeparator = part.IndexOf('-');
            if (rangeSeparator < 0)
            {
                if (!TryReadIndex(part, itemCount, out var index)) return Failure($"Selection '{part}' is outside the available range 1-{itemCount}.");
                selected.Add(index);
                continue;
            }

            if (rangeSeparator == 0 || rangeSeparator == part.Length - 1 || part.IndexOf('-', rangeSeparator + 1) >= 0 ||
                !TryReadIndex(part[..rangeSeparator].Trim(), itemCount, out var first) ||
                !TryReadIndex(part[(rangeSeparator + 1)..].Trim(), itemCount, out var last))
            {
                return Failure($"Selection range '{part}' is invalid or outside 1-{itemCount}.");
            }

            var lower = Math.Min(first, last);
            var upper = Math.Max(first, last);
            for (var index = lower; index <= upper; index++) selected.Add(index);
        }

        return Success(selected);
    }

    private static bool TryReadIndex(string value, int itemCount, out int index)
    {
        if (int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var oneBased) &&
            oneBased >= 1 &&
            oneBased <= itemCount)
        {
            index = oneBased - 1;
            return true;
        }

        index = -1;
        return false;
    }

    private static SelectionParseResult Success(IEnumerable<int> indices) =>
        new(true, Array.AsReadOnly(indices.Distinct().Order().ToArray()), null);

    private static SelectionParseResult Failure(string error) => new(false, [], error);
}
