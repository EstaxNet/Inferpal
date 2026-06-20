using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

public class ListCountBadgeTests
{
    [Theory]
    [InlineData(0, 0, "")]        // empty list → no badge
    [InlineData(3, 3, "3")]       // all enabled → plain total
    [InlineData(1, 1, "1")]
    [InlineData(3, 1, "1 / 3")]   // partial → enabled / total
    [InlineData(2, 0, "0 / 2")]   // none enabled but list non-empty
    public void Format_ProducesExpectedBadge(int total, int enabled, string expected)
    {
        Assert.Equal(expected, ListCountBadge.Format(total, enabled));
    }
}
