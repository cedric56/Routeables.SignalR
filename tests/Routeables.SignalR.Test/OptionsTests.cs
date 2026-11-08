using Routeables.SignalR;

namespace Routeables.Tests.Remote;

/// <summary>The options are checked when the hub is registered: the application does not start with limits that make no sense.</summary>
public class OptionsTests
{
    [Fact]
    public void The_default_options_are_valid()
        => Assert.Empty(new RouteablesOptions().Problems());

    [Fact]
    public void Limits_that_are_not_positive_or_contradict_each_other_are_refused_at_registration()
    {
        var options = new RouteablesOptions
        {
            MaxDepth = 0,
            MaxPendingMessages = -1,
            MaxCollectionItems = 500,
            MaxStateNodes = 100,
            ReconnectGracePeriod = TimeSpan.FromSeconds(-1),
            CommandGrace = Timeout.InfiniteTimeSpan,
            Exposure = (RouteableExposure)7,
        };
        var problems = options.Problems();
        Assert.Contains(problems, p => p.StartsWith("MaxDepth", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("MaxPendingMessages", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("MaxCollectionItems", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("ReconnectGracePeriod", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("CommandGrace", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Exposure", StringComparison.Ordinal));
        Assert.Equal(6, problems.Count);

        var refused = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddRouteablesSignalR(options: o => o.MaxDepth = 0));
        Assert.Contains("MaxDepth", refused.Message);

        // Zero is a choice for these: no target besides the page, an answer that never waits, a session that does not wait for its client.
        Assert.Empty(new RouteablesOptions { MaxTargets = 0, CommandGrace = TimeSpan.Zero, ReconnectGracePeriod = TimeSpan.Zero }.Problems());
    }
}
