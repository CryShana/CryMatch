using CryMatch.Matchmaker.Plugins;

using CryMatchGrpc;

using Google.Protobuf;

using System.Buffers;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace CryMatch.Matchmaker;

public partial class MatchmakerManager : IDisposable
{
    readonly string _id;
    readonly IState _state;
    readonly Thread[] _threads;
    readonly Configuration _config;
    readonly PluginLoader _plugins;

    /// <summary>
    /// Tickets consumed (either expired or used) [items are ticket global IDs]
    /// </summary>
    readonly ConcurrentQueue<(Ticket ticket, bool consumed_for_match)> _consumedTickets = new();
    /// <summary>
    /// Tickets fetched from state, not yet consumed [keys are ticket global IDs]
    /// </summary>
    readonly ConcurrentDictionary<string, Ticket> _assignedTickets = new();
    /// <summary>
    /// Ticket pools
    /// </summary>
    readonly ConcurrentDictionary<string, TicketPool> _pools = new();
    /// <summary>
    /// Copy of pool IDs separate from <see cref="_pools"/> to allow for quick indexing and no copying when checking available keys
    /// </summary>
    readonly List<string> _poolIds = new();
    readonly CancellationTokenSource _csc;

    // state
    int _processingTickets = 0;
    int _receivedTickets = 0;

    /// <summary>
    ///  Max batch size when communicating with state. Too small increases overhead, too big increases
    /// computation and memory use. It also increases likelihood of Redis connection being dropped 
    /// because of too much data! (2000 was too much for my local Redis)
    /// </summary>
    const int BATCH_LIMIT = 1000;

    #region Public properties
    public string Id => _id;
    /// <summary>
    /// Number of total tickets currently being processed across all ticket pools 
    /// (processed tickets are excluded from this count)
    /// </summary>
    public int ProcessingTickets => _processingTickets;
    /// <summary>
    /// Number of total tickets assigned to this matchmaker across all ticket pools 
    /// (includes also processed tickets)
    /// </summary>
    public int ReceivedTickets => _receivedTickets;
    /// <summary>
    /// Currently available ticket pools
    /// </summary>
    public IReadOnlyList<string> Pools => _poolIds;
    #endregion

    public MatchmakerManager(Configuration config, PluginLoader plugins, IState state)
    {
        _id = $"mm_{Guid.NewGuid()}";
        _state = state;
        _config = config;
        _plugins = plugins;
        _threads = new Thread[config.ParsedMatchmakerThreads];

        _csc = new();

        // set up threads
        for (int i = 0; i < _threads.Length; i++)
        {
            _threads[i] = new Thread(ThreadStart) { IsBackground = true };
            _threads[i].Start();
        }

        if (_threads.Length > 1)
        {
            Log.Information("[Matchmaker] Using {threads} threads for parallel processing", _threads.Length);
        }
        else
        {
            Log.Information("[Matchmaker] Using 1 thread for processing");
        }

        // pinger takes care of matchmaker status in state
        _ = Pinger();

        // this just makes sure pool configurations are up-to-date
        _ = PoolConfigurationFetcher();

        // will fetch tickets in background and assign them to threads 

        // (delay is required to avoid scenario where matchmaker immediately
        // fetches tickets before Director realizes it's registered and unassigns
        // those same tickets at the same time - only happens if matchmaker goes
        // offline briefly or Director comes online at the same time)
        _ = Task.Delay(TimeSpan.FromSeconds(_config.MatchmakerUpdateDelay * 4))
            .ContinueWith(_ => _ = TicketFetcher());

        // will delete tickets that were either used up or expired
        _ = TicketCleaner();
    }

    async Task Pinger()
    {
        var token = _csc.Token;

        var sw = new Stopwatch();
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(_config.MatchmakerUpdateDelay));
        do
        {
            sw.Restart();

            // status is always set first before registration (to ensure Director doesn't think a registered matchmaker is offline)
            await SetStatus();
            await _state.SetAdd(IState.SET_MATCHMAKERS, _id);

            sw.Stop();

            // warn user if pinger is taking more than 90% of update delay time
            if (sw.Elapsed.TotalSeconds > _config.MatchmakerUpdateDelay * 0.9)
            {
                Log.Warning("[Matchmaker] Pinger loop took longer than expected: {time}sec (consider increasing matchmaker update delay, currently: {delay}sec)", sw.Elapsed.TotalSeconds, _config.MatchmakerUpdateDelay);
            }
        } while (await timer.WaitForNextTickAsync(token));
    }

    async Task SetStatus()
    {
        var pools = _pools.Select(x => (x.Key, x.Value.TicketCount, x.Value.Gathering)).ToList();
        var status_text = MatchmakerStatus.FromStatus(new()
        {
            ProcessingTickets = _processingTickets,
            Pools = pools,
            LocalTimeUtc = DateTime.UtcNow
        });

        var max_lifespan = TimeSpan.FromSeconds(_config.MaxDowntimeBeforeOffline);
        await _state.SetString(_id, status_text, max_lifespan);
    }

    async Task TicketFetcher()
    {
        var token = _csc.Token;
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(_config.MatchmakerUpdateDelay));

        var stream_key = IState.STREAM_TICKETS_ASSIGNED_PREFIX + _id;

        while (await timer.WaitForNextTickAsync(token))
        {
            var assigned_tickets = await _state.StreamRead(stream_key);
            if (assigned_tickets == null || assigned_tickets.Count == 0)
                continue;

            var added = 0;
            foreach (var raw_ticket in assigned_tickets)
            {
                Ticket ticket;
                try
                {
                    ticket = Ticket.Parser.ParseFrom(raw_ticket.data);
                    ticket.StateId = raw_ticket.id;
                }
                catch
                {
                    Log.Warning("[Matchmaker] Received invalid assigned ticket (message id: {id})", raw_ticket.id);
                    continue;
                }

                // register ticket (so we can avoid existing ones)
                // (global ID is used to ignore tickets that were moved in the state and had their state ID changed)
                if (!_assignedTickets.TryAdd(ticket.GlobalId, ticket))
                {
                    // ignore existing tickets
                    continue;
                }

                // separate tickets into pools
                var pool_id = ticket.MatchmakingPoolId ?? "";
                if (!_pools.TryGetValue(pool_id, out var pool))
                {
                    var plugin = GetPluginForPool(pool_id);

                    pool = new()
                    {
                        PoolId = pool_id,
                        ResponsiblePlugin = plugin
                    };

                    _pools[pool_id] = pool;
                    _poolIds.Add(pool_id);

                    // immediately fetch latest pool configuration from state
                    await UpdatePoolConfiguration(pool);

                    if (plugin != null)
                    {
                        Log.Information("[Matchmaker] New pool created ('{id}'), responsible plugin: '{name}'", pool_id, plugin.Name());
                    }
                    else
                    {
                        Log.Information("[Matchmaker] New pool created ('{id}'), no responsible plugin", pool_id);
                    }
                }

                pool.Queue.Enqueue(ticket);

                added++;
                Interlocked.Increment(ref _receivedTickets);
            }

            if (added > 0)
            {
                var total = Interlocked.Add(ref _processingTickets, added);
                Log.Information("[Matchmaker] Received {count} assigned tickets (Total: {total})", added, total);
            }
        }
    }

    async Task TicketCleaner()
    {
        var token = _csc.Token;
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(0.5));

        var to_delete_state_ids = new HashSet<string>();
        var to_delete_tickets = new List<(Ticket ticket, bool consumed_for_match)>();

        while (await timer.WaitForNextTickAsync(token))
        {
            do
            {
                // remove items per batch, we don't want too big requests
                int count = 0;
                to_delete_tickets.Clear();
                to_delete_state_ids.Clear();

                // gather up all consumed tickets
                while (_consumedTickets.TryDequeue(out var consumed))
                {
                    if (to_delete_state_ids.Add(consumed.ticket.StateId))
                    {
                        to_delete_tickets.Add(consumed);
                        count++;
                    }

                    if (count >= BATCH_LIMIT)
                        break;
                }

                if (to_delete_state_ids.Count == 0)
                    break;

                try
                {
                    // delete from own stream first
                    var stream_key = IState.STREAM_TICKETS_ASSIGNED_PREFIX + Id;
                    await _state.StreamDeleteMessages(stream_key, to_delete_state_ids);

                    // add to CONSUMED TICKETS stream
                    await _state.StreamAddBatch(IState.STREAM_TICKETS_CONSUMED, to_delete_tickets, x => x.ticket.ToByteArray());

                    Log.Information("[Matchmaker] Marked {count} tickets as completed", count);
                }
                catch (Exception ex)
                {
                    // re-add to queue
                    foreach (var t in to_delete_tickets)
                        _consumedTickets.Enqueue(t);

                    // NOTE: these tickets could get lost if removed from
                    // matchmaker stream but never submitted to consumed
                    // stream and matchmaker goes offline

                    Log.Error(ex, "[Matchmaker] Failed to clean {count} tickets", count);
                }

                // to avoid the edge case of ticket fetcher getting tickets as they
                // are being removed and processing them AGAIN because we removed them
                // from assigned tickets too soon. I prefer this than using a semaphore
                // because we don't want to delay the fetcher - other unused tickets that
                // are fetched alongside deleted ones can still be used
                await Task.Delay(100);

                // state no longer contains these tickets,
                // we can safely remove them from assigned tickets
                foreach (var key in to_delete_state_ids)
                {
                    _assignedTickets.TryRemove(key, out _);
                }

                // if there is still a lot of consumed tickets, loop immediately
                // and handle them now before they start accumulating too much
            } while (_consumedTickets.Count > BATCH_LIMIT);
        }
    }

    async Task PoolConfigurationFetcher()
    {
        var token = _csc.Token;

        // this updater makes sure to download latest pool configuration from state
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));

        do
        {
            foreach (var pid in _poolIds)
            {
                if (!_pools.TryGetValue(pid, out var pool)) 
                    continue;

                await UpdatePoolConfiguration(pool);
            }

        } while (await timer.WaitForNextTickAsync(token));
    }

    async Task UpdatePoolConfiguration(TicketPool pool)
    {
        // MATCH SIZE
        var match_size = await _state.GetString(IState.POOL_MATCH_SIZE_PREFIX + pool.PoolId);
        if (!string.IsNullOrEmpty(match_size) &&
            int.TryParse(match_size, out var parsed_match_size) &&
            parsed_match_size > 2)
        {
            var val = (ushort)parsed_match_size;
            if (pool.LastMatchSize != val)
            {
                Log.Information("[Matchmaker] Updated match size for pool {pool_id} from {old} to {new}", 
                    pool.PoolId, pool.LastMatchSize, val);
                pool.LastMatchSize = val;
            }
        }
    }

    Plugin? GetPluginForPool(string pool_id)
    {
        var plugins = _plugins.Plugins;
        if (plugins == null) return null;

        Plugin? default_plugin = null;

        // go through all plugins and take first one responsible for this ticket pool
        foreach (var p in plugins)
        {
            var ticket_pool = p.HandledTicketPool();
            if (ticket_pool == pool_id) return p;

            // if any plugin returns empty string, that's a catch-all and will be accepted
            // if no other matches are found that exactly match the ticket pool name
            if (default_plugin == null &&
                ticket_pool == "")
            {
                default_plugin = p;
            }
        }

        return default_plugin;
    }

    void ThreadStart()
    {
        // each thread handles a separate pool. But in case there are less threads than pools,
        // we need to keep incrementing which pools are picked, so we don't get stuck on just
        // the first [threads] pools
        int pool_id_index = -1;

        var capacity = _config.MatchmakerPoolCapacity;
        var max_failures = _config.MaxMatchFailures;
        var min_gather_time = TimeSpan.FromSeconds(_config.MatchmakerMinGatherTime);
        
        var token = _csc.Token;
        while (!token.IsCancellationRequested)
        {
            // try to select next pool, this will lock it for this thread if possible
            TicketPool? pool = SelectNextPool(ref pool_id_index);
            if (pool == null)
            {
                Thread.Sleep(50);
                continue;
            }

            // only gather if below capacity, otherwise we can just
            // immediately start matching as we have enough tickets
            // (skip gathering if pool contains failed victims)
            if (pool.TicketCount < capacity && !pool.HasFailedVictims)
            {
                pool.Gathering = true;
                Thread.Sleep(min_gather_time);
                pool.Gathering = false;

                // wait a bit more to get all assigned tickets as
                // the gathering status hasn't been propagated yet
                Thread.Sleep(TimeSpan.FromSeconds(_config.MatchmakerUpdateDelay * 2));
            }

            pool.HasFailedVictims = false;

            // make snapshot of count (we don't want to do it in a loop,
            // because we would have an infinite loop if tickets kept on
            // coming without stopping) - but also we can use ArrayPool with fixed data
            var count = Math.Min(capacity, pool.TicketCount);
            // ----------------------------------------------------

            // determine match specifications
            var responsible_plugin = pool.ResponsiblePlugin;
            if (responsible_plugin != null)
            {
                // only override value if it's valid
                var plugin_match_size = responsible_plugin.MatchSize(count);
                if (plugin_match_size >= 2)
                {
                    pool.LastMatchSize = (ushort)plugin_match_size;
                }
            }
            var match_size = pool.LastMatchSize;
            var candidates_size = PreferredCandidatesSizeFor(match_size);

            // ----------------------------------------------------
            var buffer1 = ArrayPool<Ticket>.Shared.Rent(count);
            var buffer2 = ArrayPool<TicketData>.Shared.Rent(count);

            var tickets_full = buffer1.AsSpan(0, count);
            var tickets_data = buffer2.AsSpan(0, count);

            // dequeue up to [count] tickets
            var current_index = 0;
            var state_count = 0;

            TakeFromQueue(pool.PriorityQueue, count, ref current_index, ref state_count, tickets_full);
            TakeFromQueue(pool.Queue, count, ref current_index, ref state_count, tickets_full);

            void TakeFromQueue(ConcurrentQueue<Ticket> queue, int count,
                ref int current_index, ref int state_count, Span<Ticket> tickets_full)
            {
                var delay = _config.MatchmakerUpdateDelay;
                var consumed = _consumedTickets;

                DateTime now = DateTime.UtcNow;
                while (queue.TryDequeue(out var ticket))
                {
                    if (ticket.MaxAgeSeconds > 0)
                    {
                        // check if expired
                        var expires_at = DateTime.FromBinary(ticket.TimestampExpiryMatchmaker);

                        // when Director sets the expiry time, it bases it on Matchmaker reported status
                        // but based on our local update delay, this status can be behind up-to around the
                        // used delay, so we need to consider this as tolerance
                        var max_tolerance_seconds = delay;

                        var time_difference = now - expires_at;
                        if (time_difference.TotalSeconds > max_tolerance_seconds)
                        {
                            consumed.Enqueue((ticket, false));
                            continue;
                        }
                    }

                    if (ticket.State.Count > state_count)
                        state_count = ticket.State.Count;

                    tickets_full[current_index++] = ticket;

                    if (current_index >= count)
                        break;
                }
            }

            // make sure we ain't empty-handed
            if (current_index == 0)
                continue;

            var viable_tickets_full = tickets_full.Slice(0, current_index);
            var viable_tickets_data = buffer2.AsMemory(0, current_index);

            // convert to TicketData for more efficient processing
            for (int i = 0; i < viable_tickets_full.Length; i++)
                tickets_data[i] = new TicketData(viable_tickets_full[i], state_count, candidates_size);

            var consumed = new HashSet<string>();

            try
            {
                var expected_matches_count = tickets_data.Length / match_size;
                var matches = new List<TicketMatch>(expected_matches_count);

                var matched_all_it_could = MatchFunction(viable_tickets_data, matches, match_size, plugin: responsible_plugin);
                
                // in case of failed victims, we want to re-run the matching immediately (so they are not stuck)
                pool.HasFailedVictims = !matched_all_it_could;

                // submit matches first
                // (so that any invalid tickets are marked by the Director before they are consumed)
                if (matches.Count > 0)
                {
                    try
                    {
                        _state.StreamAddBatch(IState.STREAM_MATCHES, matches, static m => m.ToByteArray()).Wait();
                        Log.Information("[Matchmaker] Submitted {count} matches", matches.Count);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[Matchmaker] Failed to submit {count} matches", matches.Count);
                    }
                }

                // make sure used tickets are consumed
                var matches_span = CollectionsMarshal.AsSpan(matches);
                for (int i = 0; i < matches_span.Length; i++)
                {
                    var match = matches_span[i];

                    Ticket? ticket;

                    if (consumed.Add(match.GlobalId) && _assignedTickets.TryGetValue(match.GlobalId, out ticket))
                        _consumedTickets.Enqueue((ticket, true));

                    foreach (var tid in match.MatchedTicketGlobalIds)
                        if (consumed.Add(tid) && _assignedTickets.TryGetValue(tid, out ticket))
                            _consumedTickets.Enqueue((ticket, true));
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Matchmaker] Error while matching");
            }
            finally
            {
                // non-consumed tickets should be returned to the pool to the priority queue
                // (this check happens in finally in case EXCEPTION is thrown, tickets need to be returned)
                foreach (var t in viable_tickets_full)
                {
                    if (consumed.Contains(t.GlobalId))
                        continue;

                    t.MatchingFailureCount++;
                    if (t.MatchingFailureCount > max_failures)
                    {
                        // ticket failed to match too many times
                        _consumedTickets.Enqueue((t, false));
                        Log.Warning("[Matchmaker] Ticket {gid} failed to match too many times, consuming it", t.GlobalId);
                    }
                    else
                    {
                        pool.PriorityQueue.Enqueue(t);
                    }
                }

                ArrayPool<Ticket>.Shared.Return(buffer1);
                ArrayPool<TicketData>.Shared.Return(buffer2);

                Interlocked.Add(ref _processingTickets, -count);

                pool.Exit();
            }
        }
    }

    TicketPool? SelectNextPool(ref int pool_id_index)
    {
        if (_poolIds.Count == 0)
            return null;

        var tries = 0;
        while (tries < _poolIds.Count)
        {
            tries++;

            // NOTE: we go in a cycle from last used index, we do
            // this to avoid selecting the same pool every time.
            // If tickets are coming in fast enough, could result
            // in other pools being ignored. 

            pool_id_index = ++pool_id_index % _poolIds.Count;

            // get the pool
            var pool_id = _poolIds[pool_id_index];
            var pool = _pools[pool_id];

            // check if it has work to do (min. 2 tickets are required for smallest match size)
            // (priority queue IS IGNORED because those tickets failed to match, no use matching them again)
            if (pool.Queue.Count < 2)
                continue;

            // check if it's taken (used by another thread)
            if (!pool.TryEnter())
                continue;

            return pool;
        }

        return null;
    }

    public void Dispose()
    {
        _csc.Cancel();
    }

    class TicketPool
    {
        public string PoolId { get; init; } = "";
        /// <summary>
        /// Plugin responsible for this ticket pool
        /// </summary>
        public Plugin? ResponsiblePlugin { get; set; } = null;
        /// <summary>
        /// Last used match size (it's usually retrieved from state or from responsible plugin)
        /// <para>
        /// Can not be smaller than 2
        /// </para>
        /// </summary>
        public ushort LastMatchSize { get; set; } = 2;
        /// <summary>
        /// Ticket queue. After gathering finishes, queued up tickets are taken from here
        /// and then matched by a worker thread.
        /// </summary>
        public ConcurrentQueue<Ticket> Queue { get; } = new();
        /// <summary>
        /// Ticket priority queue. Tickets that failed to get matched get put into the
        /// priority queue, so they are dequeued before any newer tickets.
        /// 
        /// <para>
        /// This serves another purpose. If no newer tickets are queued,
        /// there is no reason to start matching again with tickets in priority
        /// queue that failed to get matched last run, as they will fail again.
        /// </para>
        /// </summary>
        public ConcurrentQueue<Ticket> PriorityQueue { get; } = new();
        /// <summary>
        /// True when preparing to start processing the queued tickets. Any tickets present in queue after
        /// gathering finishes, will be processed (up to a certain limit)
        /// <br/>
        /// Director should prioritize giving new tickets to matchmakers with pools in gathering phase
        /// </summary>
        public bool Gathering { get; set; } = false;
        /// <summary>
        /// If True, priority queue contains failed victims from previous run and it should be immediately re-run without Gathering.
        /// </summary>
        public bool HasFailedVictims { get; set; } = false;

        readonly SemaphoreSlim _lock = new(1);

        /// <summary>
        /// Try to enter this pool in order to process it. Will return false if pool 
        /// is already being processed by a different worker thread. When true is
        /// returned, the pool is reserved - remember to call <see cref="Exit"/> 
        /// when finished with pool.
        /// </summary>
        public bool TryEnter() => _lock.Wait(0);
        public void Exit() => _lock.Release();
        public int TicketCount => Queue.Count + PriorityQueue.Count;
    }
}
