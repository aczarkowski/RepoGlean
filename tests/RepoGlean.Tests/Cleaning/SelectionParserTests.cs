using RepoGlean.Cleaning;

namespace RepoGlean.Tests.Cleaning;

public sealed class SelectionParserTests
{
    [Theory]
    [InlineData("1", new[] { 0 })]
    [InlineData("1,3-4", new[] { 0, 2, 3 })]
    [InlineData("4-2", new[] { 1, 2, 3 })]
    [InlineData("all", new[] { 0, 1, 2, 3 })]
    public void Parse_accepts_one_based_numbers_ranges_and_all(string text, int[] expected)
    {
        var result = SelectionParser.Parse(text, 4, [1]);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(expected, result.SelectedIndices);
    }

    [Fact]
    public void Parse_uses_the_callers_default_selection_for_empty_input()
    {
        var repositories = SelectionParser.Parse(string.Empty, 3, [0, 1, 2]);
        var artifacts = SelectionParser.Parse("   ", 4, [0, 2]);

        Assert.Equal([0, 1, 2], repositories.SelectedIndices);
        Assert.Equal([0, 2], artifacts.SelectedIndices);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("5")]
    [InlineData("1,,2")]
    [InlineData("everything")]
    public void Parse_rejects_invalid_or_out_of_range_input(string text)
    {
        var result = SelectionParser.Parse(text, 4, []);

        Assert.False(result.IsSuccess);
        Assert.Empty(result.SelectedIndices);
        Assert.NotEmpty(result.Error!);
    }
}
