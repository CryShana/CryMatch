using CryMatch.Matchmaker.Plugins;

using CryMatchGrpc;

using Google.Protobuf;

using System.Diagnostics;
using System.Threading.Channels;
using System.Collections.Concurrent;

using MM = (string id, CryMatch.Core.MatchmakerStatus status);

namespace CryMatch.Director;

public class DirectorManager : IDisposable
{
    readonly IState _state;
    readonly Configuration _config;
    readonly PluginLoader _plugins;
    readonly CancellationTokenSource _csc;

    /// <summary>
    /// Tickets that were part of invalid matches but are still valid and should be re-added on next consumed ticket clean.
    /// (Because that is where we get the ticket data from) [key is ticket global ID]
    /// </summary>
    readonly ConcurrentDictionary<string, Ticket?> _ticketsToReadd = new();

    /// <summary>
    /// Matches that were read and consumed by a consumer using the director service. These matches are later removed from state.
    /// </summary>
    readonly ConcurrentQueue<TicketMatch> _consumedMatches = new();

    /// <summary>
    /// Keeps track of latest online matchmakers
    /// </summary>
    readonly ConcurrentDictionary<string, MatchmakerStatus> _onlineMatchmakers = new();

    /// <summary>
    /// Tickets that were lost while being moved. Should be re-added to their target stream key when possible.
    /// This can happen when assigning tickets or unregistering a matchmaker.
    /// </summary>
    readonly ConcurrentQueue<(string target_stream_key, byte[][] data)> _lostTicketsWaitingForMove = new();

    /// <summary>
    /// Contains cleaning tasks for consumed tickets. These tasks start immediately and after some delay,
    /// clean the consumed ticket and remove themselves from this dictionary. [key is ticket state ID in consumed stream, value is if it was discarded yet]
    /// </summary>
    readonly ConcurrentDictionary<string, bool> _discardScheduledTickets = new();

    /// <summary>
    /// Cleaners determine which tickets to discard, these are put here. These should not be used anymore
    /// and should be deleted from submitted set, consumed stream and the cleaners dictionary
    /// </summary>
    readonly ConcurrentBag<Ticket> _discardedTickets = new();

    /// <summary>
    /// Channel is used to send matches to consumers as Director gets them and validates them
    /// </summary>
    readonly Channel<TicketMatch> _matchChannel = Channel.CreateUnbounded<TicketMatch>();
    public ChannelReader<TicketMatch> MatchesReader => _matchChannel.Reader;

    /// <summary>
    /// Tickets submitted by clients that need to be saved into state
    /// </summary>
    readonly ConcurrentQueue<(string gid, byte[] data)> _submittedTicketsPending = new();

    // state
    int _readers;
    int _ticketsReceived;
    int _ticketsSubmitted;
    int _ticketsRemoved;
    int _ticketsAssigned;

    /// <summary>
    /// Number of extra loops we can fit inside a single update delay in case
    /// we notice state is full and can immediately run again. This number dynamically 
    /// adjusts based on last loop time
    /// </summary>
    int _emergencyLoops;

    /// <summary>
    /// Max batch size when communicating with state. Too small increases overhead, too big increases
    /// computation and memory use, which makes Director slower to react. It also increases likelihood
    /// of Redis connection being dropped because of too much data! (2000 was too much for my local Redis)
    /// </summary>
    const int BATCH_LIMIT = 1000;

    #region Public properties
    /// <summary>
    /// Number of extra loops we can affort to fit inside a single update loop.
    /// This is used to make extra calls when we know state is full and needs emptying.
    /// </summary>
    public int AvailableEmergencyLoops => _emergencyLoops;
    /// <summary>
    /// Number of consumers waiting to read match data. As long as this number is
    /// above zero, we should keep trying to fetch data from database. Otherwise
    /// we don't need to. No point holding data in-memory when there are no consumers.
    /// </summary>
    public int ActiveReaders => _readers;
    /// <summary>
    /// Number of tickets received by director and were queued for submission, if not already submitted
    /// </summary>
    public int TicketsReceived => _ticketsReceived;
    /// <summary>
    /// Number of tickets that have been submitted to the director
    /// </summary>
    public int TicketsSubmitted => _ticketsSubmitted;
    /// <summary>
    /// Number of tickets that have been requested to be removed
    /// </summary>
    public int TicketsRemoved => _ticketsRemoved;
    /// <summary>
    /// Number of tickets that have been assigned to a matchmaker
    /// </summary>
    public int TicketsAssigned => _ticketsAssigned;
    /// <summary>
    /// Last stored matchmaker status (should not be older than director and matchmakers' update delay)
    /// </summary>
    public IReadOnlyDictionary<string, MatchmakerStatus> OnlineMatchmakers => _onlineMatchmakers;
    #endregion

    public DirectorManager(Configuration config, PluginLoader plugins, IState state)
    {
        _state = state;
        _config = config;
        _plugins = plugins;
        _csc = new();

        // check if there is any other director running
        var existing_director = _state.GetString(IState.DIRECTOR_ACTIVE_KEY).Result;
        if (!string.IsNullOrEmpty(existing_director))
        {
            // we gonna wait a bit and try again (because it could be from previous run)
            Thread.Sleep(TimeSpan.FromSeconds(_config.MaxDowntimeBeforeOffline));
            existing_director = _state.GetString(IState.DIRECTOR_ACTIVE_KEY).Result;

            if (!string.IsNullOrEmpty(existing_director))
            {
                throw new Exception($"Only 1 director can be active at a time. " +
                    $"Confirm that your state service has no '{IState.DIRECTOR_ACTIVE_KEY}' key set");
            }
        }

        _ = Start();
    }

    async Task Start()
    {
        var token = _csc.Token;
        var received_match_state_ids = new HashSet<string>();
        var consumed_match_state_ids = new HashSet<string>();

        _ = Pinger();
        _ = TicketSubmitter();
        _ = SubmittedTicketCleaner();

        var update_delay = _config.DirectorUpdateDelay;


        uint lost_counter = 0;

        const int LOOP_SIZE = 10;
        var loop_times = new double[LOOP_SIZE];
        var loop_index = 0;

        var sw = new Stopwatch();
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(update_delay));
        while (await timer.WaitForNextTickAsync(token))
        {
            var t1 = ProcessMatchmakers();
            var t2 = ProcessMatches(received_match_state_ids, consumed_match_state_ids);
            var t3 = CleanConsumedTickets();
            // lost tickets don't need to be processed every loop
            var t4 = lost_counter++ % 5 == 0 ? ProcessLostTickets() : Task.CompletedTask;

            try
            {
                sw.Restart();
                await Task.WhenAll(t1, t2, t3, t4);
                sw.Stop();

                var elapsed_seconds = sw.Elapsed.TotalSeconds;
                
                // log loop times
                loop_times[loop_index++ % LOOP_SIZE] = elapsed_seconds;
                if (loop_index > 100000) loop_index = LOOP_SIZE;

                var loop_time_max = 0.0;
                var loop_times_sum = 0.0;
                var filled_loop_size = loop_index < LOOP_SIZE ? loop_index : LOOP_SIZE;
                for (int i = 0; i < filled_loop_size; i++)
                {
                    var time = loop_times[i];
                    loop_times_sum += time;
                    if (loop_time_max < time)
                        loop_time_max = time;
                }

                // warn user if director is taking more than 70% of update delay time
                if (elapsed_seconds > update_delay * 0.7)
                {
                    _emergencyLoops = 0;
                    Log.Warning("[Director] Update loop took longer than expected: {time}sec (consider increasing director update delay, currently: {delay}sec)", sw.Elapsed.TotalSeconds, _config.DirectorUpdateDelay);
                }
                else
                {
                    var loop_average = loop_times_sum / LOOP_SIZE;
                    var extra_loops = (update_delay - loop_time_max) / loop_average;

                    // if less than 70% of delay time, we can assume at least 1 emergency loop
                    _emergencyLoops = Math.Max(1, (int)extra_loops);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Director] Unexpected error");
            }
        }
    }

    async Task Pinger()
    {
        var token = _csc.Token;
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(_config.DirectorUpdateDelay));

        do
        {
            await _state.SetString(IState.DIRECTOR_ACTIVE_KEY, "Active", TimeSpan.FromSeconds(_config.MaxDowntimeBeforeOffline));
        } while (await timer.WaitForNextTickAsync(token));
    }

    async Task ProcessMatchmakers()
    {
        // get all registered matchmakers (this includes both online and offline ones)
        var registered_matchmakers = await _state.GetSetValues(IState.SET_MATCHMAKERS);
        if (registered_matchmakers == null || registered_matchmakers.Length == 0)
            return;

        // check every registered matchmaker, remove offline and get status for online ones
        var online_matchmakers = new List<MM>();
        foreach (var mm_id in registered_matchmakers)
        {
            if (string.IsNullOrEmpty(mm_id))
            {
                Log.Warning("[Director] Empty or null name for matchmakers SET");
                continue;
            }

            var status_text = await _state.GetString(mm_id);
            if (string.IsNullOrEmpty(status_text))
            {
                Log.Information("[Director] Matchmaker {id} is offline", mm_id);
                await UnregisterMatchmaker(mm_id);
                continue;
            }

            var status = MatchmakerStatus.ToStatus(status_text);
            if (status == null)
            {
                Log.Information("[Director] Matchmaker {id} set invalid status: {status}", mm_id, status_text);
                await UnregisterMatchmaker(mm_id);
                continue;
            }

            if (!_onlineMatchmakers.ContainsKey(mm_id))
            {
                Log.Information("[Director] Matchmaker {id} is online", mm_id);
            }

            online_matchmakers.Add((mm_id, status));
            _onlineMatchmakers[mm_id] = status;
        }

        if (online_matchmakers.Count == 0)
            return;

        // NOTE: Because we are using a maximum batch size, there may be a burst of unassigned
        // tickets and because they are above the batch size, we won't be able to assign all of
        // them until the next director loop. If emergency loops are available, we can use them
        // here to do extra assignments
        int loop = -1;
        int assigned_tickets;
        do
        {
            loop++;

            try
            {
                assigned_tickets = await AssignTickets(online_matchmakers);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Director] Failed to assign tickets");
                break;
            }

        } while (assigned_tickets >= BATCH_LIMIT && loop < _emergencyLoops);
    }

    async Task ProcessMatches(HashSet<string> received_match_state_ids, HashSet<string> consumed_match_state_ids)
    {
        List<string> match_all_tickets = new();
        List<string> match_valid_tickets = new();
        List<string> match_invalid_tickets = new();

        // we only want to fetch matches if there are any readers available to actually consume these matches
        // otherwise we are wasting memory by loading all matches into memory
        if (_readers > 0)
        {
            // fetch all matches
            var matches = await _state.StreamRead(IState.STREAM_MATCHES, BATCH_LIMIT);
            if (matches == null || matches.Count == 0)
                return;

            foreach (var match_raw in matches)
            {
                TicketMatch match;
                try
                {
                    match = TicketMatch.Parser.ParseFrom(match_raw.data);
                    match.StateId = match_raw.id;
                }
                catch
                {
                    Log.Warning("[Director] Invalid gRPC match data: [{id}] ({size} bytes)", match_raw.id, match_raw.data.Length);
                    continue;
                }

                // we don't want to handle existing matches we already received previously
                if (!received_match_state_ids.Add(match.StateId))
                    continue;

                match_all_tickets.Clear();
                match_all_tickets.AddRange(match.MatchedTicketGlobalIds);

                match_valid_tickets.Clear();
                match_invalid_tickets.Clear();

                try
                {
                    // check if any of involved tickets is not part of the system

                    // this ensures only valid mathces are selected and ensures no duplicates!
                    // (in case of multiple matchmakers submitting their matches for same tickets)
                    var tickets_submitted = await _state.SetContainsBatch(IState.SET_TICKETS_SUBMITTED, match_all_tickets);
                    for (int i = 0; i < tickets_submitted.Length; i++)
                    {
                        var ticket_gid = match_all_tickets[i];
                        if (tickets_submitted[i])
                        {
                            match_valid_tickets.Add(ticket_gid);
                        }
                        else
                        {
                            match_invalid_tickets.Add(ticket_gid);
                        }
                    }

                    // if only a single involved ticket is invalid, the whole match is invalid
                    if (match_invalid_tickets.Count > 0)
                    {
                        // valid tickets should be re-added to unassigned pool, so we make note of them here
                        // (we can't read from CONSUMED stream yet because matchmaker may not have moved them all yet)
                        foreach (var i in match_valid_tickets)
                            _ticketsToReadd[i] = null;

                        // only invalid tickets should be removed from submitted set, the rest are re-added to unassigned stream
                        await _state.SetRemoveBatch(IState.SET_TICKETS_SUBMITTED, match_invalid_tickets);
                        Log.Information("[Director] Marking {count} tickets to be re-added into system. Removed {icount} invalid tickets.", match_valid_tickets.Count, match_invalid_tickets.Count);

                        // (yes, there is a danger of these re-added tickets being lost if Director goes offline in the next short moments, but honestly at that point we have bigger issues)
                    }
                    else
                    {
                        // match is valid, remove ALL tickets from submitted set because they are leaving the system
                        await _state.SetRemoveBatch(IState.SET_TICKETS_SUBMITTED, match_all_tickets);
                    }
                }
                catch (Exception ex)
                {
                    // ignore this match for now, it will be processed again in next update
                    received_match_state_ids.Remove(match_raw.id);

                    Log.Error(ex, "[Director] Failed process validity of match involved tickets (match {sid}), will try again", match.StateId);
                    continue;
                }

                // NOTE: assigned tickets are moved by matchmaker to the consumed stream
                // where the Director cleaner cleans them up. If any tickets need to be
                // re-added, it will be done there

                // NOTE: matches are always posted FIRST before consumed tickets are moved
                // by the matchmaker. This ensures the cleaner can't falsely remove tickets
                // that should be re-added becuase the Director hasn't marked them yet

                // push match to channel for readers
                await _matchChannel.Writer.WriteAsync(match);
            }
        }

        if (_consumedMatches.Count > 0)
        {
            // remove all consumed matches from state
            while (_consumedMatches.TryDequeue(out var consumed_match))
                consumed_match_state_ids.Add(consumed_match.StateId);

            try
            {
                await _state.StreamDeleteMessages(IState.STREAM_MATCHES, consumed_match_state_ids);

                // clear consumed messages from received matches
                foreach (var id in consumed_match_state_ids)
                    received_match_state_ids.Remove(id);

                // clear only at the end - in case anything fails earlier, we can retry again later
                consumed_match_state_ids.Clear();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Director] Failed to remove consumed match Ids");
            }
        }
    }

    async Task CleanConsumedTickets()
    {
        // consumed tickets are cleaned with a delay because it can take some time
        // for Director to process all the matches and accurately determine which
        // consumed tickets need to be re-added - it should not take more than certain
        // number of update loops to ensure all matches are processed properly
        const int MAX_UPDATE_LOOPS = 2;
        double DELETION_DELAY_SECONDS = _config.DirectorUpdateDelay * MAX_UPDATE_LOOPS; 

        // get up to BATCH_LIMIT_CAP of consumed tickets first
        var consumed_tickets = await _state.StreamRead(IState.STREAM_TICKETS_CONSUMED, BATCH_LIMIT);
        if (consumed_tickets == null || consumed_tickets.Count == 0) return;

        var to_readd_tickets = new List<Ticket>(_ticketsToReadd.Count);
        foreach (var ticket_raw in consumed_tickets)
        {      
            try
            {
                var ticket = Ticket.Parser.ParseFrom(ticket_raw.data);
                ticket.StateId = ticket_raw.id;

                var should_readd = _ticketsToReadd.ContainsKey(ticket.GlobalId);

                // check if this ticket is already scheduled for deletion
                var scheduled = _discardScheduledTickets.TryGetValue(ticket.StateId, out bool marked_for_discard);
         
                if (!should_readd)
                {
                    // if scheduled for deletion, but no re-adding planned, just ignore it for now. It will be deleted as scheduled.
                    if (scheduled) continue;
                    // if not yet scheduled for deletion, schedule it (this delay is needed to ensure re-add tickets get re-added if they are marked a bit later)
                    else
                    {
                        _discardScheduledTickets[ticket.StateId] = false;

                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(TimeSpan.FromSeconds(DELETION_DELAY_SECONDS));

                            // mark ticket to be discarded if it's still part of the dictionary
                            if (_discardScheduledTickets.TryGetValue(ticket.StateId, out var marked_for_discard) && !marked_for_discard)
                            {
                                _discardScheduledTickets[ticket.StateId] = true;
                                _discardedTickets.Add(ticket);
                            }
                            // if it's not, the deletion of it was cancelled
                            else
                            {
                                Log.Information("[Director] Canceled deletion for ticket {id} ({sid})", ticket.GlobalId, ticket.StateId);
                            }
                        });

                        continue;
                    }
                } 
                else
                {
                    // if ticket should be re-added but was already scheduled for deletion, try to cancel scheduled deletion
                    if (scheduled)
                    {
                        // we are already too late if planned for discard
                        if (marked_for_discard)
                        {
                            continue;
                        }

                        // try to cancel it
                        if (!_discardScheduledTickets.TryRemove(ticket.StateId, out marked_for_discard) || marked_for_discard)
                        {
                            // we failed to cancel it in time, it was discarded or is marked for discard
                            continue;
                        }
                    }

                    // re-add it
                    if (_ticketsToReadd.TryRemove(ticket.GlobalId, out _))
                    {
                        to_readd_tickets.Add(ticket);
                    } 
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Director] Cleaner failed to parse consumed ticket data for message id {sid}", ticket_raw.id);
            }
        }

        // handle tickets to be re-added
        if (to_readd_tickets.Count > 0)
        {
            // don't need to add to SUBMITTED set - their global IDs should already be there
            await _state.StreamAddBatch(IState.STREAM_TICKETS_UNASSIGNED, to_readd_tickets, x => x.ToByteArray());
            Log.Information("[Director] Re-added {count} tickets to unassigned", to_readd_tickets.Count);
        }

        // handle discarded tickets now
        if (_discardedTickets.Count > 0)
        {
            var count = 0;
            var maxcount = Math.Min(BATCH_LIMIT, _discardedTickets.Count);
            var to_remove_state_ids = new HashSet<string>(maxcount);
            var to_remove_global_ids = new List<string>(maxcount);
            while (_discardedTickets.TryTake(out var ticket) && count < maxcount)
                if (to_remove_state_ids.Add(ticket.StateId))
                {
                    to_remove_global_ids.Add(ticket.GlobalId);
                    count++;
                }

            // this only removes tickets that don't need to be re-added
            await _state.SetRemoveBatch(IState.SET_TICKETS_SUBMITTED, to_remove_global_ids);

            // this removes all processed consumed tickets from stream
            await _state.StreamDeleteMessages(IState.STREAM_TICKETS_CONSUMED, to_remove_state_ids);
            Log.Information("[Director] Cleaned up {count} discarded consumed tickets", to_remove_state_ids.Count);
             
            // remove these from scheduled discards
            foreach (var state_id in to_remove_state_ids)
                _discardScheduledTickets.TryRemove(state_id, out _);
        }
    }

    async Task ProcessLostTickets()
    {
        var count = _lostTicketsWaitingForMove.Count;
        if (count == 0) return;

        for (int i = 0; i < count; i++)
        {
            if (!_lostTicketsWaitingForMove.TryDequeue(out var lost))
                break;

            try
            {
                await _state.StreamAddBatch(lost.target_stream_key, lost.data);
                Log.Information("[Director] Lost tickets successfully re-added to stream {id}", lost.target_stream_key);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Director] Attempt to re-add lost tickets failed for stream {id}. Will try again later.", lost.target_stream_key);
                _lostTicketsWaitingForMove.Enqueue(lost);
            }
        }
    }

    async Task UnregisterMatchmaker(string mm_id)
    {
        _onlineMatchmakers.TryRemove(mm_id, out _);

        var stream_key = IState.STREAM_TICKETS_ASSIGNED_PREFIX + mm_id;
        while (true)
        {
            // we keep trying to get tickets until all are moved (limit batch size to keep it all reasonably sized)
            var assigned_tickets = await _state.StreamRead(stream_key, BATCH_LIMIT);
            if (assigned_tickets == null || assigned_tickets.Count == 0)
                break;

            // REMOVE them from stream first (to avoid duplication in case of later interruption)
            try
            {
                var to_remove = assigned_tickets.Select(x => x.id).ToHashSet();       
                var moved = await _state.StreamDeleteMessages(stream_key, to_remove);
                Log.Warning("[Director] Removed {count} assigned tickets from unregistered matchmaker", moved);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Director] Failed to remove tickets from stream {id}", stream_key);

                // we return to avoid freezing this loop - will be retried in next update loop
                // NOT BREAK, otherwise we will be deleting the relevant stream and set outside the loop
                return;
            }

            // ADD them to unassigned
            var to_add = assigned_tickets.Select(x => x.data).ToArray();
            try
            {
                await _state.StreamAddBatch(IState.STREAM_TICKETS_UNASSIGNED, to_add);
                Log.Warning("[Director] Moved {count} removed tickets to unassigned (unregistered matchmaker)", to_add.Length);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Director] Failed to move tickets to unassigned from stream {id}. Will attempt again later", stream_key);

                // we will attempt to add them again later
                _lostTicketsWaitingForMove.Enqueue((IState.STREAM_TICKETS_UNASSIGNED, to_add));

                // we return to avoid freezing this loop - will be retried in next update loop
                return;
            }
        }

        // at this point the matchmaker stream should be empty

        // we can remove stream and unregister the matchmaker 
        await _state.StreamDelete(stream_key);
        await _state.SetRemove(IState.SET_MATCHMAKERS, mm_id);
    }

    async Task<int> AssignTickets(List<MM> matchmakers)
    {
        // get all unassigned tickets
        var tickets = await _state.StreamRead(IState.STREAM_TICKETS_UNASSIGNED, BATCH_LIMIT);
        if (tickets == null || tickets.Count == 0) return 0;
        
        var capacity = _config.MatchmakerPoolCapacity;
        var expired = new HashSet<string>();
        var expired_gids = new List<string>();
        var assignments = new Dictionary<string, List<(string id, byte[] data)>>();

        // parse all tickets
        var parsed_tickets = new List<Ticket>(tickets.Count);
        foreach (var ticket_raw in tickets)
        {
            try
            {
                var ticket = Ticket.Parser.ParseFrom(ticket_raw.data);
                ticket.StateId = ticket_raw.id;

                parsed_tickets.Add(ticket);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Director] Failed to parse ticket while assigning (sid)", ticket_raw.id);
            }
        }
        // check with submitted set if all tickets are valid
        // (this has to be checked before each assignment because if a ticket is moved a lot, it's state Id changes,
        // it can't be removed via old state ID, and this results in more invalid matches that are then thrown away)
        var submitted_results = await _state.SetContainsBatch(IState.SET_TICKETS_SUBMITTED, parsed_tickets, static x => x.GlobalId);

        // prepare tickets for assignment
        for (int i = 0; i < parsed_tickets.Count; i++)
        {
            var ticket = parsed_tickets[i];
            var submitted = submitted_results[i];

            // ticket must be submitted in system
            if (!submitted)
            {
                // add to expired (no need to add to expired_gids, because ticket is not submitted anyway)
                expired.Add(ticket.StateId);
                continue;
            }

            // check if expired
            DateTime? timestamp = null;
            DateTime now = DateTime.UtcNow;
            if (ticket.MaxAgeSeconds > 0)
            {
                timestamp = DateTime.FromBinary(ticket.Timestamp);
                var age = now - timestamp.Value;
                if (age.TotalSeconds > ticket.MaxAgeSeconds)
                {
                    if (expired.Add(ticket.StateId))
                        expired_gids.Add(ticket.GlobalId);

                    continue;
                }
            }

            var pool_id = ticket.MatchmakingPoolId ?? "";

            // find matchmaker that is already handling this ticket pool and also is least busy
            MM picked_mm = default;
            MM least_busy_mm = default;

            ForPool(pool_id, matchmakers, ref least_busy_mm, (mm, pool_index) =>
            {
                var pool = mm.status.Pools![pool_index];

                // only pick a mm below capacity
                if (pool.in_queue < capacity)
                {
                    if (pool.gathering)
                    {
                        // if gathering, pick this one IMMEDIATELY
                        picked_mm = mm;
                        return true;
                    }
                    else if (pool.in_queue > 0)
                    {
                        // if not gathering, but queue is non-empty, pick this one, but wait for any possible better alternatives
                        picked_mm = mm;
                        return false;
                    }
                }

                return false;
            });

            // if no matchmakers picked, pick least busy one (even if over capacity)
            if (picked_mm == default)
            {
                picked_mm = least_busy_mm;
            }

            // prepare assignment list for given matchmaker
            var stream_key = IState.STREAM_TICKETS_ASSIGNED_PREFIX + picked_mm.id;
            if (!assignments.TryGetValue(stream_key, out var assign_list))
            {
                assign_list = new();
                assignments[stream_key] = assign_list;
            }

            if (timestamp.HasValue)
            {
                // by doing this, we don't need any external NTP servers for clock synchronization

                // 'timestamp' and 'now' are both set by this server, so we need
                // to consider the time difference between us and the matchmaker
                var time_difference = now - picked_mm.status!.LocalTimeUtc;

                // to calculate creation timestamp on 2nd device:
                // [ diff = now1 - now2 ] --> [ now2 = now1 - diff]   OR  [ timestamp2 = timestamp1 - diff ]
                var timestamp_compensated = timestamp.Value.Subtract(time_difference);

                // set expiry timestamp for the assigned matchmaker using it's own local time
                ticket.TimestampExpiryMatchmaker = timestamp_compensated.AddSeconds(ticket.MaxAgeSeconds).ToBinary();
            }

            var data = ticket.ToByteArray();

            // queue the ticket for assignment later on
            assign_list.Add((ticket.StateId, data));
        }

        // remove expired tickets
        if (expired.Count > 0)
        {
            await _state.SetRemoveBatch(IState.SET_TICKETS_SUBMITTED, expired_gids);
            await _state.StreamDeleteMessages(IState.STREAM_TICKETS_UNASSIGNED, expired);
            Log.Information("[Director] Removed {count} expired or invalid tickets from unassigned stream", expired.Count);    
        }

        // assign all tickets
        foreach (var stream_key in assignments.Keys)
        {
            var assign_list = assignments[stream_key];

            var to_remove = assign_list.Select(x => x.id).ToHashSet();
            var to_assign = assign_list.Select(x => x.data).ToArray();

            // REMOVE from unassigned first (to avoid duplication in case of later interruption)
            try
            {
                await _state.StreamDeleteMessages(IState.STREAM_TICKETS_UNASSIGNED, to_remove);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Director] Failed to remove tickets from unassigned stream to move to stream {id}", stream_key);
            }

            // ADD to the stream
            try
            {

                await _state.StreamAddBatch(stream_key, to_assign);
                Interlocked.Add(ref _ticketsAssigned, assign_list.Count);
            }
            catch (Exception ex)
            { 
                Log.Error(ex, "[Director] Failed to move tickets to stream {id}. Will attempt again later", stream_key);

                // we will attempt to add them again later
                _lostTicketsWaitingForMove.Enqueue((stream_key, to_assign));
            }
        }

        return tickets.Count;

        static void ForPool(string pool_id, List<MM> matchmakers, ref MM least_busy, Func<MM, int, bool> callback)
        {
            int index = -1;
            int tickets = int.MaxValue;

            for (int i = 0; i < matchmakers.Count; i++)
            {
                var mm = matchmakers[i];
                var status = mm.status;

                if (status.ProcessingTickets < tickets)
                {
                    index = i;
                    tickets = status.ProcessingTickets;
                }

                var decided = false;
                var pools = status.Pools;
                if (pools != null)
                    for (int j = 0; j < pools.Count; j++)
                        if (pools[j].name == pool_id)
                        {
                            if (callback(mm, j))
                                decided = true;

                            break;
                        }

                if (decided)
                    break;
            }

            least_busy = matchmakers[index];
        }
    }

    async Task SubmittedTicketCleaner()
    {
        // NOTE: this is responsible for ensuring submitted tickets set is clean
        // by removing tickets that have been there for too long. These orphans
        // can occur at any desync when communicating with the state

        var token = _csc.Token;
        var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

        do
        {
            // TODO: use SSCAN command for getting members of set iteratively - get a portion of them
            // TODO: keep this portion in-memory and check for expiry

        } while (await timer.WaitForNextTickAsync(token));
    }

    async Task TicketSubmitter()
    {
        var token = _csc.Token;
        var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        var state = _state;

        var process_immediately = false;
        var to_submit_data = new List<byte[]>(BATCH_LIMIT);
        var to_submit_gids = new List<string>(BATCH_LIMIT);
        var failed_tickets = new HashSet<string>();

        while (process_immediately || await timer.WaitForNextTickAsync(token))
        {
            if (_submittedTicketsPending.Count == 0)
            {
                process_immediately = false;
                continue;
            }

            to_submit_data.Clear();
            to_submit_gids.Clear();
            failed_tickets.Clear();

            var to_submit_count = Math.Min(_submittedTicketsPending.Count, BATCH_LIMIT);
            process_immediately = to_submit_count >= BATCH_LIMIT;

            while (_submittedTicketsPending.TryDequeue(out var data))
            {
                to_submit_data.Add(data.data);
                to_submit_gids.Add(data.gid);

                if (to_submit_data.Count >= to_submit_count)
                {
                    break;
                }
            }

            try
            {
                // NOTE: It's very rare for this to fail unless the state itself fails, in which case we have bigger
                // problems. Because it's so rare, it's not worth sacrificing performance and prolonging connections
                // in order to get the right status returned

                var submitted_state_ids = await state.StreamAddBatch(IState.STREAM_TICKETS_UNASSIGNED, to_submit_data);
                var submitted_gids = await state.SetAddBatch(IState.SET_TICKETS_SUBMITTED, to_submit_gids);

                // check if any errors
                for (int i = 0; i < submitted_state_ids.Length; i++)
                    if (string.IsNullOrEmpty(submitted_state_ids.Span[i]))
                        failed_tickets.Add(to_submit_gids[i]);

                for (int i = 0; i < submitted_gids.Length; i++)
                    if (!submitted_gids[i])
                        failed_tickets.Add(to_submit_gids[i]);

                var failed = failed_tickets.Count;
                var submitted = to_submit_data.Count - failed;
                Interlocked.Add(ref _ticketsSubmitted, submitted);

                if (failed > 0)
                {
                    // TODO: anything to do here with failed tickets? We could notify clients?
                    Log.Warning("[Director] {count} tickets failed to be submitted. This should not happen.", failed);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Director] Error while submitting {count} tickets", to_submit_data.Count);
            }
        }
    }

    public TicketStatus TicketSubmit(Ticket ticket)
    {
        // assign global id
        ticket.GlobalId = Guid.NewGuid().ToString();
        ticket.Timestamp = DateTime.UtcNow.ToBinary();

        var data = ticket.ToByteArray();
        _submittedTicketsPending.Enqueue((ticket.GlobalId, data));
        Interlocked.Increment(ref _ticketsReceived);

        return TicketStatus.Ok;
    }

    /// <summary>
    /// Will remove ticket from being matched.
    /// </summary>
    /// <param name="global_id">Ticket global ID (if state ID not provided, this will be used to invalidate a ticket later on)</param>
    public async Task<TicketStatus> TicketRemove(string global_id)
    {
        if (string.IsNullOrEmpty(global_id))
            return TicketStatus.BadRequest;

        try
        {
            // remove it from submitted set 
            var removed = await _state.SetRemove(IState.SET_TICKETS_SUBMITTED, global_id);

            if (removed) Interlocked.Increment(ref _ticketsRemoved);
            return removed ? TicketStatus.Ok : TicketStatus.NotFound;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Director] Failed to remove ticket ({gid})", global_id);
            return TicketStatus.InternalError;
        }
    }

    /// <summary>
    /// Register as a reader and wait for incoming matches. Matches returned are consumed
    /// and removed from state. No other reader will get this match.
    /// </summary>
    /// <param name="match_read_callback">Callback that will consume the match</param>
    public async Task ReadIncomingMatches(Func<TicketMatch, Task> match_read_callback, CancellationToken token = default)
    {
        RegisterReader();

        try
        {
            await foreach (var match in MatchesReader.ReadAllAsync(token))
            {
                try
                {
                    await match_read_callback(match);

                    // we need to consume the match after successful transfer to reader
                    // to remove it from state storage and any future processing, it is
                    // now someone else's problem
                    ConsumeMatch(match);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[Director] Failed to write match {gid} [State: {sid}] to response stream, returning to queue", match.GlobalId, match.StateId);

                    // put match back to be consumed by a different consumer
                    await ReturnMatch(match);
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            UnregisterReader();
        }
    }

    public void ConsumeMatch(TicketMatch match) => _consumedMatches.Enqueue(match);
    public ValueTask ReturnMatch(TicketMatch match) => _matchChannel.Writer.WriteAsync(match);

    void RegisterReader() => Interlocked.Increment(ref _readers);
    void UnregisterReader() => Interlocked.Decrement(ref _readers);

    public void Dispose()
    {
        _csc.Cancel();
    }
}
