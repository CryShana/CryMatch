using CryMatch.Matchmaker.Plugins;

using CryMatchGrpc;

using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace CryMatch.Matchmaker;

public partial class MatchmakerManager
{
    /// <summary>
    /// Min. count of tickets to activate parallel operation (it has some overhead so it's not recommended for lower ticket counts)
    /// <para>
    /// After profiling speeds on a 16-core CPU (3950X), 150 tickets and below were slower in parallel execution and under 
    /// 250 tickets had very minimal improvements. This is of course considering 16-cores.
    /// </para>
    /// <para>
    /// Even at 500 tickets the difference was only 10ms: 15ms for single and 5ms for multi threaded execution. 
    /// 3950X does not have great single core performance, so we can assume more performant cores will benefit 
    /// even less from parallelization at lower ticket counts. Because of this, I decided on the below threshold
    /// </para>
    /// </summary>
    const int MIN_FOR_PARALLEL = 1000;
    /// <summary>
    /// Max. size for reliable matching. This is when lists are used to track all candidates for all tickets.
    /// This is quite memory hungry, so it should not be used for too big numbers
    /// </summary>
    const int MAX_FOR_RELIABLE = 4000;

    #region Wrappers for matching
    /// <summary>
    /// Will attempt to match all given tickets and output the matches (UNRELIABLE + RELIABLE)
    /// </summary>
    /// <returns>True if it matched all that it could, False if it could not match all tickets and should try rematching again (there were too many victims)</returns>
    public static bool MatchFunction(
        ReadOnlyMemory<TicketData> tickets_mem, 
        List<TicketMatch> out_matches, 
        int match_size = 2, 
        bool unreliable_only = false, 
        Plugin? plugin = null)
    {
        // we want to cap it based on MAX RELIABLE limit - no point in gathering more
        var buffer3 = unreliable_only ? Array.Empty<TicketData>() : ArrayPool<TicketData>.Shared.Rent(MAX_FOR_RELIABLE);
        var tickets_victims = buffer3.AsMemory();

        try
        {
            // first match UNRELIABLY (very fast and memory efficient; handles 90%+ matches)
            int victims = 0;

            if (tickets_mem.Length < MIN_FOR_PARALLEL)
            {
                MatchFunctionUnreliable(tickets_mem, out_matches, tickets_victims.Span, out victims, match_size, plugin);
            }
            else
            {
                MatchFunctionUnreliableParallel(tickets_mem, out_matches, tickets_victims.Span, out victims, match_size, plugin);
            }

            // if there were more victims than we can reliably match, we failed those :(
            var failed_victims = victims > tickets_victims.Length + match_size;

            // there were victims (we possibly don't have all matches)
            if (victims >= match_size && !unreliable_only)
            {
                // let's try reliably matching them (using full candidate lists)
                var victim_data = tickets_victims.Slice(0, Math.Min(victims, tickets_victims.Length));
                MatchFunctionReliable(victim_data, out_matches, match_size, plugin);              
            }

            return !failed_victims;
        }
        finally
        {
            ArrayPool<TicketData>.Shared.Return(buffer3);
        }
    }

    /// <summary>
    /// Will attempt to match all given tickets and output the matches (UNRELIABLE)
    /// </summary>
    /// <param name="tickets_mem">All tickets that should be considered for matches</param>
    /// <param name="plugin">Responsible plugin for validating matches</param>
    /// <param name="out_matches">Output matches (there are no duplicate matches, each ticket belongs to a single match)</param>
    /// <param name="match_size">How many tickets should be matched together</param>
    /// <param name="victims_of_theft">
    /// Used to track tickets that had all of their match candidates stolen by other matches.
    /// These tickets should be re-matched as soon as possible, usually with RELIABLE = true.
    /// </param>
    /// <param name="victims">
    /// Number of victims (tickets that had all their candidates stolen and could possibly still be matched)
    /// (NOTE: This number can also be bigger than the victims_of_theft array size)
    /// </param>
    public static void MatchFunctionUnreliable(
        ReadOnlyMemory<TicketData> tickets_mem, 
        List<TicketMatch> out_matches, 
        Span<TicketData> victims_of_theft, 
        out int victims, 
        int match_size = 2, 
        Plugin? plugin = null)
    {
        var tickets = tickets_mem.Span;

        PreprocessData(tickets, static x => { }, 
            out var max_state_size,
            out var priority_span);

        FindCandidates(tickets, static (x, c) => x.AddCandidate(c),
            // from
            0,
            // to (should exclude last element)
            tickets.Length - 1,
            priority_span,
            ignore_usage: false);

        FindMatches(tickets, victims_of_theft, out_matches, match_size, max_state_size, 
            static x => x.Candidates.AsSpan(), plugin, out victims);
    }

    /// <summary>
    /// Does the unreliable matching in Parallel
    /// </summary>
    public static void MatchFunctionUnreliableParallel(
        ReadOnlyMemory<TicketData> tickets_mem, 
        List<TicketMatch> out_matches, 
        Span<TicketData> victims_of_theft, 
        out int victims, 
        int match_size = 2, 
        Plugin? plugin = null)
    {
        var tickets = tickets_mem.Span;

        PreprocessData(tickets, static x => { }, 
            out var max_state_size,
            out var priority_span);

        // -1 because last element does not need to check others
        var partitioner = Partitioner.Create(0, tickets.Length - 1);
        Parallel.ForEach(partitioner, range =>
        {
            FindCandidates(tickets_mem.Span, static (x, c) => x.AddCandidateThreadSafe(c),
                // from
                range.Item1,
                // to
                range.Item2,
                priority_span,
                ignore_usage: false);
        });

        FindMatches(tickets, victims_of_theft, out_matches, match_size, max_state_size, 
            static x => x.Candidates.AsSpan(), plugin, out victims);
    }

    /// <summary>
    /// Will attempt to match all given tickets and output the matches (RELIABLE)
    /// </summary>
    public static void MatchFunctionReliable(
        ReadOnlyMemory<TicketData> tickets_mem,
        List<TicketMatch> out_matches, 
        int match_size = 2, 
        Plugin? plugin = null)
    {
        var tickets = tickets_mem.Span;
        var rng = new Random();

        PreprocessData(tickets, static x => x.CandidatesList = new List<MatchCandidate>(), 
            out var max_state_size,
            out var priority_span);

        FindCandidates(tickets, static (x, c) => x.AddCandidateToList(c),
            // from
            0,
            // to (should exclude last element)
            tickets.Length - 1,
            priority_span,
            ignore_usage: true);

        FindMatches(tickets, Span<TicketData>.Empty, out_matches, match_size, max_state_size, 
            static x => CollectionsMarshal.AsSpan(x.CandidatesList), plugin, 
            out _); // -> reliable matching can not have victims because it uses full candidate lists
    }
    #endregion

    #region Main Matching Logic
    delegate ReadOnlySpan<MatchCandidate> GetCandidatesFunction(TicketData ticket);
    delegate void AddCandidateFunction(TicketData ticket, MatchCandidate candidate);
    delegate void OnPreprocessFunction(TicketData ticket);

    static void PreprocessData(
        ReadOnlySpan<TicketData> tickets, 
        OnPreprocessFunction on_ticket,
        out int max_state_size,
        out float priority_span)
    {
        var max_expire_time = long.MinValue;
        var min_expire_time = long.MaxValue;
        max_state_size = 0;

        for (int i = 0; i < tickets.Length; i++)
        {
            var ticket = tickets[i];
            on_ticket(ticket);

            // log min and max expiry to determine span of time of all tickets
            // and then determine priority values to assign to older tickets
            var expiry = ticket.FullTicket.TimestampExpiryMatchmaker;
            if (expiry > max_expire_time) max_expire_time = expiry;
            if (expiry < min_expire_time) min_expire_time = expiry;

            var candidates_state_size = ticket.State.Length;
            if (candidates_state_size > max_state_size)
                max_state_size = candidates_state_size;
        }

        // prepare values for calculating normalized age
        // NOTE: multiplication is cheaper than division, so we save the inverted value for later
        var expire_range = max_expire_time - min_expire_time;
        var expire_range_inv = expire_range == 0 ? 0 : (1.0 / expire_range);

        // calculate base priority based on these factors
        var min_priority = float.MaxValue;
        var max_priority = float.MinValue;
        for (int i = 0; i < tickets.Length; i++)
        {
            var t = tickets[i];
            var p = Priority(t.FullTicket, min_expire_time, expire_range_inv);
            t.CalculatedBasePriority = p;

            if (p < min_priority) min_priority = p;
            if (p > max_priority) max_priority = p;
        }

        // this span is used to determine random noise span
        priority_span = Math.Abs(max_priority - min_priority);
    }

    static void FindCandidates(ReadOnlySpan<TicketData> tickets, AddCandidateFunction add_candidate, int from, int to, float priority_span, bool ignore_usage)
    {
        // NOTE: Random is only used here. But also this is to ensure that
        // I don't make the same mistake of creating it outside and it causing
        // bottlenecks in Parallel execution because of locking
        var rng = new Random();

        int CANDIDATE_SIZE = tickets.Length > 0 ? tickets[0].Candidates.Length : 8;

        // IMPORTANT WHEN CANDIDATE LIST LIMITED:
        // add some noise to match ratings to avoid tickets having same list candidates when scores are similar
        // (should be small enough to not affect priority scores and big enough to make a difference)
        // (non-zero values are sufficient to fix issue of identical priorities, but too small values are worse at disimilar priorities, which are more common)
        float RATING_NOISE_RANGE = Math.Max(0.001f, priority_span * 0.05f); // 5% of priority span

        // IMPORTANT WHEN CANDIDATE LIST LIMITED:
        // tickets used by many other as candidates should be ignored at some point
        // because there is an extremely high chance they will be already matched
        // (alleviates the issue where vastly-differently prioritized tickets result in a lot of tickets not matching
        // because they all pick the best prioritized tickets which are already used up)

        // NOTE: should not be too low number to avoid too many tickets ignoring it in
        // the beginning of processing (as it gets eventually pushed down everyone's lists)
        int USAGE_TO_IGNORE = CANDIDATE_SIZE * 3;

        // it has to go 'till the end
        var to2 = tickets.Length;

        for (int i = from; i < to; i++)
        {
            var a = tickets[i];
            var a_state = a.State.AsSpan();
            var a_affinities = a.Affinities.AsSpan();

            for (int j = i + 1; j < to2; j++)
            {
                var b = tickets[j];

                if (!ignore_usage && b.CandidateUsageBy > USAGE_TO_IGNORE)
                    continue;

                if (!CheckRequirements(a, a_state, b))
                    continue;

                var b_affinities = b.Affinities.AsSpan();
                if (!CheckAffinities(a_affinities, b_affinities,
                    out var priority_for_a,
                    out var priority_for_b))
                    continue;

                var base_rating = (float)rng.NextDouble() * RATING_NOISE_RANGE;

                var rating_for_a = base_rating + b.CalculatedBasePriority + priority_for_a;
                var rating_for_b = base_rating + a.CalculatedBasePriority + priority_for_b;

                add_candidate(a, new MatchCandidate(b, rating_for_a));
                add_candidate(b, new MatchCandidate(a, rating_for_b));
            }
        }  
    }

    static void FindMatches(
        ReadOnlySpan<TicketData> tickets, 
        Span<TicketData> victims_of_theft,
        List<TicketMatch> out_matches, 
        int match_size,
        int max_state_size,
        GetCandidatesFunction get_candidates, 
        Plugin? plugin, 
        out int victims)
    {
        var vcount = victims_of_theft.Length;
        var vindex = 0;
        victims = 0;

        var count = tickets.Length;
        var consumed_tickets = new HashSet<string>(count);

        var other_size = match_size - 1;
        var other_tickets = new TicketData[other_size];

        // if plugin will be overriding candidate picking process,
        // we need to prepare some extra stuff for passing data
        var picked_candidates = Array.Empty<int>();
        var gc_handles = Array.Empty<GCHandle[]>();
        var candidates_native = Array.Empty<MatchCandidateNative>();
        var candidates_native_original = Array.Empty<TicketData>();
        var plugin_override = plugin != null && plugin.OverrideCandidatePicking();
        if (plugin_override)
        {
            // we want to allocate only once and then reuse this native memory
            var max_candidates_size = MaxCandidatesSize(tickets, get_candidates);

            picked_candidates = new int[match_size - 1];
            candidates_native = new MatchCandidateNative[max_candidates_size + 1]; // +1 for owning ticket
            candidates_native_original = new TicketData[candidates_native.Length];
            gc_handles = new GCHandle[candidates_native.Length][];

            for (int i = 0; i < candidates_native.Length; i++)
            {
                candidates_native[i] = new MatchCandidateNative(max_state_size);
            }
        }

        // start processing tickets
        for (int i = 0; i < count; i++)
        {
            var ticket = tickets[i];

            // consume this ticket right away as we don't want to
            // process it again anywhere else
            if (!consumed_tickets.Add(ticket.GlobalId)) continue;

            // try to create match from best rated candidates, going from highest to lowest rated candidate
            var match_index = 0;
            var candidates_stolen = 0;
            var candidates = get_candidates(ticket);

            // use plugin if it wants to override the picking process
            if (plugin_override)
            {
                // gc handles are created inside, we need to free them outside
                PickPluginCandidates(plugin!, candidates, other_tickets, consumed_tickets, 
                    candidates_native, candidates_native_original, picked_candidates, ticket, 
                    gc_handles, match_size, ref candidates_stolen, ref match_index);
                
                // free all handles
                for (int j = 0; j < candidates_native.Length; j++)
                {
                    ref var handles = ref gc_handles[j];
                    if (handles == null) continue;

                    for (int k = 0; k < handles.Length; k++)
                        handles[k].Free();

                    handles = null;
                }
            }
            else
            {
                PickBestRatedCandidates(candidates, other_tickets, consumed_tickets, ref candidates_stolen, ref match_index);
            }

            // no candidates left to fill out the wanted match size
            if (match_index < other_size)
            {
                // remove all involved tickets from consumed (they are still useful for others)
                for (int k = 0; k < match_index; k++)
                    consumed_tickets.Remove(other_tickets[k].GlobalId);
                
                // if all candidates for a match were stolen from us, we are victims
                // of theft; we can possibly still match with others, so we need to log this
                if (candidates_stolen > match_size - 1)
                {
                    victims++;

                    if (vindex < vcount)
                    {
                        victims_of_theft[vindex++] = ticket;
                    }
                }

                continue;
            }

            // create match
            var match = new TicketMatch();
            match.GlobalId = Guid.NewGuid().ToString();
            match.MatchedTicketGlobalIds.Add(ticket.GlobalId);

            for (int k = 0; k < match_index; k++)
                match.MatchedTicketGlobalIds.Add(other_tickets[k].GlobalId);
            
            out_matches.Add(match);
        }

        static void PickBestRatedCandidates(
            ReadOnlySpan<MatchCandidate> candidates, 
            Span<TicketData> picked_tickets, 
            HashSet<string> consumed_tickets, 
            ref int candidates_stolen, 
            ref int match_index)
        {
            var picked_size = picked_tickets.Length;

            // we just pick candidates from best rated to
            // worst rated until match size is satisfied
            for (int j = 0; j < candidates.Length; j++)
            {
                // we want to use the first viable candidate
                var viable = candidates[j].Ticket;
                if (viable == null) break;

                // we consume it automatically, but if it's already present,
                // it was consumed by another match, and we can't use it
                if (!consumed_tickets.Add(viable.GlobalId))
                {
                    candidates_stolen++;
                    continue;
                }

                picked_tickets[match_index++] = viable;
                if (match_index == picked_size) break;
            }
        }

        static void PickPluginCandidates(Plugin plugin, 
            ReadOnlySpan<MatchCandidate> candidates, 
            Span<TicketData> picked_tickets,
            HashSet<string> consumed_tickets, 
            Span<MatchCandidateNative> candidates_native, 
            Span<TicketData> candidates_native_original,
            Span<int> picked_candidates_native, 
            TicketData owner_ticket, 
            GCHandle[][] handles, 
            int match_size, 
            ref int candidates_stolen, 
            ref int match_index)
        {
            match_index = 0;

            var picked_size = match_size - 1;

            // first candidate is the owning ticket (this one can't be changed)
            var candidates_valid = 1;
            handles[0] = new GCHandle[owner_ticket.State.Length];
            candidates_native[0].SetDataTo(new MatchCandidate(owner_ticket, 0), ref handles[0]);
            candidates_native_original[0] = owner_ticket;

            // move candidates to native structs that will be passed to plugin
            for (int j = 1; j < candidates_native.Length; j++)
            {
                // [candidates] array is either bigger or same-sized as match_candidates_native, never smaller
                // we subtract -1 because owner is not included in [candidates]
                var candidate = candidates[j - 1];
                if (candidate.Ticket == null) continue;

                // check if it was consumed by another match, we can't use it
                // (we don't consume it yet, because we need to process all candidates)
                if (consumed_tickets.Contains(candidate.Ticket.GlobalId))
                {
                    candidates_stolen++;
                    continue;
                }

                var index = candidates_valid++;
                handles[index] = new GCHandle[candidate.Ticket.State.Length];
                candidates_native[index].SetDataTo(candidate, ref handles[index]);
                candidates_native_original[index] = candidate.Ticket;

                // by default we set picked indexes to first best rated candidates
                // this can either be left alone by the plugin or adjusted
                var picked_index = index - 1;
                if (picked_index < picked_candidates_native.Length)
                {
                    // the picked index must be identical to one in [match_candidates_native]
                    picked_candidates_native[picked_index] = index;
                }
            }

            // if not enough candidates collected,
            // we can't make a valid match, cancel
            if (candidates_valid < match_size)
                return;

            // let plugin pick the candidates
            if (!plugin.PickMatchCandidates(candidates_native.Slice(0, candidates_valid), picked_candidates_native))
                return;

            // process picked candidates
            for (int j = 0; j < picked_candidates_native.Length; j++)
            {
                var picked_index = picked_candidates_native[j];

                // 0 should not be picked, that is the owner!
                if (picked_index < 1 || picked_index >= candidates_valid)
                    continue;

                var original_ticket = candidates_native_original[picked_index];

                // check if already used
                if (!consumed_tickets.Add(original_ticket.GlobalId))
                {
                    // duplicate was provided!
                    // (it can't have been consumed elsewhere because
                    // we checked before candidates were passed in)           
                    return;
                }

                picked_tickets[match_index++] = original_ticket;
                if (match_index == picked_size) break;
            }
        }
    }

    static bool CheckRequirements(TicketData a, ReadOnlySpan<float[]> a_state, TicketData b)
    {
        var b_state = b.State;

        foreach (var a_req in a.Requirements)
            if (!FitsRequirements(b_state, a_req))
                return false;

        foreach (var b_req in b.Requirements)
            if (!FitsRequirements(a_state, b_req))
                return false;

        return true;
    }

    static bool CheckAffinities(ReadOnlySpan<TicketAffinity> a, ReadOnlySpan<TicketAffinity> b,
        out float added_priority_for_a, out float added_priority_for_b)
    {
        added_priority_for_a = 0;
        added_priority_for_b = 0;

        // take minimum count and compare those
        var count = a.Length > b.Length ? b.Length : a.Length;
        for (int i = 0; i < count; i++)
        {
            var a_affinity = a[i];
            var b_affinity = b[i];

            // by default, bigger differences will have normalize value closer to 1 
            // this means disimilar affinities have higher priorities by default
            var difference = Math.Abs(a_affinity.Value - b_affinity.Value);
            var normalized_a = Math.Clamp(difference * a_affinity.MaxMarginInverted, 0, 1);
            var normalized_b = Math.Clamp(difference * b_affinity.MaxMarginInverted, 0, 1);

            // if you instead prefer similarity, we just flip the normalized values
            if (!a_affinity.PreferDisimilar)
                normalized_a = 1 - normalized_a;

            if (!b_affinity.PreferDisimilar)
                normalized_b = 1 - normalized_b;

            // if we have a hard margin and normalized value is 0,
            // it is outside the acceptable range and we can't match
            if (!a_affinity.SoftMargin && normalized_a == 0)
                return false;

            if (!b_affinity.SoftMargin && normalized_b == 0)
                return false;

            added_priority_for_a += normalized_a * a_affinity.PriorityFactor;
            added_priority_for_b += normalized_b * b_affinity.PriorityFactor;
        }

        return true;
    }

    static bool FitsRequirements(ReadOnlySpan<float[]> state, TicketRequirements requirements)
    {
        var reqs = requirements.Any.AsSpan();
        var length = reqs.Length;
        if (length == 0) return true;

        // check ANY = only one successful requirement is required to succeed
        foreach (var req in reqs)
        {
            var state_values = state[req.Key];
            if (req.Ranged)
            {
                if (state_values.Length == 0)
                {
                    continue;
                }

                var val = state_values[0];

                // TicketData ensures there's always 2 values available for Ranged
                // (allows us to skip a check)
                if (val >= req.Values[0] && val <= req.Values[1])
                {
                    return true;
                }
            }
            else
            {
                foreach (var v in req.Values)
                {
                    foreach (var s in state_values)
                    {
                        if (s == v)
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Values closer to 1 are older and need greater priority
    /// </summary>
    static double AgeNormalized(long expires_at, long min_expire_time, double expire_range_inv) => 1 - (expires_at - min_expire_time) * expire_range_inv;
    
    /// <summary>
    /// Get ticket base priority + age priority
    /// </summary>
    static float Priority(Ticket t, long min_expire_time, double expire_range_inv)
    {
        var age_priority = AgeNormalized(t.TimestampExpiryMatchmaker, min_expire_time, expire_range_inv) * t.AgePriorityFactor;
        var priority = t.PriorityBase + age_priority;

        return (float)priority;
    }

    static int MaxCandidatesSize(ReadOnlySpan<TicketData> tickets, GetCandidatesFunction get_candidates)
    {
        var max_candidates_size = 0;
        for (int i = 0; i < tickets.Length; i++)
        {
            var candidates = get_candidates(tickets[i]);
            if (candidates.Length > max_candidates_size)
                max_candidates_size = candidates.Length;
        }
        return max_candidates_size;
    }
    #endregion

    public static int PreferredCandidatesSizeFor(int match_size)
    {
        var candidates_required = match_size - 1;

        // after testing different match sizes for candidate sizes from 1 to 100
        // multiplying (match_size - 1) by 8 and using that for the size, consistently
        // resulted in ~92% results matched. Scaled consistently from match sizes 1 to 10.

        // going for more than 92% had noticable performance degradations, and going below
        // did not gain us much performance improvements while more quickly losing matching
        // % efficiency

        return candidates_required * 8;
    }
}
