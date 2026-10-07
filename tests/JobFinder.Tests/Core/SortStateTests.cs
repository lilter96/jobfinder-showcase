using JobFinder.App;

namespace JobFinder.Tests.Core;

public sealed class SortStateTests
{
    private static readonly SortState Fallback = new("seen", true);

    [Theory]
    [InlineData("score.desc", "score", true)]
    [InlineData("salary.asc", "salary", false)]
    [InlineData("title", "title", false)]
    public void Parses_the_query_value(string value, string key, bool desc) => Assert.Equal(new SortState(key, desc), SortState.Parse(value, Fallback));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Empty_value_keeps_the_default(string? value) => Assert.Equal(Fallback, SortState.Parse(value, Fallback));

    [Fact]
    public void Round_trips() => Assert.Equal(new SortState("score", true), SortState.Parse(new SortState("score", true).ToString(), Fallback));
}
