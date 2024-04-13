using CryMatch.Core;
using CryMatch.Matchmaker;
using CryMatch.Matchmaker.Plugins;

using CryMatchGrpc;

namespace CryMatchTests;

public class MatchAffinityAndPriorityTests
{
    static ReadOnlyMemory<TicketData> GetTickets(params Ticket[] tickets) => GetTicketsWithCandidatesSize(8, tickets);
    static ReadOnlyMemory<TicketData> GetTicketsWithCandidatesSize(int candidates_size, params Ticket[] tickets)
        => tickets.Select(x => new TicketData(x, x.State.Count + 3, candidates_size)).ToArray().AsMemory();
    static Ticket CreateTicket() => new Ticket() { GlobalId = Guid.NewGuid().ToString() };

    [Fact]
    public void AffinityTest1()
    {
        var t1 = CreateTicket();
        t1.Affinities.Add(new MatchmakingAffinity
        {
            Value = 1200,
            MaxMargin = 1000,
            PreferDisimilar = false,
            PriorityFactor = 1,
            SoftMargin = true
        });

        var t2 = CreateTicket();
        t2.Affinities.Add(new MatchmakingAffinity
        {
            Value = 1000,
            MaxMargin = 1000,
            PreferDisimilar = false,
            PriorityFactor = 1,
            SoftMargin = true
        });

        var t3 = CreateTicket();
        t3.Affinities.Add(new MatchmakingAffinity
        {
            Value = 1000,
            MaxMargin = 1000,
            PreferDisimilar = false,
            PriorityFactor = 1,
            SoftMargin = true
        });

        var t4 = CreateTicket();
        t4.Affinities.Add(new MatchmakingAffinity
        {
            Value = 1100,
            MaxMargin = 1000,
            PreferDisimilar = false,
            PriorityFactor = 1,
            SoftMargin = true
        });

        var matches = new List<TicketMatch>();

        // try multiple times to ensure it's not a random fluke
        for (int i = 0; i < 50; i++)
        {
            matches.Clear();
            var tickets = GetTickets(t1, t2, t3, t4);
            Assert.True(MatchmakerManager.MatchFunction(tickets, matches));

            Assert.True(matches.Count == 2);
            var m = matches.Find(x => x.MatchedTicketGlobalIds.Contains(t1.GlobalId));
            Assert.NotNull(m);
            Assert.True(m.MatchedTicketGlobalIds.Count == 2);
            Assert.Contains(t4.GlobalId, m.MatchedTicketGlobalIds);
        }
    }

    [Fact]
    public void AffinityTest2()
    {
        var t1 = CreateTicket();
        t1.Affinities.Add(new MatchmakingAffinity
        {
            Value = 1100,
            MaxMargin = 1000,
            PreferDisimilar = true,
            PriorityFactor = 1,
            SoftMargin = true
        });

        var t2 = CreateTicket();
        t2.Affinities.Add(new MatchmakingAffinity
        {
            Value = 900,
            MaxMargin = 1000,
            PreferDisimilar = true,
            PriorityFactor = 1,
            SoftMargin = true
        });

        var t3 = CreateTicket();
        t3.Affinities.Add(new MatchmakingAffinity
        {
            Value = 1000,
            MaxMargin = 1000,
            PreferDisimilar = true,
            PriorityFactor = 1,
            SoftMargin = true
        });

        var t4 = CreateTicket();
        t4.Affinities.Add(new MatchmakingAffinity
        {
            Value = 1200,
            MaxMargin = 1000,
            PreferDisimilar = true,
            PriorityFactor = 1,
            SoftMargin = true
        });

        var matches = new List<TicketMatch>();

        // try multiple times to ensure it's not a random fluke
        for (int i = 0; i < 50; i++)
        {
            matches.Clear();
            var tickets = GetTickets(t1, t2, t3, t4);
            Assert.True(MatchmakerManager.MatchFunction(tickets, matches));

            Assert.True(matches.Count == 2);
            var m = matches.Find(x => x.MatchedTicketGlobalIds.Contains(t4.GlobalId));

            Assert.NotNull(m);

            // NOTE: t1 will be handled first, which will take away the most ideal match for t4
            // this is because t1 was added to queue first, this behaviour is good
            Assert.Contains(t3.GlobalId, m.MatchedTicketGlobalIds);
        }
    }

    [Fact]
    public void AffinityTest3()
    {
        var t1 = CreateTicket();
        t1.Affinities.Add(new MatchmakingAffinity
        {
            Value = 1000,
            MaxMargin = 1000,
            PreferDisimilar = false,
            PriorityFactor = 1,
            SoftMargin = true
        });

        var t2 = CreateTicket();
        t2.Affinities.Add(new MatchmakingAffinity
        {
            Value = 900,
            MaxMargin = 1000,
            PreferDisimilar = true,
            PriorityFactor = 1,
            SoftMargin = true
        });

        var t3 = CreateTicket();
        t3.Affinities.Add(new MatchmakingAffinity
        {
            Value = 1010,
            MaxMargin = 1000,
            PreferDisimilar = false,
            PriorityFactor = 1,
            SoftMargin = true
        });

        var t4 = CreateTicket();
        t4.Affinities.Add(new MatchmakingAffinity
        {
            Value = 1200,
            MaxMargin = 1000,
            PreferDisimilar = true,
            PriorityFactor = 1,
            SoftMargin = true
        });

        var matches = new List<TicketMatch>();

        // try multiple times to ensure it's not a random fluke
        for (int i = 0; i < 50; i++)
        {
            matches.Clear();
            var tickets = GetTickets(t1, t2, t3, t4);
            Assert.True(MatchmakerManager.MatchFunction(tickets, matches));

            Assert.True(matches.Count == 2);
            var m = matches.Find(x => x.MatchedTicketGlobalIds.Contains(t4.GlobalId));

            Assert.NotNull(m);

            Assert.Contains(t2.GlobalId, m.MatchedTicketGlobalIds);
        }
    }

    [Fact]
    public void AffinityTest4()
    {
        var t1 = CreateTicket();
        t1.Affinities.Add(new MatchmakingAffinity
        {
            Value = 1200,
            MaxMargin = 100,
            PreferDisimilar = false,
            PriorityFactor = 1,
            SoftMargin = false
        });

        var t2 = CreateTicket();
        t2.Affinities.Add(new MatchmakingAffinity
        {
            Value = 1000,
            MaxMargin = 1000,
            PreferDisimilar = false,
            PriorityFactor = 1,
            SoftMargin = true
        });

        var t3 = CreateTicket();
        t3.Affinities.Add(new MatchmakingAffinity
        {
            Value = 1000,
            MaxMargin = 1000,
            PreferDisimilar = false,
            PriorityFactor = 1,
            SoftMargin = true
        });

        var t4 = CreateTicket();
        t4.Affinities.Add(new MatchmakingAffinity
        {
            Value = 1050,
            MaxMargin = 1000,
            PreferDisimilar = false,
            PriorityFactor = 1,
            SoftMargin = true
        });

        var matches = new List<TicketMatch>();

        // try multiple times to ensure it's not a random fluke
        for (int i = 0; i < 50; i++)
        {
            matches.Clear();

            var tickets = GetTickets(t1, t2, t3, t4);
            Assert.True(MatchmakerManager.MatchFunction(tickets, matches));

            Assert.Single(matches);
            var m = matches.Find(x => x.MatchedTicketGlobalIds.Contains(t2.GlobalId));
            Assert.NotNull(m);
            Assert.True(m.MatchedTicketGlobalIds.Count == 2);
            Assert.Contains(t3.GlobalId, m.MatchedTicketGlobalIds);
        }
    }

    [Fact]
    public void AffinityTest7_Multiple()
    {
        var t1 = CreateTicket();
        t1.Affinities.Add(new MatchmakingAffinity
        {
            Value = 1500,
            MaxMargin = 1000,
            PreferDisimilar = false,
            PriorityFactor = 1,
            SoftMargin = true
        });
        t1.Affinities.Add(new MatchmakingAffinity
        {
            Value = 1000,
            MaxMargin = 1000,
            PreferDisimilar = true,
            PriorityFactor = 1,
            SoftMargin = true
        });

        // -----------
        var t2 = CreateTicket();
        t2.Affinities.Add(new MatchmakingAffinity
        {
            Value = 1000,
            MaxMargin = 1000,
            PreferDisimilar = false,
            PriorityFactor = 1,
            SoftMargin = true
        });
        t2.Affinities.Add(new MatchmakingAffinity
        {
            Value = 1000,
            MaxMargin = 1000,
            PreferDisimilar = true,
            PriorityFactor = 1,
            SoftMargin = true
        });

        // -----------
        var t3 = CreateTicket();
        t3.Affinities.Add(new MatchmakingAffinity
        {
            Value = 900,
            MaxMargin = 1000,
            PreferDisimilar = false,
            PriorityFactor = 1,
            SoftMargin = true
        });
        t3.Affinities.Add(new MatchmakingAffinity
        {
            Value = 900,
            MaxMargin = 1000,
            PreferDisimilar = true,
            PriorityFactor = 1,
            SoftMargin = true
        });

        // -----------
        var t4 = CreateTicket();
        t4.Affinities.Add(new MatchmakingAffinity
        {
            Value = 1500,
            MaxMargin = 1000,
            PreferDisimilar = false,
            PriorityFactor = 1,
            SoftMargin = true
        });
        t4.Affinities.Add(new MatchmakingAffinity
        {
            Value = 1000,
            MaxMargin = 1000,
            PreferDisimilar = true,
            PriorityFactor = 1,
            SoftMargin = true
        });
        // -----------

        var matches = new List<TicketMatch>();

        // try multiple times to ensure it's not a random fluke
        for (int i = 0; i < 50; i++)
        {
            matches.Clear();
            var tickets = GetTickets(t1, t2, t3, t4);
            Assert.True(MatchmakerManager.MatchFunction(tickets, matches));

            Assert.Equal(2, matches.Count);
            var m = matches.Find(x => x.MatchedTicketGlobalIds.Contains(t1.GlobalId));
            Assert.NotNull(m);
            Assert.True(m.MatchedTicketGlobalIds.Count == 2);

            Assert.Contains(t4.GlobalId, m.MatchedTicketGlobalIds);
        }
    }

    [Fact]
    public void AffinityTest8_Multiple()
    {
        var t1 = CreateTicket();
        t1.Affinities.Add(new MatchmakingAffinity
        {
            Value = 1500,
            MaxMargin = 1000,
            PreferDisimilar = false,
            PriorityFactor = 1,
            SoftMargin = true
        });
        t1.Affinities.Add(new MatchmakingAffinity
        {
            Value = 1000,
            MaxMargin = 1000,
            PreferDisimilar = true,
            PriorityFactor = 1,
            SoftMargin = true
        });

        // -----------
        var t2 = CreateTicket();
        t2.Affinities.Add(new MatchmakingAffinity
        {
            Value = 1000,
            MaxMargin = 1000,
            PreferDisimilar = false,
            PriorityFactor = 1,
            SoftMargin = true
        });
        t2.Affinities.Add(new MatchmakingAffinity
        {
            Value = 0,
            MaxMargin = 1000,
            PreferDisimilar = true,
            PriorityFactor = 1,
            SoftMargin = true
        });

        // -----------
        var t3 = CreateTicket();
        t3.Affinities.Add(new MatchmakingAffinity
        {
            Value = 900,
            MaxMargin = 1000,
            PreferDisimilar = false,
            PriorityFactor = 1,
            SoftMargin = true
        });
        t3.Affinities.Add(new MatchmakingAffinity
        {
            Value = 900,
            MaxMargin = 1000,
            PreferDisimilar = true,
            PriorityFactor = 1,
            SoftMargin = true
        });

        // -----------
        var t4 = CreateTicket();
        t4.Affinities.Add(new MatchmakingAffinity
        {
            Value = 1500,
            MaxMargin = 1000,
            PreferDisimilar = false,
            PriorityFactor = 1,
            SoftMargin = true
        });
        t4.Affinities.Add(new MatchmakingAffinity
        {
            Value = 1000,
            MaxMargin = 1000,
            PreferDisimilar = true,
            PriorityFactor = 1,
            SoftMargin = true
        });
        // -----------

        var matches = new List<TicketMatch>();

        // try multiple times to ensure it's not a random fluke
        for (int i = 0; i < 50; i++)
        {
            matches.Clear();

            var tickets = GetTickets(t1, t2, t3, t4);
            Assert.True(MatchmakerManager.MatchFunction(tickets, matches));

            Assert.Equal(2, matches.Count);
            var m = matches.Find(x => x.MatchedTicketGlobalIds.Contains(t1.GlobalId));
            Assert.NotNull(m);
            Assert.True(m.MatchedTicketGlobalIds.Count == 2);

            // t2 because it was most disimilar
            Assert.Contains(t2.GlobalId, m.MatchedTicketGlobalIds);
        }
    }

    Ticket CreateTicketWithAffinity(float skill_rating)
    {
        var t = CreateTicket();
        t.Affinities.Add(new MatchmakingAffinity
        {
            Value = skill_rating,
            MaxMargin = 1000,
            PreferDisimilar = false,
            PriorityFactor = 1,
            SoftMargin = true
        });
        return t;
    }

    [Fact]
    public void AffinityTest9_Multiple_Optimal()
    {
        // these will match optimally because of the order in which they are provided
        var t1 = CreateTicketWithAffinity(500);
        var t2 = CreateTicketWithAffinity(1000);
        var t3 = CreateTicketWithAffinity(1020);
        var t4 = CreateTicketWithAffinity(2000);

        var matches = new List<TicketMatch>();

        // try multiple times to ensure it's not a random fluke
        for (int i = 0; i < 50; i++)
        {
            matches.Clear();

            var tickets = GetTickets(t1, t2, t3, t4);
            Assert.True(MatchmakerManager.MatchFunction(tickets, matches));
            Assert.Equal(2, matches.Count);

            var m1 = matches.Find(x => x.MatchedTicketGlobalIds.Contains(t1.GlobalId) && x.MatchedTicketGlobalIds.Contains(t2.GlobalId));
            Assert.NotNull(m1);

            var m2 = matches.Find(x => x.MatchedTicketGlobalIds.Contains(t3.GlobalId) && x.MatchedTicketGlobalIds.Contains(t4.GlobalId));
            Assert.NotNull(m2);
        }
    }

    [Fact]
    public void AffinityTest10_Multiple_Optimal()
    {
        // these will NOT match optimally because 1000 will first match with 1020 and 500 with 2000 -- instead of 500,1000 and 1000,2000 to minimize differences globally
        var t1 = CreateTicketWithAffinity(1000);
        var t2 = CreateTicketWithAffinity(500);
        var t3 = CreateTicketWithAffinity(1020);
        var t4 = CreateTicketWithAffinity(2000);

        var matches = new List<TicketMatch>();

        // try multiple times to ensure it's not a random fluke
        for (int i = 0; i < 50; i++)
        {
            matches.Clear();

            var tickets = GetTickets(t1, t2, t3, t4);
            Assert.True(MatchmakerManager.MatchFunction(tickets, matches));
            Assert.Equal(2, matches.Count);

            var m1 = matches.Find(x => x.MatchedTicketGlobalIds.Contains(t1.GlobalId) && x.MatchedTicketGlobalIds.Contains(t3.GlobalId));
            Assert.NotNull(m1);

            var m2 = matches.Find(x => x.MatchedTicketGlobalIds.Contains(t2.GlobalId) && x.MatchedTicketGlobalIds.Contains(t4.GlobalId));
            Assert.NotNull(m2);
        }
    }

    [Fact]
    public void AgeTest1()
    {
        var now = DateTime.UtcNow;

        var t1 = CreateTicket();
        t1.TimestampExpiryMatchmaker = now.ToBinary();
        t1.AgePriorityFactor = 1;

        var t2 = CreateTicket();
        t2.TimestampExpiryMatchmaker = now.ToBinary();
        t2.AgePriorityFactor = 1;

        var t3 = CreateTicket();
        t3.TimestampExpiryMatchmaker = now.ToBinary();
        t3.AgePriorityFactor = 1;

        var t4 = CreateTicket();
        t4.TimestampExpiryMatchmaker = now.Subtract(TimeSpan.FromSeconds(1)).ToBinary();
        t4.AgePriorityFactor = 1;

        var matches = new List<TicketMatch>();

        // try multiple times to ensure it's not a random fluke
        for (int i = 0; i < 50; i++)
        {
            matches.Clear();

            var tickets = GetTickets(t1, t2, t3, t4);
            Assert.True(MatchmakerManager.MatchFunction(tickets, matches));

            Assert.Equal(2, matches.Count);
            var m = matches.Find(x => x.MatchedTicketGlobalIds.Contains(t1.GlobalId));
            Assert.NotNull(m);

            // t1 is processed first, and t4 has priority from age
            Assert.Contains(t4.GlobalId, m.MatchedTicketGlobalIds);
        }
    }

    [Fact]
    public void PriorityTest1()
    {
        var now = DateTime.UtcNow;

        var t1 = CreateTicket();
        t1.PriorityBase = 1;

        var t2 = CreateTicket();
        t2.TimestampExpiryMatchmaker = now.ToBinary();
        t2.PriorityBase = 1;

        var t3 = CreateTicket();
        t3.TimestampExpiryMatchmaker = now.ToBinary();
        t3.PriorityBase = 2;

        var t4 = CreateTicket();
        t4.TimestampExpiryMatchmaker = now.Subtract(TimeSpan.FromSeconds(1)).ToBinary();
        t4.PriorityBase = 1;

        var matches = new List<TicketMatch>();

        // try multiple times to ensure it's not a random fluke
        for (int i = 0; i < 50; i++)
        {
            matches.Clear();

            var tickets = GetTickets(t1, t2, t3, t4);
            Assert.True(MatchmakerManager.MatchFunction(tickets, matches));

            Assert.Equal(2, matches.Count);
            var m = matches.Find(x => x.MatchedTicketGlobalIds.Contains(t1.GlobalId));
            Assert.NotNull(m);

            // t1 is processed first, and t4 has priority from age
            Assert.Contains(t3.GlobalId, m.MatchedTicketGlobalIds);
        }
    }

    [Fact]
    public void SimilarPriorityTest_Similar()
    {
        const int MATCH_SIZE = 2;
        const int CANDIDATES_SIZE = 4;

        var tickets = new Ticket[1000];
        for (int i = 0; i < tickets.Length - CANDIDATES_SIZE; i++)
        {
            tickets[i] = CreateTicket();
            tickets[i].PriorityBase = 1;
        }

        var offset = tickets.Length - CANDIDATES_SIZE;
        for (int i = 0; i < CANDIDATES_SIZE; i++)
        {
            tickets[offset + i] = CreateTicket();
            tickets[offset + i].PriorityBase = 1; // here it's the same because "Similar"
        }

        var matches = new List<TicketMatch>();

        // try multiple times to ensure it's not a random fluke
        for (int i = 0; i < 100; i++)
        {
            matches.Clear();

            var tickets_data = GetTicketsWithCandidatesSize(CANDIDATES_SIZE, tickets);
            Assert.False(MatchmakerManager.MatchFunction(tickets_data, matches, match_size: MATCH_SIZE, unreliable_only: true));

            Assert.True(matches.Count >= 400);
        }
    }

    [Fact]
    public void SimilarPriorityTest_DisSimilar()
    {
        const int MATCH_SIZE = 2;
        const int CANDIDATES_SIZE = 8;

        var tickets = new Ticket[1000];
        for (int i = 0; i < tickets.Length - CANDIDATES_SIZE; i++)
        {
            tickets[i] = CreateTicket();
            tickets[i].PriorityBase = 1;
        }

        // below priorities will screw up the candidate picking
        // every ticket will pick these for their candidates,
        // which is not good - this test tests that random noise is property picked
        var offset = tickets.Length - CANDIDATES_SIZE;
        for (int i = 0; i < CANDIDATES_SIZE; i++)
        {
            tickets[offset + i] = CreateTicket();
            tickets[offset + i].PriorityBase = 5;
        }

        var matches = new List<TicketMatch>();

        // try multiple times to ensure it's not a random fluke
        for (int i = 0; i < 100; i++)
        {
            matches.Clear();

            var tickets_data = GetTicketsWithCandidatesSize(CANDIDATES_SIZE, tickets);
            Assert.False(MatchmakerManager.MatchFunction(tickets_data, matches, match_size: MATCH_SIZE, unreliable_only: true));

            Assert.True(matches.Count >= 400); 
        }
    }
}