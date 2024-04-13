using CryMatch.Core;

namespace CryMatchTests;

public class StatusTests
{
    [Fact]
    public void StatusConversion1()
    {
        var now = DateTime.UtcNow;

        var status = new MatchmakerStatus()
        {
            ProcessingTickets = 1234,
            LocalTimeUtc = now.Subtract(TimeSpan.FromSeconds(2)),
        };

        var text1 = status.ToString();
        var text2 = MatchmakerStatus.FromStatus(status);
        Assert.Equal(text2, text1);

        var parsed = MatchmakerStatus.ToStatus(text2);
        Assert.NotNull(parsed);
        Assert.Equal(status.ProcessingTickets, parsed.ProcessingTickets);
        Assert.True(parsed.Pools == null || parsed.Pools.Count == 0);
        Assert.True(status.Pools == null || status.Pools.Count == 0);
        Assert.Equal(now.Subtract(TimeSpan.FromSeconds(2)), status.LocalTimeUtc);
    }

    [Fact]
    public void StatusConversion2()
    {
        var status = new MatchmakerStatus()
        {
            ProcessingTickets = 1234,
            Pools = new()
            {
                ("PoolName123", 1, false),
                ("MyPool2", 23, true),
                ("", 666, true)
            },
            LocalTimeUtc = DateTime.UtcNow
        };

        var text1 = status.ToString();
        var text2 = MatchmakerStatus.FromStatus(status);
        Assert.Equal(text2, text1);

        var parsed = MatchmakerStatus.ToStatus(text2);
        Assert.NotNull(parsed);
        Assert.Equal(status.ProcessingTickets, parsed.ProcessingTickets);
        
        Assert.NotNull(parsed.Pools);
        Assert.NotNull(status.Pools);
        Assert.Equal(status.Pools.Count, parsed.Pools.Count);
        Assert.Equal(status.Pools, parsed.Pools);
        Assert.Equal(DateTime.UtcNow, status.LocalTimeUtc, TimeSpan.FromSeconds(1));
    }
}