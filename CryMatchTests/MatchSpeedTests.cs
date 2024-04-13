using CryMatch.Core;
using CryMatch.Matchmaker;
using CryMatch.Matchmaker.Plugins;

using CryMatchGrpc;

using System.Buffers;
using System.Diagnostics;
using System.Collections.Concurrent;

using Xunit.Abstractions;
using System.Runtime.InteropServices;
using System.Collections.Frozen;

namespace CryMatchTests;

public class MatchSpeedTests
{
    static PluginLoader GetPlugins() => new PluginLoader("plugins");

    readonly ITestOutputHelper _output;

    public MatchSpeedTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// This generates a specified amount of random tickets that can all match with each other.
    /// Each requirement added will have 3 sub-requirements, which is a common scenario.
    /// </summary>
    static ReadOnlyMemory<TicketData> CreateMatchableTickets(int count, 
        int requirement_count, int candidates_size = 8)
    {
        var tickets = new TicketData[count];

        for (int i = 0; i < count; i++)
        {
            var t = new Ticket();
            t.GlobalId = Guid.NewGuid().ToString();
            
            t.AddStateValue(0).Add(3);
            t.AddStateValue(1).Add(3);
            t.AddStateValue(2).Add(150);

            for (int j = 0; j < requirement_count; j++)
            {
                t.AddRequirements().AddDiscreet(0, 1, 2, 3);
                t.AddRequirements().AddDiscreet(1, 3);
                t.AddRequirements().AddRange(2, 100, 300);
            }

            t.Affinities.Add(new MatchmakingAffinity
            {
                Value = 1000,
                MaxMargin = 500,
                PreferDisimilar = false,
                PriorityFactor = 1,
                SoftMargin = true
            });

            tickets[i] = new TicketData(t, t.State.Count, candidates_size);
        }

        return tickets.AsMemory();
    }

    [Fact]
    public void SpeedTestWorst_1K()
    {
        
        const int TICKET_COUNT = 1000;
        const int REQUIREMENT_GROUPS = 4;
        const int EXPECTED_MATCHES = TICKET_COUNT / 2;

        var tickets = CreateMatchableTickets(TICKET_COUNT, REQUIREMENT_GROUPS);
        var matches = new List<TicketMatch>(EXPECTED_MATCHES);

        var sw = Stopwatch.StartNew();

        // warmup
        sw.Restart();
        MatchmakerManager.MatchFunction(tickets, matches);
        sw.Stop();

        // both tickets match, as they have nothing to exclude each other
        Assert.Equal(EXPECTED_MATCHES, matches.Count);

        // for real
        matches.Clear();

        sw.Restart();
        MatchmakerManager.MatchFunction(tickets, matches);
        sw.Stop();

        _output.WriteLine($"Matched {TICKET_COUNT} tickets ({REQUIREMENT_GROUPS * 3} requirements on each) in {sw.Elapsed.TotalSeconds} seconds");

        Assert.True(sw.Elapsed.TotalSeconds < 0.2, $"It takes too much time to match {TICKET_COUNT} worst-case tickets");
        CheckMatchesAndGetRelativeDistanceBetweenMatchedTickets(tickets.Span, matches);
    }

    [Fact]
    public void SpeedTestNormal_1K()
    {
        
        const int TICKET_COUNT = 1000;
        const int EXPECTED_MATCHES = TICKET_COUNT / 2;

        var tickets = CreateMatchableTickets(TICKET_COUNT, 1);
        var matches = new List<TicketMatch>(EXPECTED_MATCHES);

        var sw = Stopwatch.StartNew();

        // warmup
        sw.Restart();
        MatchmakerManager.MatchFunction(tickets, matches);
        sw.Stop();

        // both tickets match, as they have nothing to exclude each other
        Assert.Equal(EXPECTED_MATCHES, matches.Count);

        // for real
        matches.Clear();

        sw.Restart();
        MatchmakerManager.MatchFunction(tickets, matches);
        sw.Stop();

        _output.WriteLine($"Matched {TICKET_COUNT} tickets (3 requirements on each) in {sw.Elapsed.TotalSeconds} seconds");

        Assert.True(sw.Elapsed.TotalSeconds < 0.2, $"It takes too much time to match {TICKET_COUNT} normal-case tickets");
        CheckMatchesAndGetRelativeDistanceBetweenMatchedTickets(tickets.Span, matches);
    }

    [Fact]
    public void SpeedTestNormal_100()
    {
        
        const int TICKET_COUNT = 100;
        const int EXPECTED_MATCHES = TICKET_COUNT / 2;

        var tickets = CreateMatchableTickets(TICKET_COUNT, 1);
        var matches = new List<TicketMatch>(EXPECTED_MATCHES);

        var sw = Stopwatch.StartNew();

        // warmup
        sw.Restart();
        MatchmakerManager.MatchFunction(tickets, matches);
        sw.Stop();

        // both tickets match, as they have nothing to exclude each other
        Assert.Equal(EXPECTED_MATCHES, matches.Count);

        // for real
        matches.Clear();

        sw.Restart();
        MatchmakerManager.MatchFunction(tickets, matches);
        sw.Stop();

        _output.WriteLine($"Matched {TICKET_COUNT} tickets (3 requirements on each) in {sw.Elapsed.TotalSeconds} seconds");

        Assert.True(sw.Elapsed.TotalSeconds < 0.1, $"It takes too much time to match {TICKET_COUNT} normal-case tickets");
        CheckMatchesAndGetRelativeDistanceBetweenMatchedTickets(tickets.Span, matches);
    }

    [Fact]
    public void SpeedTestConversion_1K()
    {
        const int TICKET_COUNT = 1000;
        const int REQUIREMENT_GROUPS = 4;

        var matchable = CreateMatchableTickets(TICKET_COUNT, REQUIREMENT_GROUPS);

        // warmup
        var sw = Stopwatch.StartNew();

        sw.Restart();
        Test(matchable);
        sw.Stop();

        sw.Restart();
        Test(matchable);
        sw.Stop();

        sw.Restart();
        Test(matchable);
        sw.Stop();

        // for real
        sw.Restart();
        Test(matchable);
        sw.Stop();
        var a = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        Test(matchable);
        sw.Stop();
        var b = sw.Elapsed.TotalMilliseconds;

        var elapsed = Math.Min(a, b);

        _output.WriteLine($"Converted {TICKET_COUNT} tickets ({REQUIREMENT_GROUPS * 3} requirements on each) in {elapsed}ms");

        // a bit bigger here to account for Debug
        Assert.True(elapsed < 4, $"It takes too much time to convert {TICKET_COUNT} tickets");

        static void Test(ReadOnlyMemory<TicketData> generated)
        {
            ConcurrentQueue<Ticket> pool = new();
            foreach (var m in generated.Span)
                pool.Enqueue(m.FullTicket);

            var count = pool.Count;

            var buffer1 = ArrayPool<Ticket>.Shared.Rent(count);
            var buffer2 = ArrayPool<TicketData>.Shared.Rent(count);

            var tickets_full = buffer1.AsSpan(0, count);
            var tickets_data = buffer2.AsSpan(0, count);

            try
            {
                int state_count = 0;
                int current_index = 0;
                while (pool.TryDequeue(out var ticket))
                {
                    if (ticket.State.Count > state_count)
                        state_count = ticket.State.Count;

                    tickets_full[current_index++] = ticket;

                    if (current_index >= count)
                        break;
                }

                for (int i = 0; i < count; i++)
                {
                    tickets_data[i] = new TicketData(tickets_full[i], state_count);
                }
            }
            finally
            {
                ArrayPool<Ticket>.Shared.Return(buffer1);
                ArrayPool<TicketData>.Shared.Return(buffer2);
            }
        }
    }

    [Fact]
    public void SpeedTestNormal_5K()
    {
        
        const int TICKET_COUNT = 5_000;
        const int EXPECTED_MATCHES = TICKET_COUNT / 2;

        var tickets = CreateMatchableTickets(TICKET_COUNT, 1);
        var matches = new List<TicketMatch>(EXPECTED_MATCHES);

        var sw = Stopwatch.StartNew();

        // warmup
        sw.Restart();
        MatchmakerManager.MatchFunction(tickets, matches);
        sw.Stop();

        // both tickets match, as they have nothing to exclude each other
        Assert.Equal(EXPECTED_MATCHES, matches.Count);

        // for real
        matches.Clear();

        sw.Restart();
        MatchmakerManager.MatchFunction(tickets, matches);
        sw.Stop();

        _output.WriteLine($"Matched {TICKET_COUNT} tickets (3 requirements on each) in {sw.Elapsed.TotalSeconds} seconds");

        // accounting for Debug
        Assert.True(sw.Elapsed.TotalSeconds < 4, $"It takes too much time to match {TICKET_COUNT} normal-case tickets");

        CheckMatchesAndGetRelativeDistanceBetweenMatchedTickets(tickets.Span, matches);
    }

    [Fact]
    public void SpeedTestNormal_5K_Size10()
    {

        const int TICKET_COUNT = 5_000;
        const int MATCH_SIZE = 10;
        const int EXPECTED_MATCHES = TICKET_COUNT / MATCH_SIZE;

        var tickets = CreateMatchableTickets(TICKET_COUNT, 1, MatchmakerManager.PreferredCandidatesSizeFor(MATCH_SIZE));
        var matches = new List<TicketMatch>(EXPECTED_MATCHES);

        var sw = Stopwatch.StartNew();

        // warmup
        sw.Restart();
        MatchmakerManager.MatchFunction(tickets, matches, match_size: MATCH_SIZE);
        sw.Stop();

        // both tickets match, as they have nothing to exclude each other
        Assert.Equal(EXPECTED_MATCHES, matches.Count);

        // for real
        matches.Clear();

        sw.Restart();
        MatchmakerManager.MatchFunction(tickets, matches, match_size: MATCH_SIZE);
        sw.Stop();

        _output.WriteLine($"Matched {TICKET_COUNT} tickets (3 requirements on each) in {sw.Elapsed.TotalSeconds} seconds (match size {MATCH_SIZE})");

        // accounting for Debug
        Assert.True(sw.Elapsed.TotalSeconds < 4, $"It takes too much time to match {TICKET_COUNT} normal-case tickets");

        CheckMatchesAndGetRelativeDistanceBetweenMatchedTickets(tickets.Span, matches);
    }

    static void CheckMatchesAndGetRelativeDistanceBetweenMatchedTickets(ReadOnlySpan<TicketData> tickets, List<TicketMatch> matches)
    {
        // we want to store locations of matched tickets
        var indexes = new Dictionary<string, int>();
        var matches_span = CollectionsMarshal.AsSpan(matches);
        for (int i = 0; i < tickets.Length; i++)
        {
            int contained = 0;
            var ticket = tickets[i];
            var gid = ticket.GlobalId;

            indexes[gid] = i;

            foreach (var m in matches_span)
            {
                if (m.MatchedTicketGlobalIds.Contains(gid)) 
                    contained++;

                if (contained > 1) 
                    Assert.Fail("Tickets is not contained exactly once in provided matches");
            }

            if (contained != 1) 
                Assert.Fail("Tickets is not contained exactly once in provided matches");
        }

        var frozen = indexes.ToFrozenDictionary();

        // also check that there is a match that contains tickets that are further away
        // to ensure that matches are being made across the entire pool of tickets
        var max_diff = 0;
        foreach (var m in matches_span)
        {
            var index0 = frozen[m.MatchedTicketGlobalIds[0]];

            foreach (var t1 in m.MatchedTicketGlobalIds)
            {
                var index1 = frozen[t1];

                foreach (var t2 in m.MatchedTicketGlobalIds)
                {
                    if (t1 == t2) continue;
                    var index2 = frozen[t1];

                    var diff2 = Math.Abs(index1 - index2);
                    if (diff2 > max_diff) max_diff = diff2;
                }

                var diff0 = Math.Abs(index1 - index0);
                if (diff0 > max_diff) max_diff = diff0;
            }    
        }

        var relative_difference = max_diff / (double)tickets.Length;
        Assert.True(relative_difference > 0.7);   
    }
}