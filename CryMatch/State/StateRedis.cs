using StackExchange.Redis;

using System.Buffers;

namespace CryMatch.Storage;

public class StateRedis : IState, IDisposable
{
    readonly Configuration _config;
    readonly ConnectionMultiplexer _redis;

    public StateRedis(Configuration config)
    {
        _config = config;

        if (string.IsNullOrEmpty(config.RedisConfigurationOptions))
            throw new Exception("No Redis configuration provided");

        Log.Information("Using Redis for state");

        // the following will block until connected, which is fine
        // we don't want to continue until this connection is valid
        _redis = ConnectionMultiplexer.Connect(config.RedisConfigurationOptions);
        Log.Information("Redis connected");
    }

    public async Task<string?> GetString(string key)
        // await is needed here for the implicit conversion between RedisValue and string
        => await _redis.GetDatabase().StringGetAsync(key); 

    public Task<bool> SetString(string key, string? value, TimeSpan? expiry = null) 
        => _redis.GetDatabase().StringSetAsync(key, value, expiry);

    // NOTE: we use Redis streams instead of pub/sub because streams remember the data and will not discard it
    // until it is manually removed by the Director

    // NOTE: we do not need to create consumer groups for streams, because each matchmaker will have it's own
    // stream key anyway. Director will handle adding tickets to the right streams. This is because it is not
    // optimal to just indiscriminately assign tickets to all consumers within a consumer group, we want
    // the Director to handle this logic. This means no stream acknowledgments are necessary either as we
    // aren't using consumer groups and we also don't need to deal with auto-claiming pending messages

    public async Task<string?> StreamAdd(string key, byte[] data)
    {
        string? id = await _redis.GetDatabase().StreamAddAsync(key,
        [
            new NameValueEntry("data", data)
        ]);

        return id;
    }

    public Task<ReadOnlyMemory<string>> StreamAddBatch(string key, IList<byte[]> batched_data)
        => StreamAddBatch(key, batched_data, static d => d);

    public async Task<ReadOnlyMemory<string>> StreamAddBatch<T>(string key, IList<T> batched_data, Func<T, byte[]> data_fetcher)
    {
        var ids = new string[batched_data.Count];
        var responses = new Task<RedisValue>[batched_data.Count];

        var batch = _redis.GetDatabase().CreateBatch();

        for (int i = 0; i < batched_data.Count; i++)
        {
            responses[i] = batch.StreamAddAsync(key,
            [
                new NameValueEntry("data", data_fetcher(batched_data[i]))
            ]);
        }

        batch.Execute();

        var values = await Task.WhenAll(responses);
        for (int i = 0; i < values.Length; i++)
        {
            ids[i] = values[i].ToString();
        }

        return ids.AsMemory();
    }

    public async Task<IReadOnlyList<(string id, byte[] data)>?> StreamRead(string key, int? max_count = null)
    {
        if (max_count <= 0)
            return null;

        var messages = await _redis.GetDatabase().StreamReadAsync(key, 0, max_count);
        if (messages == null || messages.Length == 0)
            return null;

        var list = new List<(string, byte[])>(messages.Length);
        for (int i = 0; i < messages.Length; i++)
        {
            var m = messages[i];
            byte[]? data = m["data"];
            if (data == null || data.Length == 0)
                continue;

            list.Add((m.Id!, data));
        }

        return list;
    }

    public Task<bool> StreamDelete(string key) 
        => _redis.GetDatabase().KeyDeleteAsync(key);
    
    public Task<long> StreamDeleteMessages(string stream_key, HashSet<string> message_ids)
    {
        var ids = message_ids.Select(x => new RedisValue(x)).ToArray();
        return _redis.GetDatabase().StreamDeleteAsync(stream_key, ids);
    }

    public Task<bool> SetAdd(string set_key, string value) 
        => _redis.GetDatabase().SetAddAsync(set_key, value);

    public Task<bool[]> SetAddBatch(string set_key, IList<string> values)
    {
        var responses = new Task<bool>[values.Count];
        var batch = _redis.GetDatabase().CreateBatch();

        for (int i = 0; i < values.Count; i++)
        {
            responses[i] = batch.SetAddAsync(set_key, values[i]);
        }

        batch.Execute();

        return Task.WhenAll(responses);
    }

    public Task<bool> SetRemove(string set_key, string value) 
        => _redis.GetDatabase().SetRemoveAsync(set_key, value);
    
    public Task<bool[]> SetRemoveBatch(string set_key, IList<string> values)
    {
        var responses = new Task<bool>[values.Count];
        var batch = _redis.GetDatabase().CreateBatch();

        for (int i = 0; i < values.Count; i++)
        {
            responses[i] = batch.SetRemoveAsync(set_key, values[i]);
        }

        batch.Execute();

        return Task.WhenAll(responses);
    }

    public async Task<string?[]?> GetSetValues(string set_key)
    {
        var members = await _redis.GetDatabase().SetMembersAsync(set_key);
        if (members == null || members.Length == 0) return null;
        return members.Select(x => (string?)x).ToArray();
    }

    public Task<bool> SetContains(string set_key, string value) 
        => _redis.GetDatabase().SetContainsAsync(set_key, value);

    public Task<bool[]> SetContainsBatch(string set_key, IList<string> values)
        => SetContainsBatch(set_key, values, static x => x);

    public Task<bool[]> SetContainsBatch<T>(string set_key, IList<T> items, Func<T, string> value_fetcher)
    {
        var responses = new Task<bool>[items.Count];
        var batch = _redis.GetDatabase().CreateBatch();

        for (int i = 0; i < items.Count; i++)
        {
            responses[i] = batch.SetContainsAsync(set_key, value_fetcher(items[i]));
        }

        batch.Execute();

        return Task.WhenAll(responses);
    }

    public void Dispose() => _redis.Dispose();
    
    public Task<bool> KeyDelete(string key) 
        => _redis.GetDatabase().KeyDeleteAsync(key);
    
    public async Task<StateKeyType> KeyType(string key)
    {
        var type = await _redis.GetDatabase().KeyTypeAsync(key);
        return type switch
        {
            RedisType.Set => StateKeyType.Set,
            RedisType.String => StateKeyType.String,
            RedisType.Stream => StateKeyType.Stream,
            RedisType.None => StateKeyType.None,
            _ => StateKeyType.Unknown
        };
    }
}
