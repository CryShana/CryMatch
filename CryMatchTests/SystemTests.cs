using CryMatch.Core;
using CryMatch.Core.Enums;
using CryMatch.Core.Interfaces;

using CryMatch.Director;
using CryMatch.Matchmaker;

using CryMatchGrpc;

using CryMatchTests.Fixtures;

using System.Collections.Concurrent;
using System.Diagnostics;

using Xunit.Abstractions;

namespace CryMatchTests;

[Collection("System")]
public class SystemTests : IClassFixture<StateFixture>
{
    public IState Memory { get; }
    public IState Redis { get; }

    readonly ITestOutputHelper _output;

    public SystemTests(StateFixture state_fixture, ITestOutputHelper output)
    {
        Memory = state_fixture.Memory;
        Redis = state_fixture.Redis;
        _output = output;
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
            MatchmakerUpdateDelay = 0.1,
            DirectorUpdateDelay = 0.2,
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
    public async Task Matching1v1(string state_name)
    {
        var state = GetState(state_name);
        var (config, plugins) = GetCoreServices();

        await ResetState(state);

        using var matchmaker = new MatchmakerManager(config, plugins, state);
        using var director = new DirectorManager(config, plugins, state);

        var ticket = new Ticket();   
        director.TicketSubmit(ticket);
        await Task.Delay(200);

        var submitted_tickets = await state.GetSetValues(IState.SET_TICKETS_SUBMITTED);
        Assert.Equal([ticket.GlobalId], submitted_tickets);

        var ticket_count = 1002;
        var expected_matches = ticket_count / 2;

        var sw = Stopwatch.StartNew();
        sw.Restart();
        for (int i = 0; i < ticket_count - 1; i++)
        {
            director.TicketSubmit(ticket);
        }
        sw.Stop();
        Assert.True(sw.Elapsed.TotalSeconds < 1f, "Time to submit tickets was " + sw.Elapsed.TotalSeconds + " seconds");
        await Task.Delay(300); // if we wait for too long, matches are then taken from set_submitted and matched

        Assert.Equal(1002, director.TicketsSubmitted);

        submitted_tickets = await state.GetSetValues(IState.SET_TICKETS_SUBMITTED);
        Assert.Equal(1002, submitted_tickets!.Length);

        // gather time is 2f
        await Task.Delay(TimeSpan.FromSeconds(3f));

        // we are not waiting for it yet (so its 0)
        Assert.Equal(0, director.MatchesReader.Count);

        var matches_in_state = await state.StreamRead(IState.STREAM_MATCHES);
        Assert.Equal(expected_matches, matches_in_state!.Count);

        var csc = new CancellationTokenSource();
        csc.CancelAfter(1000);

        // the moment we register as a reader, director starts reading matches from state
        var matches = new List<TicketMatch>();
        await director.ReadIncomingMatches((m) =>
        {
            matches.Add(m);
            return Task.CompletedTask;
        }, csc.Token);

        Assert.Equal(expected_matches, matches.Count);
        Assert.Equal(0, director.MatchesReader.Count);

        submitted_tickets = await state.GetSetValues(IState.SET_TICKETS_SUBMITTED);
        Assert.True(submitted_tickets == null || submitted_tickets.Length == 0);

        await Task.Delay(TimeSpan.FromSeconds(config.DirectorUpdateDelay * 2));

        // they have been consumed
        matches_in_state = await state.StreamRead(IState.STREAM_MATCHES);
        Assert.True(matches_in_state == null || matches_in_state.Count == 0);

        await ResetState(state);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("redis")]
    public async Task Matching5v5(string state_name)
    {
        var state = GetState(state_name);
        var (config, plugins) = GetCoreServices();

        await ResetState(state);

        using var matchmaker = new MatchmakerManager(config, plugins, state);
        using var director = new DirectorManager(config, plugins, state);

        var ticket = new Ticket();
        director.TicketSubmit(ticket);
        await Task.Delay(200);

        var submitted_tickets = await state.GetSetValues(IState.SET_TICKETS_SUBMITTED);
        Assert.Equal([ticket.GlobalId], submitted_tickets);
        
        var match_size = 10;
        var ticket_count = 1002;
        var expected_matches = ticket_count / match_size;
        var left_over_tickets = ticket_count - (expected_matches * match_size);

        await state.SetString(IState.POOL_MATCH_SIZE_PREFIX + "", match_size.ToString());

        var sw = Stopwatch.StartNew();
        sw.Restart();
        for (int i = 0; i < ticket_count - 1; i++)
        {
            director.TicketSubmit(ticket);
        }
        sw.Stop();
        Assert.True(sw.Elapsed.TotalSeconds < 1f, "Time to submit tickets was " + sw.Elapsed.TotalSeconds + " seconds");
        await Task.Delay(200);

        submitted_tickets = await state.GetSetValues(IState.SET_TICKETS_SUBMITTED);
        Assert.Equal(1002, submitted_tickets!.Length);

        // gather time is 2f
        await Task.Delay(TimeSpan.FromSeconds(3f));

        // we are not waiting for it yet (so its 0)
        Assert.Equal(0, director.MatchesReader.Count);

        var matches_in_state = await state.StreamRead(IState.STREAM_MATCHES);
        Assert.Equal(expected_matches, matches_in_state!.Count);

        var csc = new CancellationTokenSource();
        csc.CancelAfter(1000);

        // the moment we register as a reader, director starts reading matches from state
        var matches = new List<TicketMatch>();
        await director.ReadIncomingMatches((m) =>
        {
            matches.Add(m);
            return Task.CompletedTask;
        }, csc.Token);

        Assert.Equal(expected_matches, matches.Count);
        Assert.Equal(0, director.MatchesReader.Count);

        submitted_tickets = await state.GetSetValues(IState.SET_TICKETS_SUBMITTED);
        Assert.Equal(left_over_tickets, submitted_tickets!.Length);

        await Task.Delay(TimeSpan.FromSeconds(config.DirectorUpdateDelay * 2));

        // they need to have been consumed
        matches_in_state = await state.StreamRead(IState.STREAM_MATCHES);
        Assert.True(matches_in_state == null || matches_in_state.Count == 0);

        await ResetState(state);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("redis")]
    public async Task MatchingExpiry(string state_name)
    {
        var state = GetState(state_name);
        var (config, plugins) = GetCoreServices();

        await ResetState(state);

        using var matchmaker = new MatchmakerManager(config, plugins, state);
        using var director = new DirectorManager(config, plugins, state);

        var ticket = new Ticket();
        ticket.MaxAgeSeconds = 2;
        director.TicketSubmit(ticket);
        await Task.Delay(200);

        var submitted_tickets = await state.GetSetValues(IState.SET_TICKETS_SUBMITTED);
        Assert.Equal([ticket.GlobalId], submitted_tickets);

        await Task.Delay(TimeSpan.FromSeconds(1));
        director.TicketSubmit(ticket);
        await Task.Delay(TimeSpan.FromSeconds(config.DirectorUpdateDelay * 3));

        // is in gathering
        Assert.Single(director.OnlineMatchmakers);
        Assert.True(director.OnlineMatchmakers.First().Value.Pools![0].gathering);
        Assert.Equal(2, director.OnlineMatchmakers.First().Value.Pools![0].in_queue);

        // wait for gathering to finish (plus some time for cleaner to clean consumed tickets)
        await Task.Delay(TimeSpan.FromSeconds(2 + 2));

        // both tickets should have been expired
        submitted_tickets = await state.GetSetValues(IState.SET_TICKETS_SUBMITTED);
        Assert.True(submitted_tickets == null || submitted_tickets.Length == 0);

        await ResetState(state);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("redis")]
    public async Task SubmitTest(string state_name)
    {
        var state = GetState(state_name);
        var (config, plugins) = GetCoreServices();

        await ResetState(state);

        using var matchmaker = new MatchmakerManager(config, plugins, state);
        using var director = new DirectorManager(config, plugins, state);

        // wait for matchmaker to register and director to warm up (for proper emergency loop count)
        await Task.Delay(TimeSpan.FromSeconds(config.DirectorUpdateDelay * 10));

        const int TICKETS = 20_000;

        var sw = Stopwatch.StartNew();
        var partitioner = Partitioner.Create(0, TICKETS);
        Parallel.ForEach(partitioner, range =>
        {
            var d = director;
            for (int i = range.Item1; i < range.Item2; i++)
            {
                d.TicketSubmit(new Ticket());
            }
        });
        sw.Stop();
        Assert.Equal(TICKETS, director.TicketsReceived);
        await Task.Delay(400);

        _output.WriteLine($"Submitted {TICKETS} tickets in {sw.Elapsed.TotalSeconds} seconds (Emergency loops: {director.AvailableEmergencyLoops})");
        Assert.True(sw.Elapsed.TotalSeconds < 3, $"{TICKETS} tickets too long to be submitted at once!");

        var submitted_tickets = await state.GetSetValues(IState.SET_TICKETS_SUBMITTED);
        Assert.True(TICKETS - submitted_tickets!.Length < 1000); // within tolerance

        var unassigned_tickets = await state.StreamRead(IState.STREAM_TICKETS_UNASSIGNED);
        var assigned_tickets = await state.StreamRead(IState.STREAM_TICKETS_ASSIGNED_PREFIX + matchmaker.Id);

        // some tickets could be in-between (mid assigning), so we allow up to BATCH_SIZE of inaccuracy
        var total_count = (unassigned_tickets?.Count ?? 0) + (assigned_tickets?.Count ?? 0);
        Assert.True(TICKETS - total_count < 1000);

        // wait for director to assign all tickets to matchmaker

        // director will need a few cycles to assign all tickets to matchmakers
        // (memory requires just 2-times director update delay, local Redis is a bit slower, had to up it to 8)
        await Task.Delay(TimeSpan.FromSeconds(config.DirectorUpdateDelay * 8));
        
        Assert.Equal(TICKETS, director.TicketsReceived);
        Assert.Equal(TICKETS, director.TicketsSubmitted);
        Assert.Equal(TICKETS, director.TicketsAssigned);
        Assert.Equal(TICKETS, matchmaker.ProcessingTickets);
        
        Assert.Equal(1, matchmaker.Pools.Count);
        Assert.Equal(1, director.OnlineMatchmakers.Count);

        var pools = director.OnlineMatchmakers.First().Value!.Pools!;
        Assert.Single(pools);
        Assert.Equal(TICKETS, director.OnlineMatchmakers.First().Value!.ProcessingTickets);
        Assert.True(pools[0].gathering);
        Assert.Equal(TICKETS, pools[0].in_queue);

        await ResetState(state);
    }
}