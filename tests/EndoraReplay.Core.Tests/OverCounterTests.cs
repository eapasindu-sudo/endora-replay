using EndoraReplay.Core.Model;
using Xunit;

namespace EndoraReplay.Core.Tests;

public class OverCounterTests
{
    [Fact]
    public void Counts_balls_into_overs_like_a_scoreboard()
    {
        var c = new OverCounter();
        Assert.False(c.Started);
        for (var i = 0; i < 6; i++) c.AddBall();
        Assert.Equal("0.6", c.Label);
        c.AddBall();
        Assert.Equal("1.1", c.Label);
        Assert.True(c.Undo());
        Assert.Equal("0.6", c.Label);
    }

    [Fact]
    public void Can_be_set_and_rejects_impossible_counts()
    {
        var c = new OverCounter();
        Assert.True(c.TrySet("12.4"));
        Assert.Equal("12.4", c.Label);
        Assert.True(c.TrySet("20"));
        Assert.Equal("20.0", c.Label);
        Assert.False(c.TrySet("3.7"));
        Assert.False(c.TrySet("abc"));
        Assert.Equal("20.0", c.Label);
        Assert.True(c.Undo());
        Assert.Equal("12.4", c.Label);
    }

    [Fact]
    public void Keeps_the_count_across_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"endora-overs-{Guid.NewGuid():N}.json");
        try
        {
            new OverCounter(path).TrySet("7.3");
            Assert.Equal("7.3", new OverCounter(path).Label);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
