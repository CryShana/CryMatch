
namespace CryMatch.Core.Interfaces;

/// <summary>
/// Fast state storage that contains CryMatch state
/// </summary>
public interface IState
{
    // common names used across the system
    public const string STREAM_MATCHES = "matches";
    public const string STREAM_TICKETS_UNASSIGNED = "tickets_unassigned";
    public const string STREAM_TICKETS_ASSIGNED_PREFIX = "tickets_";
    public const string STREAM_TICKETS_CONSUMED = "consumed_tickets";
    public const string SET_MATCHMAKERS = "matchmakers";
    public const string SET_TICKETS_SUBMITTED = "tickets_submitted";
    public const string DIRECTOR_ACTIVE_KEY = "director_is_active";
    public const string POOL_MATCH_SIZE_PREFIX = "pool_match_size_";

    /// <summary>
    /// Sets given string value under string key with optional expiry.
    /// </summary>
    /// <param name="key">Key</param>
    /// <param name="value">Value</param>
    /// <param name="expiry">Key will be deleted after this time has passed</param>
    /// <returns>True if string was set successfully</returns>
    Task<bool> SetString(string key, string? value, TimeSpan? expiry = null);
    /// <summary>
    /// Gets string value under string key.
    /// </summary>
    /// <param name="key">Key</param>
    /// <returns>Contained value or NULL if it does not exist</returns>
    Task<string?> GetString(string key);
    /// <summary>
    /// Adds given data to the stream
    /// </summary>
    /// <param name="key">Stream key</param>
    /// <param name="data">Binary data to be added</param>
    /// <returns>Id of created message (NULL if it failed to be created)</returns>
    Task<string?> StreamAdd(string key, byte[] data);
    /// <summary>
    /// Adds given data to the stream in batch (to minimize RTT overhead)
    /// </summary>
    /// <param name="key">Stream key</param>
    /// <param name="batched_data">List of binary data to be added</param>
    /// <returns>List of Ids of created messages (in same order it was provided)</returns>
    Task<ReadOnlyMemory<string>> StreamAddBatch(string key, IList<byte[]> batched_data);
    /// <summary>
    /// Adds given data to the stream in batch (to minimize RTT overhead) using custom data fetching logic
    /// to avoid needless allocations
    /// </summary>
    /// <param name="key">Stream key</param>
    /// <param name="batched_data">List of binary data to be added</param>
    /// <param name="data_fetcher">Function to use that will get the binary data from each item in batched data</param>
    /// <returns>List of Ids of created messages (in same order it was provided)</returns>
    Task<ReadOnlyMemory<string>> StreamAddBatch<T>(string key, IList<T> batched_data, Func<T, byte[]> data_fetcher);
    /// <summary>
    /// Returns list of available messages in the stream, from oldest to most recent.
    /// </summary>
    /// <param name="key">Stream key</param>
    /// <param name="max_count">Max elements to return per call</param>
    /// <returns>List of messages or NULL if there are no messages</returns>
    Task<IReadOnlyList<(string id, byte[] data)>?> StreamRead(string key, int? max_count = null);
    /// <summary>
    /// Deletes stream
    /// </summary>
    /// <param name="key">Stream key</param>
    /// <returns>True if deleted successfully</returns>
    Task<bool> StreamDelete(string key);
    /// <summary>
    /// Deletes all messages in a stream without deleting the stream
    /// </summary>
    /// <param name="stream_key">Stream key</param>
    /// <returns>Number of messages deleted</returns>
    Task<long> StreamDeleteMessages(string stream_key, HashSet<string> message_ids);
    /// <summary>
    /// Add value to set
    /// </summary>
    /// <param name="set_key">Set key</param>
    /// <param name="value">Value to add</param>
    /// <returns>True if value was added, False if it already existed</returns>
    Task<bool> SetAdd(string set_key, string value);
    /// <summary>
    /// Add values to set in batch (to minimize RTT overhead)
    /// </summary>
    /// <param name="set_key">Set key</param>
    /// <param name="values">Values to add</param>
    /// <returns>List of results (in same order it was provided)</returns>
    Task<bool[]> SetAddBatch(string set_key, IList<string> values);
    /// <summary>
    /// Remove value from set
    /// </summary>
    /// <param name="set_key">Set key</param>
    /// <param name="value">Value to remove</param>
    /// <returns>True if value was removed, False if it wasn't</returns>
    Task<bool> SetRemove(string set_key, string value);
    /// <summary>
    /// Remove values from set in batch (to minimize RTT overhead)
    /// </summary>
    /// <param name="set_key">Set key</param>
    /// <param name="values">Values to be remove</param>
    /// <returns>List of results (in same order it was provided)</returns>
    Task<bool[]> SetRemoveBatch(string set_key, IList<string> values);
    /// <summary>
    /// Gets all set values
    /// </summary>
    /// <param name="set_key">Set key</param>
    /// <returns>Array of values of NULL if set does not exist</returns>
    Task<string?[]?> GetSetValues(string set_key);
    /// <summary>
    /// Checks if set contains given value
    /// </summary>
    /// <param name="set_key">Set key</param>
    /// <param name="value">Value to check for</param>
    /// <returns>True if set contains given value</returns>
    Task<bool> SetContains(string set_key, string value);
    /// <summary>
    /// Checks if set contains given values in batch (to minimize RTT overhead)
    /// </summary>
    /// <param name="set_key">Set key</param>
    /// <param name="values">List of values to check for</param>
    /// <returns>List of results (in same order it was provided)</returns>
    Task<bool[]> SetContainsBatch(string set_key, IList<string> values);
    /// <summary>
    /// Checks if set contains given values in batch (to minimize RTT overhead)
    /// </summary>
    /// <param name="set_key">Set key</param>
    /// <param name="items">List of items which values will be checked</param>
    /// <param name="value_fetcher">Function that gets value from each list item</param>
    /// <returns>List of results (in same order it was provided)</returns>
    Task<bool[]> SetContainsBatch<T>(string set_key, IList<T> items, Func<T, string> value_fetcher);
    /// <summary>
    /// Deletes given key
    /// </summary>
    /// <param name="key">Key</param>
    /// <returns>True if it was deleted</returns>
    Task<bool> KeyDelete(string key);
    /// <summary>
    /// Get key type
    /// </summary>
    /// <param name="key">Key</param>
    /// <returns>Type of key</returns>
    Task<StateKeyType> KeyType(string key);
}