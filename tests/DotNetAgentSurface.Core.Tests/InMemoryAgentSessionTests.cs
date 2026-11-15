namespace DotNetAgentSurface.Core.Tests;

public sealed class InMemoryAgentSessionTests
{
    [Fact]
    public void Values_round_trip_through_json_and_can_be_removed()
    {
        var session = new InMemoryAgentSession("s1");

        session.Set("targets", new Dictionary<string, string> { ["main"] = "C:\\app.dll" });
        session.Set<string?>("none", null);

        Assert.Equal("s1", session.Id);
        Assert.Equal("C:\\app.dll", session.Get<Dictionary<string, string>>("targets")!["main"]);
        Assert.Null(session.Get<string>("none"));
        Assert.Null(session.Get<string>("missing"));

        session.Remove("targets");
        Assert.Null(session.Get<Dictionary<string, string>>("targets"));
    }

    [Fact]
    public async Task Concurrent_writers_do_not_corrupt_the_session()
    {
        var session = new InMemoryAgentSession();

        await Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(() =>
        {
            for (var round = 0; round < 200; round++)
            {
                session.Set($"key-{index}", round);
                session.Remove($"other-{index}");
            }
        })));

        Assert.All(Enumerable.Range(0, 16), index => Assert.Equal(199, session.Get<int>($"key-{index}")));
    }
}
