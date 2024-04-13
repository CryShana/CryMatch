using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CryMatch.Core;

public class Configuration
{
    /// <summary>
    /// Endpoint on which this app will listen for requests
    /// </summary>
    public string? ListenEndpoint { get; set; } = "0.0.0.0:5000";
    /// <summary>
    /// Path to certificate file to use with HTTP 2.0
    /// </summary>
    public string? CertificatePath { get; set; } = "";
    /// <summary>
    /// Path to private key file to use with HTTP 2.0
    /// </summary>
    public string? PrivateKeyPath { get; set; } = "";
    /// <summary>
    /// Determines work mode, check enum for more information
    /// </summary>
    public string? Mode { get; set; } = WorkMode.Standalone.ToString();
    /// <summary>
    /// Amount of threads that can be utilized by the matchmaker for parallel processing
    /// </summary>
    public int MatchmakerThreads { get; set; } = Math.Min(2, Math.Max(1, Environment.ProcessorCount));
    /// <summary>
    /// True if Redis configuration should be used to connect to Redis
    /// for CryMatch state storage (otherwise app memory is used by default)
    /// </summary>
    public bool UseRedis { get; set; } = false;
    /// <summary>
    /// Specify configuration options for connecting to Redis, more info here https://stackexchange.github.io/StackExchange.Redis/Configuration.html
    /// <para>
    /// Simplest configuration is just 'localhost' and will assume default port 6379. Further options can then be appended using comma as a delimiter
    /// </para>
    /// <para>
    /// When scaling Redis, you can specify all redis instances like 'redis0:6380,redis1:6380'
    /// </para>
    /// <para>
    /// If you specify a service name, it will trigger Sentinel mode. The following will connect to sentinel instance using default port 26379 and discover any other instances:
    /// 'localhost,serviceName=myPrimary' (it will auto-update if primary changes)
    /// </para>
    /// <para>
    /// Other common options (that can be appended to the string) include:
    /// <list type="bullet">
    /// <item>allowAdmin=false (set true to allow clients to make risky operations)</item>
    /// <item>channelPrefix=null (optional channel prefix for all sub-operations)</item>
    /// <item>connectRetry=3 (amount of times to retry during INITIAL connection)</item>
    /// <item>connectTimeout=5000 (time in ms to timeout connect operations)</item>
    /// <item>defaultDatabase=-1 (default database index from 0 to [databases - 1])</item>
    /// <item>name=null (client name, identification for connection within Redis)</item>
    /// <item>keepAlive=-1 (time in seconds to send message to keep connection alive, 60sec default)</item>
    /// <item>password=null (password for Redis server)</item>
    /// <item>user=null (user for Redis server, for use with ACLs)</item>
    /// <item>ssl=false (true if SSL encryption should be used)</item>
    /// <item>sslHost=null (enforces SSL host identity for server certificate)</item>
    /// </list>
    /// </para>
    /// </summary>
    public string? RedisConfigurationOptions { get; set; } = "localhost:6379,name=CryMatch,password=123";
    /// <summary>
    /// How much time in seconds can a matchmaker/director entry remain stale before it's considered offline
    /// </summary>
    public double MaxDowntimeBeforeOffline { get; set; } = 0.6;
    /// <summary>
    /// Time between matchmaker updates in seconds (matchmaker updates the state status every update)
    /// </summary>
    public double MatchmakerUpdateDelay { get; set; } = 0.1;
    /// <summary>
    /// Time between director updates in seconds (director assigns tickets, processes matches, handles matchmakers)
    /// </summary>
    public double DirectorUpdateDelay { get; set; } = 0.2;

    public const double MIN_DOWNTIME = 0.1;
    public const double MIN_DIRECTOR_UPDATE_DELAY = 0.01;
    public const double MIN_MATCHMAKER_UPDATE_DELAY = 0.01;

    /// <summary>
    /// Time in seconds that matchmaker will wait for more tickets to gather in a pool before processing them
    /// (only applies if below capacity)
    /// </summary>
    public double MatchmakerMinGatherTime { get; set; } = 5;
    /// <summary>
    /// Capacity of ticket pool after which a matchmaker is considered full. (This is per ticket pool)
    /// </summary>
    public int MatchmakerPoolCapacity { get; set; } = 50_000;
    /// <summary>
    /// Max. number of matching failures a ticket can go through 
    /// before matchmaker consumes it to make space for other tickets
    /// </summary>
    public int MaxMatchFailures { get; set; } = 100;

    #region Parsed data
    int? _matchmakerThreads;
    IPEndPoint? _parsedListenEndpoint;
    WorkMode? _parsedWorkMode;

    [JsonIgnore]
    public IPEndPoint ParsedListenEndpoint
    {
        get
        {
            if (_parsedListenEndpoint == null)
            {
                var endpoint = ListenEndpoint ?? "0.0.0.0:5000";
                if (!endpoint.ParseAsEndpoint(out _parsedListenEndpoint))
                {
                    _parsedListenEndpoint = new IPEndPoint(IPAddress.Any, 5000);
                }
            }

            return _parsedListenEndpoint;
        }
    }

    [JsonIgnore]
    public WorkMode ParsedMode
    {
        get
        {
            if (!_parsedWorkMode.HasValue)
                _parsedWorkMode = Mode.ParseAsWorkMode();

            return _parsedWorkMode.Value;
        }
    }

    [JsonIgnore]
    public int ParsedMatchmakerThreads
    {
        get
        {
            if (!_matchmakerThreads.HasValue)
            {
                var max = MatchmakerThreads;
                if (MatchmakerThreads <= 0 || MatchmakerThreads > 128) 
                    max = 1;

                _matchmakerThreads = (int)max;
            }

            return _matchmakerThreads.Value;
        }
    }
    #endregion

    public void Save(string filepath)
    {
        using var file = File.OpenWrite(filepath);
        JsonSerializer.Serialize(file, this, AppJsonSerializerContext.Default.Configuration);
    }

    public static Configuration? Load(string filepath)
    {
        if (!File.Exists(filepath)) return null;
        using var file = File.OpenRead(filepath);

        try
        {
            return JsonSerializer.Deserialize(file, AppJsonSerializerContext.Default.Configuration);
        }
        catch
        {
            throw new Exception("Failed to deserialize configuration file, is JSON valid?");
        }
    }
}

[JsonSerializable(typeof(Configuration))]
[JsonSourceGenerationOptions(AllowTrailingCommas = true, WriteIndented = true)]
internal partial class AppJsonSerializerContext : JsonSerializerContext { }
