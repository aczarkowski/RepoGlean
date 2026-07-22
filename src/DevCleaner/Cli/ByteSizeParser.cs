using System.Globalization;
using System.Text.RegularExpressions;

namespace DevCleaner.Cli;

public static partial class ByteSizeParser
{
    private static readonly IReadOnlyDictionary<string, decimal> UnitMultipliers = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
    {
        [""] = 1,
        ["B"] = 1,
        ["KB"] = 1_000,
        ["MB"] = 1_000_000,
        ["GB"] = 1_000_000_000,
        ["TB"] = 1_000_000_000_000,
        ["KiB"] = 1_024,
        ["MiB"] = 1_048_576,
        ["GiB"] = 1_073_741_824,
        ["TiB"] = 1_099_511_627_776,
    };

    public static bool TryParse(string input, out long bytes)
    {
        bytes = default;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var match = SizePattern().Match(input.Trim());
        if (!match.Success ||
            !decimal.TryParse(match.Groups["value"].Value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ||
            value <= 0 ||
            !UnitMultipliers.TryGetValue(match.Groups["unit"].Value, out var multiplier))
        {
            return false;
        }

        decimal scaled;
        try
        {
            scaled = value * multiplier;
        }
        catch (OverflowException)
        {
            return false;
        }

        if (scaled != decimal.Truncate(scaled) || scaled > long.MaxValue)
        {
            return false;
        }

        bytes = (long)scaled;
        return true;
    }

    [GeneratedRegex("^(?<value>\\+?(?:\\d+(?:\\.\\d+)?|\\.\\d+))\\s*(?<unit>B|KB|MB|GB|TB|KiB|MiB|GiB|TiB)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizePattern();
}
