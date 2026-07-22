using DevCleaner.Cli;

namespace DevCleaner.Tests.Cli;

public sealed class ByteSizeParserTests
{
    [Theory]
    [InlineData("1", 1)]
    [InlineData("42B", 42)]
    [InlineData("1.5KB", 1500)]
    [InlineData("1.5MiB", 1_572_864)]
    [InlineData("2gib", 2_147_483_648)]
    public void TryParse_accepts_positive_decimal_and_binary_sizes(string input, long expected)
    {
        var parsed = ByteSizeParser.TryParse(input, out var bytes);

        Assert.True(parsed);
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1MB")]
    [InlineData("1.5B")]
    [InlineData("NaNMB")]
    [InlineData("InfinityGB")]
    [InlineData("999999999999999999999TB")]
    [InlineData("2PB")]
    public void TryParse_rejects_non_positive_non_finite_fractional_byte_overflow_and_unknown_units(string input)
    {
        var parsed = ByteSizeParser.TryParse(input, out _);

        Assert.False(parsed);
    }
}
