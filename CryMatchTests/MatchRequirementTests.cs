using CryMatch.Core;
using CryMatch.Matchmaker;
using CryMatch.Matchmaker.Plugins;

using CryMatchGrpc;

namespace CryMatchTests;

public class MatchRequirementTests
{
    static PluginLoader GetPlugins() => new PluginLoader("plugins");
    static ReadOnlyMemory<TicketData> GetTickets(params Ticket[] tickets) 
        => tickets.Select(x => new TicketData(x, x.State.Count + 3)).ToArray().AsMemory();
    static ReadOnlyMemory<TicketData> GetTickets(int match_size, params Ticket[] tickets)
        => tickets.Select(x => new TicketData(x, x.State.Count + 3, candidates_size: MatchmakerManager.PreferredCandidatesSizeFor(match_size))).ToArray().AsMemory();

    static Ticket CreateTicket() => new Ticket() { GlobalId = Guid.NewGuid().ToString() };

    [Fact]
    public void RequirementTest1()
    { 
        var t1 = CreateTicket();
        var t2 = CreateTicket();

        var tickets = GetTickets(t1, t2);
        var matches = new List<TicketMatch>();
        Assert.True(MatchmakerManager.MatchFunction(tickets, matches));

        // both tickets match, as they have nothing to exclude each other
        Assert.Single(matches);
        
        Assert.True(
            matches[0].MatchedTicketGlobalIds.Contains(t1.GlobalId) || 
            matches[0].MatchedTicketGlobalIds.Contains(t2.GlobalId));
    }

    [Fact]
    public void RequirementTest2()
    { 
        var t1 = CreateTicket();
        t1.AddStateValue(0).Add(1);
        t1.AddRequirements().AddDiscreet(0, 1);

        var t2 = CreateTicket();

        var tickets = GetTickets(t1, t2);
        var matches = new List<TicketMatch>();
        Assert.True(MatchmakerManager.MatchFunction(tickets, matches));

        Assert.Empty(matches);
    }

    [Fact]
    public void RequirementTest3()
    {   
        var t1 = CreateTicket();
        t1.AddStateValue(0).Add(1);
        t1.AddRequirements().AddDiscreet(0, 1);

        var t2 = CreateTicket();
        t2.AddStateValue(0).Add(2);
        t2.AddRequirements().AddDiscreet(0, 2);

        var tickets = GetTickets(t1, t2);
        var matches = new List<TicketMatch>();
        Assert.True(MatchmakerManager.MatchFunction(tickets, matches));

        Assert.Empty(matches);
    }

    [Fact]
    public void RequirementTest4()
    {
        var t1 = CreateTicket();
        t1.AddStateValue(0).AddRange([1, 2]);
        t1.AddRequirements().AddDiscreet(0, 1, 2);

        var t2 = CreateTicket();
        t2.AddStateValue(0).Add(2);
        t2.AddRequirements().AddDiscreet(0, 2);

        var tickets = GetTickets(t1, t2);
        var matches = new List<TicketMatch>();
        Assert.True(MatchmakerManager.MatchFunction(tickets, matches));

        // matched because they share a common gamemode
        Assert.Single(matches);
        Assert.True(
            matches[0].MatchedTicketGlobalIds.Contains(t1.GlobalId) ||
            matches[0].MatchedTicketGlobalIds.Contains(t2.GlobalId));
    }

    [Fact]
    public void RequirementTest5()
    {       
        var t1 = CreateTicket();
        t1.AddStateValue(0).AddRange([1, 2]);
        t1.AddRequirements().AddDiscreet(0, 2, 3);

        var t2 = CreateTicket();
        t2.AddStateValue(1).Add(2);
        t2.AddRequirements().AddDiscreet(1, 2);

        var tickets = GetTickets(t1, t2);
        var matches = new List<TicketMatch>();
        Assert.True(MatchmakerManager.MatchFunction(tickets, matches));

        Assert.Empty(matches);
    }

    [Fact]
    public void RequirementTest5_OneSidedMatch()
    {
        var t1 = CreateTicket();
        t1.AddStateValue(0).AddRange([1, 2]);
        t1.AddRequirements().AddDiscreet(0, 1, 2);

        var t2 = CreateTicket();
        t2.AddStateValue(0).Add(2); // even if ticket1 matches with ticket2, ticket2 won't match with ticket1
        t2.AddRequirements().AddDiscreet(1, 2);

        var tickets = GetTickets(t1, t2);
        var matches = new List<TicketMatch>();
        Assert.True(MatchmakerManager.MatchFunction(tickets, matches));

        // should be empty because the match needs to go both-ways
        Assert.Empty(matches);
    }

    [Fact]
    public void RequirementTest6()
    {
        var t1 = CreateTicket();
        t1.AddStateValue(0).AddRange([1, 2]);
        t1.AddStateValue(1).Add(1);
        t1.AddRequirements().AddDiscreet(0, 1, 2);
        t1.AddRequirements().AddDiscreet(1, 1);

        var t2 = CreateTicket();
        t2.AddStateValue(0).Add(2);
        t2.AddStateValue(1).Add(3);
        t2.AddRequirements().AddDiscreet(0, 2);
        t2.AddRequirements().AddDiscreet(1, 3);

        var tickets = GetTickets(t1, t2);
        var matches = new List<TicketMatch>();
        Assert.True(MatchmakerManager.MatchFunction(tickets, matches));

        // empty because ALL keys need to match
        Assert.Empty(matches);
    }

    [Fact]
    public void RequirementTest7()
    {
        var t1 = CreateTicket();
        t1.AddStateValue(0).AddRange([1, 2]);
        t1.AddStateValue(1).Add(1);
        t1.AddRequirements()
            .AddDiscreet(0, 1, 2)
            .AddDiscreet(1, 1);

        var t2 = CreateTicket();
        t2.AddStateValue(0).Add(2);
        t2.AddStateValue(1).Add(3);
        t2.AddRequirements()
          .AddDiscreet(0, 2)
          .AddDiscreet(1, 3);

        var tickets = GetTickets(t1, t2);
        var matches = new List<TicketMatch>();
        Assert.True(MatchmakerManager.MatchFunction(tickets, matches));

        // matched because we are using ANY = at least 1 criteria must match
        Assert.Single(matches);
        Assert.True(
            matches[0].MatchedTicketGlobalIds.Contains(t1.GlobalId) ||
            matches[0].MatchedTicketGlobalIds.Contains(t2.GlobalId));
    }

    [Fact]
    public void RequirementTest8()
    {
        
        var t1 = CreateTicket();
        t1.AddStateValue(0).AddRange([1, 3]);
        t1.AddStateValue(1).Add(1);
        t1.AddRequirements()
          .AddDiscreet(0, 1, 3)
          .AddDiscreet(1, 1);

        var t2 = CreateTicket();
        t2.AddStateValue(0).Add(2);
        t2.AddStateValue(1).Add(3);
        t2.AddRequirements()
          .AddDiscreet(0, 2)
          .AddDiscreet(1, 3);

        var tickets = GetTickets(t1, t2);
        var matches = new List<TicketMatch>();
        Assert.True(MatchmakerManager.MatchFunction(tickets, matches));

        // empty because nothing matches
        Assert.Empty(matches);
    }

    [Fact]
    public void RequirementTest9()
    {
        
        var t1 = CreateTicket();
        t1.AddStateValue(0).AddRange([1, 3]);
        t1.AddStateValue(1).Add(1);
        t1.AddStateValue(2).AddRange([3, 4, 5]);

        t1.AddRequirements().AddDiscreet(0, 1, 3);
        t1.AddRequirements()
            .AddDiscreet(2, 3, 4, 5)
            .AddDiscreet(1, 1);

        var t2 = CreateTicket();
        t2.AddStateValue(0).AddRange([3, 1]);
        t2.AddStateValue(1).AddRange([2, 1]);
        t2.AddStateValue(2).AddRange([1, 2, 6]);
        t2.AddRequirements().AddDiscreet(0, 3, 1);
        t2.AddRequirements()
            .AddDiscreet(2, 1, 2, 6)
            .AddDiscreet(1, 2, 1);

        var tickets = GetTickets(t1, t2);
        var matches = new List<TicketMatch>();
        Assert.True(MatchmakerManager.MatchFunction(tickets, matches));

        // matches because ALL requirements match and ANY requirements one matches, and another one doesn't
        Assert.Single(matches);
    }

    [Fact]
    public void RequirementTest10()
    { 
        var t1 = CreateTicket();
        t1.AddStateValue(0).AddRange([1, 3]);
        t1.AddStateValue(1).Add(1);
        t1.AddStateValue(2).AddRange([3, 4, 5]);

        t1.AddRequirements().AddDiscreet(0, 1, 3);
        t1.AddRequirements().AddDiscreet(2, 3, 4, 5);
        t1.AddRequirements().AddDiscreet(1, 1);

        var t2 = CreateTicket();
        t2.AddStateValue(0).AddRange([3, 1]);
        t2.AddStateValue(1).AddRange([2, 1]);
        t2.AddStateValue(2).AddRange([1, 2, 6]);

        t2.AddRequirements().AddDiscreet(0, 3, 1);
        t2.AddRequirements().AddDiscreet(2, 1, 2, 6);
        t2.AddRequirements().AddDiscreet(1, 2, 1);

        var tickets = GetTickets(t1, t2);
        var matches = new List<TicketMatch>();
        Assert.True(MatchmakerManager.MatchFunction(tickets, matches));

        // does not match because requirements are separated (if they were together in one group, would match)
        Assert.Empty(matches);
    }

    [Fact]
    public void RequirementTest11()
    {
        
        var t1 = CreateTicket();
        t1.AddStateValue(0).AddRange([3, 4, 5]);
        t1.AddStateValue(1).Add(1);
        t1.AddRequirements()
          .AddDiscreet(0, 3, 4, 5)
          .AddDiscreet(1, 1);

        var t2 = CreateTicket();
        t2.AddStateValue(1).AddRange([2, 1]);
        t2.AddRequirements()
          .AddDiscreet(1, 2, 1);

        var tickets = GetTickets(t1, t2);
        var matches = new List<TicketMatch>();
        Assert.True(MatchmakerManager.MatchFunction(tickets, matches));

        // matches because at least one ANY requirement matches
        Assert.Single(matches);
    }

    [Fact]
    public void RequirementTest12()
    {
        
        var t1 = CreateTicket();
        t1.AddStateValue(0).AddRange([3, 4, 5]);
        t1.AddStateValue(1).Add(1);
        t1.AddRequirements().AddDiscreet(0, 3, 4, 5);
        t1.AddRequirements().AddDiscreet(1, 1);

        var t2 = CreateTicket();
        t2.AddStateValue(1).AddRange([2, 1]);
        t2.AddRequirements().AddDiscreet(1, 2, 1);

        var tickets = GetTickets(t1, t2);
        var matches = new List<TicketMatch>();
        Assert.True(MatchmakerManager.MatchFunction(tickets, matches));

        // matches because maps dont match
        Assert.Empty(matches);
    }

    [Fact]
    public void RequirementTest13()
    {
        
        var t1 = CreateTicket();
        t1.AddStateValue(0).AddRange([3, 4, 5]);
        t1.AddStateValue(1).Add(1);
        t1.AddRequirements().AddDiscreet(0, 3, 4, 5);
        t1.AddRequirements().AddDiscreet(1, 1);

        var t2 = CreateTicket();
        t2.AddStateValue(0).Add(3);
        t2.AddStateValue(1).AddRange([2, 1]);
        t2.AddRequirements().AddDiscreet(1, 2, 1);

        var tickets = GetTickets(t1, t2);
        var matches = new List<TicketMatch>();
        Assert.True(MatchmakerManager.MatchFunction(tickets, matches));

        // matches because all requirements match even if ticket2 had no requirement for map
        Assert.Single(matches);
    }

    [Fact]
    public void RequirementTest14()
    {
        
        var t1 = CreateTicket();
        t1.AddStateValue(0).Add(120);
        t1.AddRequirements().AddRange(0, 300, 500);

        var t2 = CreateTicket();
        t2.AddStateValue(0).Add(330);
        t2.AddRequirements().AddRange(0, 100, 400);

        var tickets = GetTickets(t1, t2);
        var matches = new List<TicketMatch>();
        Assert.True(MatchmakerManager.MatchFunction(tickets, matches));

        Assert.Single(matches);
    }

    [Fact]
    public void RequirementTest15()
    {
        
        var t1 = CreateTicket();
        t1.AddStateValue(0).Add(330);
        t1.AddRequirements().AddRange(0, 300, 500);

        var t2 = CreateTicket();
        t2.AddStateValue(0).Add(330);
        t2.AddRequirements().AddRange(0, 100, 250);

        var tickets = GetTickets(t1, t2);
        var matches = new List<TicketMatch>();
        Assert.True(MatchmakerManager.MatchFunction(tickets, matches));

        // there is no intersection
        Assert.Empty(matches);
    }

    [Fact]
    public void RequirementTest16()
    {
        
        var t1 = CreateTicket();
        t1.AddStateValue(0).Add(350);
        t1.AddStateValue(1).Add(70);
        t1.AddRequirements()
            .AddRange(0, 300, 500)
            .AddRange(1, 50, 200);

        var t2 = CreateTicket();
        t2.AddStateValue(0).Add(130);
        t2.AddStateValue(1).Add(65);
        t2.AddRequirements()
            .AddRange(0, 100, 250)
            .AddRange(1, 60, 70);

        var tickets = GetTickets(t1, t2);
        var matches = new List<TicketMatch>();
        Assert.True(MatchmakerManager.MatchFunction(tickets, matches));

        // match because one ANY requirement matches -> ping
        Assert.Single(matches);
    }

    [Fact]
    public void RequirementTest17()
    {
        
        var t1 = CreateTicket();
        t1.AddStateValue(0).Add(350);
        t1.AddStateValue(1).Add(70);
        t1.AddStateValue(2).Add(1);

        t1.AddRequirements()
            .AddRange(0, 300, 500)
            .AddRange(1, 50, 200);

        t1.AddRequirements().AddDiscreet(2, 1);

        var t2 = CreateTicket();
        t2.AddStateValue(0).Add(150);
        t2.AddStateValue(1).Add(65);
        t2.AddStateValue(2).AddRange([2, 1]);

        t2.AddRequirements()
            .AddRange(0, 100, 250)
            .AddRange(1, 60, 70);

        t2.AddRequirements().AddDiscreet(2, 2, 1);

        var tickets = GetTickets(t1, t2);
        var matches = new List<TicketMatch>();
        Assert.True(MatchmakerManager.MatchFunction(tickets, matches));

        // match because both requirements are ture
        Assert.Single(matches);
    }

    [Fact]
    public void RequirementTest18()
    {
        
        var t1 = CreateTicket();
        t1.AddStateValue(0).Add(350);
        t1.AddStateValue(1).Add(70);
        t1.AddStateValue(2).Add(1);

        t1.AddRequirements()
            .AddRange(0, 300, 500)
            .AddRange(1, 50, 200);

        t1.AddRequirements().AddDiscreet(2, 1);

        var t2 = CreateTicket();
        t2.AddStateValue(0).Add(400);
        t2.AddStateValue(2).AddRange([2, 1]);

        var tickets = GetTickets(t1, t2);
        var matches = new List<TicketMatch>();
        Assert.True(MatchmakerManager.MatchFunction(tickets, matches));

        Assert.Single(matches);
    }

    [Fact]
    public void RequirementTest19()
    {
        
        var t1 = CreateTicket();
        t1.AddStateValue(0).Add(350);
        t1.AddStateValue(1).Add(70);
        t1.AddStateValue(2).Add(1);

        t1.AddRequirements()
            .AddRange(0, 300, 500)
            .AddRange(1, 50, 200);

        t1.AddRequirements().AddDiscreet(2, 1);

        var t2 = CreateTicket();
        t2.AddStateValue(0).Add(400);

        var tickets = GetTickets(t1, t2);
        var matches = new List<TicketMatch>();
        Assert.True(MatchmakerManager.MatchFunction(tickets, matches));

        // failed because 2nd requirement failed to match for ticket1
        Assert.Empty(matches);
    }

    [Fact]
    public void RequirementTest20()
    {
        
        var t1 = CreateTicket();
        t1.AddStateValue(0).Add(330);
        t1.AddStateValue(1).Add(1);

        t1.AddRequirements().AddRange(0, 300, 500);

        t1.AddRequirements().AddDiscreet(1, 1);

        var t2 = CreateTicket();
        t2.AddStateValue(0).Add(210);
        t2.AddStateValue(1).AddRange([2, 1]);

        t2.AddRequirements().AddRange(0, 100, 250);

        t2.AddRequirements().AddDiscreet(1, 2, 1);

        var tickets = GetTickets(t1, t2);
        var matches = new List<TicketMatch>();
        MatchmakerManager.MatchFunction(tickets, matches);

        // does not match because one requirement is false
        Assert.Empty(matches);
    }

    [Fact]
    public void RequirementTest21_MultipleTickets()
    {
        
        var t1 = CreateTicket();
        t1.AddStateValue(0).Add(2);
        t1.AddRequirements().AddDiscreet(0, 1, 2);

        var t2 = CreateTicket();
        t2.AddStateValue(0).Add(2);
        t2.AddRequirements().AddDiscreet(0, 2);

        var t3 = CreateTicket();
        t3.AddStateValue(0).Add(2);
        t3.AddRequirements().AddDiscreet(0, 2, 1);

        var t4 = CreateTicket();
        t4.AddStateValue(0).Add(2);
        t4.AddRequirements().AddDiscreet(0, 2, 1);

        var tickets = GetTickets(t1, t2, t3, t4);
        var matches = new List<TicketMatch>();
        MatchmakerManager.MatchFunction(tickets, matches);

        // matched because they share a common gamemode
        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public void RequirementTest22()
    {
        
        var t1 = CreateTicket();
        t1.AddRequirements().AddDiscreet(0, 1);

        var t2 = CreateTicket();

        var tickets = GetTickets(t1, t2);
        var matches = new List<TicketMatch>();
        MatchmakerManager.MatchFunction(tickets, matches);

        // matched because they share a common gamemode
        Assert.Empty(matches);
    }

    [Fact]
    public void RequirementTest23_MatchSize3()
    {
        
        var t1 = CreateTicket();
        t1.AddStateValue(0).Add(2);
        t1.AddRequirements().AddDiscreet(0, 2);

        var t2 = CreateTicket();
        t2.AddStateValue(0).Add(1);
        t2.AddRequirements().AddDiscreet(0, 1);

        var t3 = CreateTicket();
        t3.AddStateValue(0).Add(2);
        t3.AddRequirements().AddDiscreet(0, 2);

        var t4 = CreateTicket();
        t4.AddStateValue(0).Add(1);
        t4.AddRequirements().AddDiscreet(0, 1);

        var t5 = CreateTicket();
        t5.AddStateValue(0).Add(1);
        t5.AddRequirements().AddDiscreet(0, 1);

        var t6 = CreateTicket();
        t6.AddStateValue(0).Add(2);
        t6.AddRequirements().AddDiscreet(0, 2);

        var matches = new List<TicketMatch>();

        // do multiple times to ensure it's not a fluke
        for (int i = 0; i < 50; i++)
        {
            matches.Clear();
            var tickets = GetTickets(3, t1, t2, t3, t4, t5, t6);
            MatchmakerManager.MatchFunction(tickets, matches, match_size: 3);

            Assert.Equal(2, matches.Count);

            var m1 = matches.Find(x => x.MatchedTicketGlobalIds.Contains(t1.GlobalId));
            Assert.NotNull(m1);
            Assert.Contains(t3.GlobalId, m1.MatchedTicketGlobalIds);
            Assert.Contains(t6.GlobalId, m1.MatchedTicketGlobalIds);

            var m2 = matches.Find(x => x.MatchedTicketGlobalIds.Contains(t2.GlobalId));
            Assert.NotNull(m2);
            Assert.Contains(t2.GlobalId, m2.MatchedTicketGlobalIds);
            Assert.Contains(t5.GlobalId, m2.MatchedTicketGlobalIds);
        }
    }

    [Fact]
    public void RequirementTest23_MatchSize10()
    {
        
        var tickets_raw = new Ticket[30];
        for (int i = 0; i < tickets_raw.Length; i++)
        {
            var t = CreateTicket();

            var gamemode = 2;
            if (i < 10) gamemode = 2;
            else if (i < 20) gamemode = 3;
            else if (i < 25) gamemode = 4;
            else gamemode = 5;

            t.AddStateValue(0).Add(gamemode);
            t.AddRequirements().AddDiscreet(0, gamemode);
            tickets_raw[i] = t;
        }

        var matches = new List<TicketMatch>();

        // do multiple times to ensure it's not a fluke
        for (int i = 0; i < 50; i++)
        {
            matches.Clear();
            var tickets = GetTickets(10, tickets_raw);
            MatchmakerManager.MatchFunction(tickets, matches, match_size: 10);

            Assert.Equal(2, matches.Count);

            // all gamemode 2 tickets should have gamemode 2 and nothing else
            var m1 = matches.Find(x => x.MatchedTicketGlobalIds.Contains(tickets_raw[0].GlobalId));
            Assert.NotNull(m1);
            foreach (var t in m1.MatchedTicketGlobalIds)
            {
                foreach (var tt in tickets_raw)
                {
                    if (t != tt.GlobalId) continue;
                    Assert.Equal(tickets_raw[0].State[0], tt.State[0]);
                }
            }

            // all gamemode 3 tickets should have gamemode 3 and nothing else
            var m2 = matches.Find(x => x.MatchedTicketGlobalIds.Contains(tickets_raw[11].GlobalId));
            Assert.NotNull(m2);
            foreach (var t in m2.MatchedTicketGlobalIds)
            {
                foreach (var tt in tickets_raw)
                {
                    if (t != tt.GlobalId) continue;
                    Assert.Equal(tickets_raw[11].State[0], tt.State[0]);
                }
            }
        }
    }
}