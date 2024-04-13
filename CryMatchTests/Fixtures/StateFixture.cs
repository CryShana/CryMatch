
using CryMatch.Core;
using CryMatch.Storage;

namespace CryMatchTests.Fixtures;

public class StateFixture : IDisposable
{
    public StateMemory Memory { get; }
    public StateRedis Redis { get; }

    public StateFixture()
    {
        var config = new Configuration
        {
            UseRedis = true,
            RedisConfigurationOptions = "localhost"
        };

        Memory = new StateMemory();
        Redis = new StateRedis(config);
    }

    public void Dispose()
    {
        Redis.Dispose();
    }
}
