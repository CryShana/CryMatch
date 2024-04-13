using CryMatchGrpc;

namespace CryMatch.Matchmaker;

public class TicketData
{
    public readonly string GlobalId;
    public readonly float[][] State;
    public readonly TicketRequirements[] Requirements;
    public readonly TicketAffinity[] Affinities;
    public readonly MatchCandidate[] Candidates;
    public List<MatchCandidate>? CandidatesList;
    public readonly Ticket FullTicket;
    public int CandidatesLast = -1;
    public float CalculatedBasePriority = 0;
    public int CandidateUsageBy = 0;

    public TicketData(Ticket ticket, int state_count, int candidates_size = 8)
    {
        FullTicket = ticket;
        GlobalId = ticket.GlobalId;
        Candidates = new MatchCandidate[candidates_size];

        Affinities = new TicketAffinity[ticket.Affinities.Count];
        for (int i = 0; i < Affinities.Length; i++)
            Affinities[i] = new TicketAffinity(ticket.Affinities[i]);

        var count = ticket.State.Count;

        State = new float[state_count][];
        for (int i = 0; i < state_count; i++)
            State[i] = i < count ? ticket.State[i].Values.ToArray() : Array.Empty<float>();
        
        Requirements = new TicketRequirements[ticket.Requirements.Count];
        for (int i = 0; i < Requirements.Length; i++)
            Requirements[i] = new TicketRequirements(ticket.Requirements[i], state_count);
    }

    /// <summary>
    /// Add to candidate list while keeping the Descending order of ratings.
    /// If candidate can not be added, returns false
    /// </summary>
    public bool AddCandidate(MatchCandidate candidate)
    {
        var candidates = Candidates.AsSpan();
        var length = candidates.Length;
        var rating = candidate.Rating;

        // compare to last inserted one first
        var last = CandidatesLast;
        if (last >= 0 && candidates[last].Rating >= rating)
        {
            // the last possible candidate is higher rated, we can't do anything
            if (last >= length - 1) return false;

            // insert
            var new_index = last + 1;
            candidates[new_index] = candidate;
            CandidatesLast = new_index;

            // increment usage counter
            candidate.Ticket!.CandidateUsageBy++;
            return true;
        }

        for (int i = 0; i < length; i++)
        {
            ref MatchCandidate current = ref candidates[i];

            // if current one is higher rated, we keep moving on
            if (current.Rating >= rating)
                continue;

            // null ticket is checked last because most checks will be against non-NULL entries
            // and we want to minimize unnecessary checks if first one is already better rated
            if (current.Ticket == null)
            {
                current = candidate;
                CandidatesLast = i;

                candidate.Ticket!.CandidateUsageBy++;
                return true;
            }

            // we have to make space by moving other candidates down
            var to_move = length - i - 1; // -1 because last item is lost
            var from = candidates.Slice(i, to_move);

            // decrement usage of lost item
            if (last == candidates.Length - 1)
            {
                candidates[last].Ticket!.CandidateUsageBy--;
            }

            // move down
            var to = candidates.Slice(i + 1, to_move);
            from.CopyTo(to);

            // insert the element
            current = candidate;

            // one null disappeared
            if (CandidatesLast < length - 1) 
                CandidatesLast++;

            // increment usage counter
            candidate.Ticket!.CandidateUsageBy++;
            return true;
        }

        return false;
    }
    public bool AddCandidateThreadSafe(MatchCandidate candidate)
    {
        var candidates = Candidates.AsSpan();
        var length = candidates.Length;
        var rating = candidate.Rating;
        var success = false;

        // compare to last inserted one first
        var last = CandidatesLast;
        if (last >= 0 && candidates[last].Rating >= rating)
        {
            // the last possible candidate is higher rated, we can't do anything
            if (last >= length - 1) return false;

            lock (Candidates)
            {
                // check again
                last = CandidatesLast;
                if (candidates[last].Rating >= rating)
                {
                    if (last >= length - 1) return false;

                    // insert
                    var new_index = last + 1;
                    candidates[new_index] = candidate;
                    CandidatesLast = new_index;
                    success = true;
                }
            }

            if (success)
            {
                Interlocked.Increment(ref candidate.Ticket!.CandidateUsageBy);
                return true;
            }
        }

        for (int i = 0; i < length; i++)
        {
            // if current one is higher rated, we keep moving on
            if (candidates[i].Rating >= rating)
                continue;

            // only lock when modifying array
            lock (Candidates)
            {
                ref MatchCandidate current = ref candidates[i];

                // check again, as another thread may have changed it
                if (current.Rating >= rating)
                    continue;

                // null ticket is checked last because most checks will be against non-NULL entries
                // and we want to minimize unnecessary checks if first one is already better rated
                if (current.Ticket == null)
                {
                    current = candidate;
                    CandidatesLast = i;

                    Interlocked.Increment(ref candidate.Ticket!.CandidateUsageBy);
                    return true;
                }

                // we have to make space by moving other candidates down
                var to_move = length - i - 1; // -1 because last item is lost
                var from = candidates.Slice(i, to_move);

                // decrement usage of lost item
                if (last == candidates.Length - 1)
                {
                    Interlocked.Decrement(ref candidates[last].Ticket!.CandidateUsageBy);
                }

                var to = candidates.Slice(i + 1, to_move);
                from.CopyTo(to);

                // insert the element
                current = candidate;

                // one null disappeared
                if (CandidatesLast < length - 1)
                    CandidatesLast++;
            }
         
            Interlocked.Increment(ref candidate.Ticket!.CandidateUsageBy);
            return true;  
        }

        return false;
    }

    public bool AddCandidateToList(MatchCandidate candidate)
    {
        CandidatesList!.Add(candidate);
        candidate.Ticket!.CandidateUsageBy++;
        return true;
    } 
}

public readonly struct TicketRequirements
{
    public readonly Req[] Any;

    public TicketRequirements(MatchmakingRequirements reqs, int state_count)
    {
        Any = new Req[reqs.Any.Count];
        for (int i = 0; i < Any.Length; i++)
        {
            var item = reqs.Any[i];

            // if key is out of bounds for state, it can be ignored
            if (item.Key >= state_count) continue;

            Any[i] = new Req(item);
        }
    }
}

public class TicketAffinity
{
    public readonly float Value;
    public readonly float MaxMarginInverted;
    public readonly float PriorityFactor;
    public readonly bool PreferDisimilar;
    public readonly bool SoftMargin;

    public TicketAffinity(MatchmakingAffinity affinity)
    {
        Value = affinity.Value;
        MaxMarginInverted = 1.0f / affinity.MaxMargin;
        PriorityFactor = affinity.PriorityFactor;
        PreferDisimilar = affinity.PreferDisimilar;
        SoftMargin = affinity.SoftMargin;
    }
}

public readonly struct Req
{
    public readonly int Key;
    public readonly bool Ranged;
    public readonly float[] Values;

    public Req(Requirement req)
    {
        Key = req.Key;
        Ranged = req.Ranged;

        // make sure we have values available if it's Ranged
        while (req.Ranged && req.Values.Count < 2)
            req.Values.Add(0);

        Values = req.Values.ToArray();
    }
}

public readonly struct MatchCandidate
{
    public readonly TicketData? Ticket;
    public readonly float Rating;

    public MatchCandidate(TicketData ticket, float rating)
    {
        Ticket = ticket;
        Rating = rating;
    }

    // important for HashSet to hash based on associated Ticket and not this specific struct
    public override int GetHashCode() => Ticket!.GlobalId.GetHashCode();
}