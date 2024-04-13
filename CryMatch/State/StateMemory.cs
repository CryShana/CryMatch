using System.Collections.Concurrent;
using System.Runtime.InteropServices;

using StreamMessage = (string id, byte[] data);

namespace CryMatch.Storage;

public class StateMemory : IState
{
    readonly ConcurrentDictionary<string, StateEntry> _store = new();

    public StateMemory()
    {
        _ = Start();

        Log.Information("Using in-memory for state");
    }

    async Task Start()
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync())
        {
            // can do stuff here...
        }
    }

    public Task<string?> GetString(string key)
    {
        if (_store.TryGetValue(key, out var entry)) return Task.FromResult<string?>(entry.GetString());
        return Task.FromResult<string?>(null);
    }

    public Task<bool> SetString(string key, string? value, TimeSpan? expiry = null)
    {
        if (_store.TryGetValue(key, out var entry) && entry.Type != StateKeyType.String)
        {
            // CHECK: for some reason Redis no longer throws error when setting string and entry is different type,
            // so for now we are overriding everything it seems
   
            // throw new Exception("Key type is not a string");
        }

        // if entry exists, we cancel expiry either way,
        // as we are either removing it or updating it
        entry?.ExpiryCancellationSource?.Cancel();

        // remove key if NULL
        if (value == null)
            return Task.FromResult(_store.TryRemove(key, out entry));

        // create new key or update existing
        var csc = expiry.HasValue ? new CancellationTokenSource() : null;

        _store[key] = new(value, csc);

        if (expiry.HasValue)
        {
            _ = Task.Delay(expiry.Value, csc!.Token).ContinueWith(t =>
            {
                _store.TryRemove(key, out _);
            }, csc.Token);
        }

        return Task.FromResult(true);
    }

    public Task<string?> StreamAdd(string key, byte[] data)
    {
        var entry = _store.GetOrAdd(key, new StateEntry(new List<StreamMessage>()));
        if (entry.Type != StateKeyType.Stream) throw new Exception("Key type is not a stream");

        var id = Guid.NewGuid().ToString();

        var stream = entry.GetStream();
        lock (stream) stream.Add((id, data));

        return Task.FromResult<string?>(id);
    }

    public Task<ReadOnlyMemory<string>> StreamAddBatch(string key, IList<byte[]> batched_data)
        => StreamAddBatch(key, batched_data, static d => d);

    public Task<ReadOnlyMemory<string>> StreamAddBatch<T>(string key, IList<T> batched_data, Func<T, byte[]> data_fetcher)
    {
        var entry = _store.GetOrAdd(key, new StateEntry(new List<StreamMessage>()));
        if (entry.Type != StateKeyType.Stream) throw new Exception("Key type is not a stream");

        var ids = new string[batched_data.Count];
        var stream = entry.GetStream();
        lock (stream)
        {
            for (int i = 0; i < batched_data.Count; i++)
            {
                var id = Guid.NewGuid().ToString();
                stream.Add((id, data_fetcher(batched_data[i])));
                ids[i] = id;
            }
        }

        ReadOnlyMemory<string> mem = ids.AsMemory();
        return Task.FromResult(mem);
    }

    public Task<IReadOnlyList<(string id, byte[] data)>?> StreamRead(string key, int? max_count = null)
    {
        if (max_count <= 0)
            return Task.FromResult<IReadOnlyList<StreamMessage>?>(null);

        if (!_store.TryGetValue(key, out var entry))
            return Task.FromResult<IReadOnlyList<StreamMessage>?>(null);

        var stream = entry.GetStream();
        if (stream.Count == 0)
            return Task.FromResult<IReadOnlyList<StreamMessage>?>(null);

        lock (stream)
        {
            var count = Math.Min(stream.Count, max_count ?? int.MaxValue);
            if (count <= 0)
                return Task.FromResult<IReadOnlyList<StreamMessage>?>(null);

            var list = new StreamMessage[count];

            // fastest copy in the west
            CollectionsMarshal.AsSpan(stream)
                .Slice(0, count)
                .CopyTo(list.AsSpan());

            return Task.FromResult<IReadOnlyList<StreamMessage>?>(list);
        }
    }

    public Task<bool> StreamDelete(string key)
    {
        var success = _store.TryRemove(key, out _);
        return Task.FromResult(success);
    }

    public Task<long> StreamDeleteMessages(string stream_key, HashSet<string> message_ids)
    {
        if (!_store.TryGetValue(stream_key, out var entry))
            return Task.FromResult(0L);

        if (message_ids.Count == 0)
            return Task.FromResult(0L);

        var stream = entry.GetStream();

        var count = 0L;
        lock (stream)
        {
            for (int i = 0; i < stream.Count; i++)
                if (message_ids.Contains(stream[i].id))
                {
                    stream.RemoveAt(i--);
                    count++;

                    // HashSet will not contain duplicates, so if we removed
                    // [HashSet.Count] elements, we definitely removed them all
                    if (count == message_ids.Count)
                        break;
                }
        }

        return Task.FromResult(count);
    }

    public Task<bool> SetAdd(string set_key, string value)
    {
        var entry = _store.GetOrAdd(set_key, new StateEntry(new HashSet<string>()));
        if (entry.Type != StateKeyType.Set) throw new Exception("Key type is not a set");

        var set = entry.GetSet();
        lock (set) return Task.FromResult(set.Add(value));
    }

    public Task<bool[]> SetAddBatch(string set_key, IList<string> values)
    {
        var entry = _store.GetOrAdd(set_key, new StateEntry(new HashSet<string>()));
        if (entry.Type != StateKeyType.Set) throw new Exception("Key type is not a set");
        
        var results = new bool[values.Count];

        var set = entry.GetSet();
        lock (set)
        {
            for (int i = 0; i < values.Count; i++)
            {
                results[i] = set.Add(values[i]);
            }
        }

        return Task.FromResult(results);
    }

    public Task<bool> SetRemove(string set_key, string value)
    {
        if (!_store.TryGetValue(set_key, out var entry)) return Task.FromResult(false);

        var set = entry.GetSet();
        lock (set)
        {
            var removed = set.Remove(value);

            if (set.Count == 0)
                _store.TryRemove(set_key, out _);

            return Task.FromResult(removed);
        }
    }

    public Task<bool[]> SetRemoveBatch(string set_key, IList<string> values)
    {
        var results = new bool[values.Count];

        if (!_store.TryGetValue(set_key, out var entry)) return Task.FromResult(results);

        var set = entry.GetSet();
        lock (set)
        {
            for (int i = 0; i < values.Count; i++)
            {
                results[i] = set.Remove(values[i]);
            }

            if (set.Count == 0)
                _store.TryRemove(set_key, out _);
        }

        return Task.FromResult(results);
    }

    public Task<string?[]?> GetSetValues(string set_key)
    {
        if (!_store.TryGetValue(set_key, out var entry)) return Task.FromResult<string?[]?>(null);

        var set = entry.GetSet();
        return Task.FromResult<string?[]?>(set.ToArray());
    }

    public Task<bool> SetContains(string set_key, string value)
    {
        if (!_store.TryGetValue(set_key, out var entry)) return Task.FromResult(false);

        var set = entry.GetSet();
        return Task.FromResult(set.Contains(value));
    }

    public Task<bool[]> SetContainsBatch(string set_key, IList<string> values) 
        => SetContainsBatch(set_key, values, static x => x);

    public Task<bool[]> SetContainsBatch<T>(string set_key, IList<T> items, Func<T, string> value_fetcher)
    {
        var results = new bool[items.Count];

        if (!_store.TryGetValue(set_key, out var entry)) return Task.FromResult(results);

        var set = entry.GetSet();
        lock (set)
        {
            for (int i = 0; i < items.Count; i++)
            {
                results[i] = set.Contains(value_fetcher(items[i]));
            }
        }

        return Task.FromResult(results);
    }

    public Task<bool> KeyDelete(string key)
    {
        return Task.FromResult(_store.TryRemove(key, out _));
    }

    public Task<StateKeyType> KeyType(string key)
    {
        if (!_store.TryGetValue(key, out var entry))
            return Task.FromResult(StateKeyType.None);

        return Task.FromResult(entry.Type);
    }

    class StateEntry
    {
        readonly string? _stringValue;
        readonly HashSet<string>? _set;
        readonly List<StreamMessage>? _stream;

        public StateKeyType Type { get; }
        public CancellationTokenSource? ExpiryCancellationSource { get; }

        public StateEntry(string val, CancellationTokenSource? csc = null)
        {
            Type = StateKeyType.String;

            _stringValue = val;
            ExpiryCancellationSource = csc;
        }

        public StateEntry(HashSet<string> set, CancellationTokenSource? csc = null)
        {
            Type = StateKeyType.Set;

            _set = set;
            ExpiryCancellationSource = csc;
        }

        public StateEntry(List<StreamMessage> stream, CancellationTokenSource? csc = null)
        {
            Type = StateKeyType.Stream;

            _stream = stream;
            ExpiryCancellationSource = csc;
        }

        public string GetString()
        {
            if (Type != StateKeyType.String) throw new Exception("Key type is not a string");
            return _stringValue!;
        }

        public HashSet<string> GetSet()
        {
            if (Type != StateKeyType.Set) throw new Exception("Key type is not a set");
            return _set!;
        }

        public List<StreamMessage> GetStream()
        {
            if (Type != StateKeyType.Stream) throw new Exception("Key type is not a stream");
            return _stream!;
        }
    }
}
