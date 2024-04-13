using CryMatch.Core;
using CryMatch.Core.Enums;
using CryMatch.Core.Interfaces;

using CryMatch.Director;
using CryMatch.Matchmaker;

using CryMatchGrpc;

using CryMatchTests.Fixtures;

namespace CryMatchTests;

[Collection("System")]
public class AssignmentTests : IClassFixture<StateFixture>
{
    public IState Memory { get; }
    public IState Redis { get; }

    public AssignmentTests(StateFixture state_fixture)
    {
        Memory = state_fixture.Memory;
        Redis = state_fixture.Redis;
    }

    IState GetState(string name) => name switch
    {
        "memory" => Memory,
        "redis" => Redis,
        _ => throw new Exception("Invalid state name")
    };

    (Configuration config, CryMatch.Matchmaker.Plugins.PluginLoader) GetCoreServices()
    {
        var plugins = new CryMatch.Matchmaker.Plugins.PluginLoader("plugins");
        var config = new Configuration()
        {
            Mode = WorkMode.Standalone.ToString(),
            MatchmakerMinGatherTime = 2,
            MatchmakerThreads = 2,
            UseRedis = false
        };

        return (config, plugins);
    }

    async Task ResetState(IState state)
    {
        // clear everything
        var matchmakers = await state.GetSetValues(IState.SET_MATCHMAKERS);
        if (matchmakers != null)
        {
            foreach (var old_mm in matchmakers)
            {
                await state.StreamDelete(IState.STREAM_TICKETS_ASSIGNED_PREFIX + old_mm);
                await state.SetRemove(IState.SET_MATCHMAKERS, old_mm!);
            }
        }

        await state.KeyDelete(IState.SET_MATCHMAKERS);
        await state.SetString(IState.DIRECTOR_ACTIVE_KEY, null);
        await state.StreamDelete(IState.STREAM_TICKETS_UNASSIGNED);
        await state.StreamDelete(IState.STREAM_MATCHES);
        await state.KeyDelete(IState.SET_TICKETS_SUBMITTED);
        await state.KeyDelete(IState.POOL_MATCH_SIZE_PREFIX);
        await state.KeyDelete(IState.STREAM_TICKETS_CONSUMED);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("redis")]
    public async Task Assignment(string state_name)
    {
        var state = GetState(state_name);
        var (config, plugins) = GetCoreServices();

        await ResetState(state);

        using var matchmaker = new MatchmakerManager(config, plugins, state);
        using var director = new DirectorManager(config, plugins, state);

        Assert.Equal(0, matchmaker.ProcessingTickets);
        Assert.Equal(0, matchmaker.ReceivedTickets);
        Assert.Equal(0, matchmaker.Pools.Count);
        Assert.Equal(0, director.ActiveReaders);
        Assert.Equal(0, director.OnlineMatchmakers.Count);
        Assert.Equal(0, director.TicketsSubmitted);
        Assert.Equal(0, director.TicketsAssigned);

        await Task.Delay(TimeSpan.FromSeconds(config.DirectorUpdateDelay * 2));

        Assert.Equal(0, matchmaker.ProcessingTickets);
        Assert.Equal(0, matchmaker.ReceivedTickets);
        Assert.Equal(0, matchmaker.Pools.Count);
        Assert.Equal(0, director.ActiveReaders);
        Assert.Equal(1, director.OnlineMatchmakers.Count);
        Assert.Equal(0, director.TicketsSubmitted);
        Assert.Equal(0, director.TicketsAssigned);

        var mm_status = director.OnlineMatchmakers.First().Value;
        Assert.Equal(0, mm_status.ProcessingTickets);
        Assert.True(mm_status.Pools == null || mm_status.Pools.Count == 0);

        // 1ST TICKET SUBMIT (will be queued and pool will enter GATHER state) (empty pool)
        var req = director.TicketSubmit(new Ticket());
        Assert.Equal(TicketStatus.Ok, req);

        // NOTE: delay has to be slightly longer than director delay
        // submitted ticket is first added to UNASSIGNED pool, and then in next update loop
        // it is assigned to a matchmaker. So it will take a bit more than a single director delay
        // (matchmaker also has to fetch it first before it updates the ProcessingTickets)
        await Task.Delay(TimeSpan.FromSeconds(config.DirectorUpdateDelay * 2));

        Assert.Equal(1, matchmaker.ProcessingTickets);
        Assert.Equal(1, matchmaker.ReceivedTickets);
        Assert.Equal(1, matchmaker.Pools.Count);
        Assert.Equal(0, director.ActiveReaders);
        Assert.Equal(1, director.OnlineMatchmakers.Count);
        Assert.Equal(1, director.TicketsSubmitted);
        Assert.Equal(1, director.TicketsAssigned);

        // wait a bit longer still to get the latest mm status
        await Task.Delay(TimeSpan.FromSeconds(config.DirectorUpdateDelay * 2));
        
        mm_status = director.OnlineMatchmakers.First().Value;
        Assert.Equal(1, mm_status.ProcessingTickets);
        Assert.NotNull(mm_status.Pools);
        Assert.Single(mm_status.Pools);
        Assert.False(mm_status.Pools[0].gathering); // not gathering yet, because at least 2 tickets are required to start it
        Assert.Equal("", mm_status.Pools[0].name);
        Assert.Equal(1, mm_status.Pools[0].in_queue);

        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.Equal(1, director.TicketsAssigned);

        // 2nd TICKET SUBMIT ('test_pool' pool)
        req = director.TicketSubmit(new Ticket() {  MatchmakingPoolId = "test_pool" });
        Assert.Equal(TicketStatus.Ok, req);

        // 3rd TICKET SUBMIT (empty pool) with requirements to avoid matching
        // we don't want tickets to match, so we add some requirements
        var ticket = new Ticket();
        ticket.AddRequirements().AddDiscreet(0, 1);

        req = director.TicketSubmit(ticket);
        Assert.Equal(TicketStatus.Ok, req);
        await Task.Delay(400);

        Assert.Equal(3, director.TicketsSubmitted); 

        // wait a bit to fetch latest mm status
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        mm_status = director.OnlineMatchmakers.First().Value;
        Assert.Equal(3, mm_status.ProcessingTickets);
        Assert.NotNull(mm_status.Pools);
        Assert.Equal(2, mm_status.Pools.Count);

        // 1st pool is now gathering
        Assert.True(mm_status.Pools[1].gathering);
        Assert.Equal("", mm_status.Pools[1].name);

        // 2 here, because tickets were not matched and were moved to priority queue
        Assert.Equal(2, mm_status.Pools[1].in_queue); 

        // 2nd pool is gathering
        Assert.Equal("test_pool", mm_status.Pools[0].name);
        Assert.Equal(1, mm_status.Pools[0].in_queue);
        Assert.False(mm_status.Pools[0].gathering); // 2nd pool can't enter gathering with just 1 ticket

        Assert.Equal(3, matchmaker.ProcessingTickets);
        Assert.Equal(3, matchmaker.ReceivedTickets);
        Assert.Equal(2, matchmaker.Pools.Count);
        Assert.Equal(0, director.ActiveReaders);
        Assert.Equal(1, director.OnlineMatchmakers.Count);
        Assert.Equal(3, director.TicketsSubmitted);
        Assert.Equal(3, director.TicketsAssigned);

        // wait for the remainder of gathering time
        await Task.Delay(TimeSpan.FromSeconds(2));

        mm_status = director.OnlineMatchmakers.First().Value;
        Assert.NotNull(mm_status.Pools);
        Assert.Equal(1, mm_status.ProcessingTickets); // 2 are now done, 1 is still in queue
        Assert.Equal(2, mm_status.Pools.Count);

        // gathering needs to be false now!
        Assert.Equal("", mm_status.Pools[1].name);
        Assert.False(mm_status.Pools[1].gathering);

        // still 2 here, because tickets were not matched and were moved to priority queue
        Assert.Equal(2, mm_status.Pools[1].in_queue);

        // here gathering is false now
        Assert.Equal("test_pool", mm_status.Pools[0].name);
        Assert.False(mm_status.Pools[0].gathering);

        // 1 here, because the single ticket did not get matched
        Assert.Equal(1, mm_status.Pools[0].in_queue);

        Assert.Equal(1, matchmaker.ProcessingTickets);
        Assert.Equal(3, matchmaker.ReceivedTickets);
        Assert.Equal(2, matchmaker.Pools.Count);
        Assert.Equal(0, director.ActiveReaders);
        Assert.Equal(1, director.OnlineMatchmakers.Count);
        Assert.Equal(3, director.TicketsSubmitted);
        Assert.Equal(3, director.TicketsAssigned);

        await ResetState(state);
    }
}