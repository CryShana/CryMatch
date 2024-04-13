using CryMatch.Core;
using CryMatch.Matchmaker;

using CryMatchGrpc;

namespace CryMatchTests;

public class MatchGeneralTests
{
    static Ticket CreateTicket() => new Ticket() { GlobalId = Guid.NewGuid().ToString() };

    [Fact]
    public void AddCandidateTest()
    {
        const int CANDIDATES_SIZE = 4;

        var t1 = new TicketData(CreateTicket(), 1, CANDIDATES_SIZE);
        var t2 = new TicketData(CreateTicket(), 1, CANDIDATES_SIZE);
        var t3 = new TicketData(CreateTicket(), 1, CANDIDATES_SIZE);
        var t4 = new TicketData(CreateTicket(), 1, CANDIDATES_SIZE);
        var t5 = new TicketData(CreateTicket(), 1, CANDIDATES_SIZE);
        var t6 = new TicketData(CreateTicket(), 1, CANDIDATES_SIZE);
        var t7 = new TicketData(CreateTicket(), 1, CANDIDATES_SIZE);

        Assert.Equal(0, t1.CandidateUsageBy);
        Assert.Equal(0, t2.CandidateUsageBy);
        Assert.Equal(0, t3.CandidateUsageBy);
        Assert.Equal(0, t4.CandidateUsageBy);
        Assert.Equal(0, t5.CandidateUsageBy);
        Assert.Equal(0, t6.CandidateUsageBy);
        Assert.Equal(0, t7.CandidateUsageBy);

        Assert.Null(t1.Candidates[0].Ticket);
        Assert.Null(t1.Candidates[1].Ticket);
        Assert.Null(t1.Candidates[2].Ticket);
        Assert.Null(t1.Candidates[3].Ticket);

        // ADD CANDIDATE
        t1.AddCandidate(new MatchCandidate(t2, 1));

        Assert.Equal(0, t1.CandidateUsageBy);
        Assert.Equal(1, t2.CandidateUsageBy);
        Assert.Equal(0, t3.CandidateUsageBy);
        Assert.Equal(0, t4.CandidateUsageBy);
        Assert.Equal(0, t5.CandidateUsageBy);
        Assert.Equal(0, t6.CandidateUsageBy);
        Assert.Equal(0, t7.CandidateUsageBy);

        Assert.NotNull(t1.Candidates[0].Ticket);
        Assert.Null(t1.Candidates[1].Ticket);
        Assert.Null(t1.Candidates[2].Ticket);
        Assert.Null(t1.Candidates[3].Ticket);

        Assert.Equal(t2.GlobalId, t1.Candidates[0].Ticket!.GlobalId);

        // ADD CANDIDATE
        t1.AddCandidate(new MatchCandidate(t3, 2));

        Assert.Equal(0, t1.CandidateUsageBy);
        Assert.Equal(1, t2.CandidateUsageBy);
        Assert.Equal(1, t3.CandidateUsageBy);
        Assert.Equal(0, t4.CandidateUsageBy);
        Assert.Equal(0, t5.CandidateUsageBy);
        Assert.Equal(0, t6.CandidateUsageBy);
        Assert.Equal(0, t7.CandidateUsageBy);

        Assert.NotNull(t1.Candidates[0].Ticket);
        Assert.NotNull(t1.Candidates[1].Ticket);
        Assert.Null(t1.Candidates[2].Ticket);
        Assert.Null(t1.Candidates[3].Ticket);

        Assert.Equal(t3.GlobalId, t1.Candidates[0].Ticket!.GlobalId);
        Assert.Equal(t2.GlobalId, t1.Candidates[1].Ticket!.GlobalId);

        // ADD CANDIDATE
        t1.AddCandidate(new MatchCandidate(t4, 0.5f));

        Assert.Equal(0, t1.CandidateUsageBy);
        Assert.Equal(1, t2.CandidateUsageBy);
        Assert.Equal(1, t3.CandidateUsageBy);
        Assert.Equal(1, t4.CandidateUsageBy);
        Assert.Equal(0, t5.CandidateUsageBy);
        Assert.Equal(0, t6.CandidateUsageBy);
        Assert.Equal(0, t7.CandidateUsageBy);

        Assert.NotNull(t1.Candidates[0].Ticket);
        Assert.NotNull(t1.Candidates[1].Ticket);
        Assert.NotNull(t1.Candidates[2].Ticket);
        Assert.Null(t1.Candidates[3].Ticket);

        Assert.Equal(t3.GlobalId, t1.Candidates[0].Ticket!.GlobalId);
        Assert.Equal(t2.GlobalId, t1.Candidates[1].Ticket!.GlobalId);
        Assert.Equal(t4.GlobalId, t1.Candidates[2].Ticket!.GlobalId);

        // ADD CANDIDATE
        t1.AddCandidate(new MatchCandidate(t5, 0.5f));

        Assert.Equal(0, t1.CandidateUsageBy);
        Assert.Equal(1, t2.CandidateUsageBy);
        Assert.Equal(1, t3.CandidateUsageBy);
        Assert.Equal(1, t4.CandidateUsageBy);
        Assert.Equal(1, t5.CandidateUsageBy);
        Assert.Equal(0, t6.CandidateUsageBy);
        Assert.Equal(0, t7.CandidateUsageBy);

        Assert.NotNull(t1.Candidates[0].Ticket);
        Assert.NotNull(t1.Candidates[1].Ticket);
        Assert.NotNull(t1.Candidates[2].Ticket);
        Assert.NotNull(t1.Candidates[3].Ticket);

        Assert.Equal(t3.GlobalId, t1.Candidates[0].Ticket!.GlobalId);
        Assert.Equal(t2.GlobalId, t1.Candidates[1].Ticket!.GlobalId);
        Assert.Equal(t4.GlobalId, t1.Candidates[2].Ticket!.GlobalId);
        Assert.Equal(t5.GlobalId, t1.Candidates[3].Ticket!.GlobalId);

        // ADD CANDIDATE (overflow)
        t1.AddCandidate(new MatchCandidate(t6, 0.5f));

        Assert.Equal(0, t1.CandidateUsageBy);
        Assert.Equal(1, t2.CandidateUsageBy);
        Assert.Equal(1, t3.CandidateUsageBy);
        Assert.Equal(1, t4.CandidateUsageBy);
        Assert.Equal(1, t5.CandidateUsageBy);
        Assert.Equal(0, t6.CandidateUsageBy); // failed to get added
        Assert.Equal(0, t7.CandidateUsageBy);

        Assert.NotNull(t1.Candidates[0].Ticket);
        Assert.NotNull(t1.Candidates[1].Ticket);
        Assert.NotNull(t1.Candidates[2].Ticket);
        Assert.NotNull(t1.Candidates[3].Ticket);

        Assert.Equal(t3.GlobalId, t1.Candidates[0].Ticket!.GlobalId);
        Assert.Equal(t2.GlobalId, t1.Candidates[1].Ticket!.GlobalId);
        Assert.Equal(t4.GlobalId, t1.Candidates[2].Ticket!.GlobalId);
        Assert.Equal(t5.GlobalId, t1.Candidates[3].Ticket!.GlobalId);

        // ADD CANDIDATE (overflow)
        t1.AddCandidate(new MatchCandidate(t7, 1.5f));

        Assert.Equal(0, t1.CandidateUsageBy);
        Assert.Equal(1, t2.CandidateUsageBy);
        Assert.Equal(1, t3.CandidateUsageBy);
        Assert.Equal(1, t4.CandidateUsageBy);
        Assert.Equal(0, t5.CandidateUsageBy);
        Assert.Equal(0, t6.CandidateUsageBy);
        Assert.Equal(1, t7.CandidateUsageBy);

        Assert.NotNull(t1.Candidates[0].Ticket);
        Assert.NotNull(t1.Candidates[1].Ticket);
        Assert.NotNull(t1.Candidates[2].Ticket);
        Assert.NotNull(t1.Candidates[3].Ticket);

        Assert.Equal(t3.GlobalId, t1.Candidates[0].Ticket!.GlobalId);
        Assert.Equal(t7.GlobalId, t1.Candidates[1].Ticket!.GlobalId);
        Assert.Equal(t2.GlobalId, t1.Candidates[2].Ticket!.GlobalId);
        Assert.Equal(t4.GlobalId, t1.Candidates[3].Ticket!.GlobalId);

        t2.AddCandidate(new MatchCandidate(t4, 1.5f));

        Assert.Equal(0, t1.CandidateUsageBy);
        Assert.Equal(1, t2.CandidateUsageBy);
        Assert.Equal(1, t3.CandidateUsageBy);
        Assert.Equal(2, t4.CandidateUsageBy);
        Assert.Equal(0, t5.CandidateUsageBy);
        Assert.Equal(0, t6.CandidateUsageBy);
        Assert.Equal(1, t7.CandidateUsageBy);
    }

    [Fact]
    public void AddCandidateThreadSafeTest()
    {
        const int CANDIDATES_SIZE = 4;

        var t1 = new TicketData(CreateTicket(), 1, CANDIDATES_SIZE);
        var t2 = new TicketData(CreateTicket(), 1, CANDIDATES_SIZE);
        var t3 = new TicketData(CreateTicket(), 1, CANDIDATES_SIZE);
        var t4 = new TicketData(CreateTicket(), 1, CANDIDATES_SIZE);
        var t5 = new TicketData(CreateTicket(), 1, CANDIDATES_SIZE);
        var t6 = new TicketData(CreateTicket(), 1, CANDIDATES_SIZE);
        var t7 = new TicketData(CreateTicket(), 1, CANDIDATES_SIZE);

        Assert.Equal(0, t1.CandidateUsageBy);
        Assert.Equal(0, t2.CandidateUsageBy);
        Assert.Equal(0, t3.CandidateUsageBy);
        Assert.Equal(0, t4.CandidateUsageBy);
        Assert.Equal(0, t5.CandidateUsageBy);
        Assert.Equal(0, t6.CandidateUsageBy);
        Assert.Equal(0, t7.CandidateUsageBy);

        Assert.Null(t1.Candidates[0].Ticket);
        Assert.Null(t1.Candidates[1].Ticket);
        Assert.Null(t1.Candidates[2].Ticket);
        Assert.Null(t1.Candidates[3].Ticket);

        // ADD CANDIDATE
        t1.AddCandidateThreadSafe(new MatchCandidate(t2, 1));

        Assert.Equal(0, t1.CandidateUsageBy);
        Assert.Equal(1, t2.CandidateUsageBy);
        Assert.Equal(0, t3.CandidateUsageBy);
        Assert.Equal(0, t4.CandidateUsageBy);
        Assert.Equal(0, t5.CandidateUsageBy);
        Assert.Equal(0, t6.CandidateUsageBy);
        Assert.Equal(0, t7.CandidateUsageBy);

        Assert.NotNull(t1.Candidates[0].Ticket);
        Assert.Null(t1.Candidates[1].Ticket);
        Assert.Null(t1.Candidates[2].Ticket);
        Assert.Null(t1.Candidates[3].Ticket);

        Assert.Equal(t2.GlobalId, t1.Candidates[0].Ticket!.GlobalId);

        // ADD CANDIDATE
        t1.AddCandidateThreadSafe(new MatchCandidate(t3, 2));

        Assert.Equal(0, t1.CandidateUsageBy);
        Assert.Equal(1, t2.CandidateUsageBy);
        Assert.Equal(1, t3.CandidateUsageBy);
        Assert.Equal(0, t4.CandidateUsageBy);
        Assert.Equal(0, t5.CandidateUsageBy);
        Assert.Equal(0, t6.CandidateUsageBy);
        Assert.Equal(0, t7.CandidateUsageBy);

        Assert.NotNull(t1.Candidates[0].Ticket);
        Assert.NotNull(t1.Candidates[1].Ticket);
        Assert.Null(t1.Candidates[2].Ticket);
        Assert.Null(t1.Candidates[3].Ticket);

        Assert.Equal(t3.GlobalId, t1.Candidates[0].Ticket!.GlobalId);
        Assert.Equal(t2.GlobalId, t1.Candidates[1].Ticket!.GlobalId);

        // ADD CANDIDATE
        t1.AddCandidateThreadSafe(new MatchCandidate(t4, 0.5f));

        Assert.Equal(0, t1.CandidateUsageBy);
        Assert.Equal(1, t2.CandidateUsageBy);
        Assert.Equal(1, t3.CandidateUsageBy);
        Assert.Equal(1, t4.CandidateUsageBy);
        Assert.Equal(0, t5.CandidateUsageBy);
        Assert.Equal(0, t6.CandidateUsageBy);
        Assert.Equal(0, t7.CandidateUsageBy);

        Assert.NotNull(t1.Candidates[0].Ticket);
        Assert.NotNull(t1.Candidates[1].Ticket);
        Assert.NotNull(t1.Candidates[2].Ticket);
        Assert.Null(t1.Candidates[3].Ticket);

        Assert.Equal(t3.GlobalId, t1.Candidates[0].Ticket!.GlobalId);
        Assert.Equal(t2.GlobalId, t1.Candidates[1].Ticket!.GlobalId);
        Assert.Equal(t4.GlobalId, t1.Candidates[2].Ticket!.GlobalId);

        // ADD CANDIDATE
        t1.AddCandidateThreadSafe(new MatchCandidate(t5, 0.5f));

        Assert.Equal(0, t1.CandidateUsageBy);
        Assert.Equal(1, t2.CandidateUsageBy);
        Assert.Equal(1, t3.CandidateUsageBy);
        Assert.Equal(1, t4.CandidateUsageBy);
        Assert.Equal(1, t5.CandidateUsageBy);
        Assert.Equal(0, t6.CandidateUsageBy);
        Assert.Equal(0, t7.CandidateUsageBy);

        Assert.NotNull(t1.Candidates[0].Ticket);
        Assert.NotNull(t1.Candidates[1].Ticket);
        Assert.NotNull(t1.Candidates[2].Ticket);
        Assert.NotNull(t1.Candidates[3].Ticket);

        Assert.Equal(t3.GlobalId, t1.Candidates[0].Ticket!.GlobalId);
        Assert.Equal(t2.GlobalId, t1.Candidates[1].Ticket!.GlobalId);
        Assert.Equal(t4.GlobalId, t1.Candidates[2].Ticket!.GlobalId);
        Assert.Equal(t5.GlobalId, t1.Candidates[3].Ticket!.GlobalId);

        // ADD CANDIDATE (overflow)
        t1.AddCandidateThreadSafe(new MatchCandidate(t6, 0.5f));

        Assert.Equal(0, t1.CandidateUsageBy);
        Assert.Equal(1, t2.CandidateUsageBy);
        Assert.Equal(1, t3.CandidateUsageBy);
        Assert.Equal(1, t4.CandidateUsageBy);
        Assert.Equal(1, t5.CandidateUsageBy);
        Assert.Equal(0, t6.CandidateUsageBy);
        Assert.Equal(0, t7.CandidateUsageBy);

        Assert.NotNull(t1.Candidates[0].Ticket);
        Assert.NotNull(t1.Candidates[1].Ticket);
        Assert.NotNull(t1.Candidates[2].Ticket);
        Assert.NotNull(t1.Candidates[3].Ticket);

        Assert.Equal(t3.GlobalId, t1.Candidates[0].Ticket!.GlobalId);
        Assert.Equal(t2.GlobalId, t1.Candidates[1].Ticket!.GlobalId);
        Assert.Equal(t4.GlobalId, t1.Candidates[2].Ticket!.GlobalId);
        Assert.Equal(t5.GlobalId, t1.Candidates[3].Ticket!.GlobalId);

        // ADD CANDIDATE (overflow)
        t1.AddCandidateThreadSafe(new MatchCandidate(t7, 1.5f));

        Assert.Equal(0, t1.CandidateUsageBy);
        Assert.Equal(1, t2.CandidateUsageBy);
        Assert.Equal(1, t3.CandidateUsageBy);
        Assert.Equal(1, t4.CandidateUsageBy);
        Assert.Equal(0, t5.CandidateUsageBy);
        Assert.Equal(0, t6.CandidateUsageBy);
        Assert.Equal(1, t7.CandidateUsageBy);

        Assert.NotNull(t1.Candidates[0].Ticket);
        Assert.NotNull(t1.Candidates[1].Ticket);
        Assert.NotNull(t1.Candidates[2].Ticket);
        Assert.NotNull(t1.Candidates[3].Ticket);

        Assert.Equal(t3.GlobalId, t1.Candidates[0].Ticket!.GlobalId);
        Assert.Equal(t7.GlobalId, t1.Candidates[1].Ticket!.GlobalId);
        Assert.Equal(t2.GlobalId, t1.Candidates[2].Ticket!.GlobalId);
        Assert.Equal(t4.GlobalId, t1.Candidates[3].Ticket!.GlobalId);

        t2.AddCandidateThreadSafe(new MatchCandidate(t4, 1.5f));

        Assert.Equal(0, t1.CandidateUsageBy);
        Assert.Equal(1, t2.CandidateUsageBy);
        Assert.Equal(1, t3.CandidateUsageBy);
        Assert.Equal(2, t4.CandidateUsageBy);
        Assert.Equal(0, t5.CandidateUsageBy);
        Assert.Equal(0, t6.CandidateUsageBy);
        Assert.Equal(1, t7.CandidateUsageBy);
    }
}