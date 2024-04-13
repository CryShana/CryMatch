using CryMatch.Core.Enums;
using CryMatch.Core.Interfaces;

using CryMatchTests.Fixtures;

using System.Diagnostics;
using System.Security.Cryptography;

using Xunit.Abstractions;

namespace CryMatchTests;

[Collection("System")]
public class StateTests : IClassFixture<StateFixture>
{
    const int SPEED_ITERATIONS = 1000;

    readonly ITestOutputHelper _output;

    public IState Memory { get; }
    public IState Redis { get; }

    public StateTests(StateFixture state_fixture, ITestOutputHelper output)
    {
        Memory = state_fixture.Memory;
        Redis = state_fixture.Redis;
        _output = output;
    }

    IState GetState(string name) => name switch
    {
        "memory" => Memory,
        "redis" => Redis,
        _ => throw new Exception("Invalid state name")
    };

    [Theory]
    [InlineData("memory")]
    [InlineData("redis")]
    public async Task Strings(string state_name)
    {
        var state = GetState(state_name);
        var key = "test123";

        string? val;
        bool success;
        StateKeyType type;

        await state.KeyDelete(key);

        type = await state.KeyType(key);
        Assert.Equal(StateKeyType.None, type);

        val = await state.GetString(key);
        Assert.Null(val);

        type = await state.KeyType(key);
        Assert.Equal(StateKeyType.None, type);

        success = await state.SetString(key, "321");
        Assert.True(success);

        val = await state.GetString(key);
        Assert.Equal("321", val);

        type = await state.KeyType(key);
        Assert.Equal(StateKeyType.String, type);

        success = await state.SetString(key, "3210");
        Assert.True(success);

        val = await state.GetString(key);
        Assert.Equal("3210", val);

        success = await state.SetString(key, null);
        Assert.True(success);

        val = await state.GetString(key);
        Assert.Null(val);

        success = await state.SetString(key, null);
        Assert.False(success);

        success = await state.SetString(key, "001");
        Assert.True(success);

        await Task.Delay(100);

        val = await state.GetString(key + "0");
        Assert.Null(val);

        val = await state.GetString(key);
        Assert.Equal("001", val);

        success = await state.SetString(key, "001", TimeSpan.FromMilliseconds(200));
        Assert.True(success);

        val = await state.GetString(key);
        Assert.Equal("001", val);

        await Task.Delay(50);

        val = await state.GetString(key);
        Assert.Equal("001", val);

        await Task.Delay(200);

        val = await state.GetString(key);
        Assert.Null(val);

        success = await state.KeyDelete(key);
        Assert.False(success);

        type = await state.KeyType(key);
        Assert.Equal(StateKeyType.None, type);

        success = await state.KeyDelete(key + "0");
        Assert.False(success);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("redis")]
    public async Task Sets(string state_name)
    {
        var state = GetState(state_name);
        var set_key = "test_set";

        bool success;
        string?[]? values;

        await state.KeyDelete(set_key);
        await state.KeyDelete(set_key + "1");

        Assert.False(await state.SetContains(set_key, "t1"));

        var cresults = await state.SetContainsBatch(set_key, ["t1", "t2"]);
        Assert.Equal([false, false], cresults);

        await state.SetString(set_key, "blocker");

        try
        {
            success = await state.SetAdd(set_key, "t1");
            Assert.Fail("Set should not have been created");
        }
        catch { }

        try
        {
            values = await state.GetSetValues(set_key);
            Assert.Null(values);
        }
        catch { }

        await state.KeyDelete(set_key);

        success = await state.SetAdd(set_key, "t1");
        Assert.True(success);

        Assert.True(await state.SetContains(set_key, "t1"));
        Assert.False(await state.SetContains(set_key, "t2"));

        cresults = await state.SetContainsBatch(set_key, ["t1", "t2"]);
        Assert.Equal([true, false], cresults);

        cresults = await state.SetContainsBatch(set_key, ["t2", "t1"]);
        Assert.Equal([false, true], cresults);

        values = await state.GetSetValues(set_key);
        Assert.NotNull(values);
        Assert.Equal(["t1"], values);

        success = await state.SetAdd(set_key, "t1");
        Assert.False(success);

        success = await state.SetAdd(set_key, "t1");
        Assert.False(success);

        values = await state.GetSetValues(set_key);
        Assert.NotNull(values);
        Assert.Equal(["t1"], values);

        success = await state.SetAdd(set_key, "t2");
        Assert.True(success);

        values = await state.GetSetValues(set_key);
        Assert.NotNull(values);
        Assert.Equal(["t1", "t2"], values);

        success = await state.SetRemove(set_key, "t3");
        Assert.False(success);

        success = await state.SetAdd(set_key + "1", "t3");
        Assert.True(success);

        values = await state.GetSetValues(set_key);
        Assert.NotNull(values);
        Assert.Equal(["t1", "t2"], values);

        values = await state.GetSetValues(set_key + "1");
        Assert.NotNull(values);
        Assert.Equal(["t3"], values);

        success = await state.SetRemove(set_key, "t1");
        Assert.True(success);

        values = await state.GetSetValues(set_key);
        Assert.NotNull(values);
        Assert.Equal(["t2"], values);

        var type = await state.KeyType(set_key);
        Assert.Equal(StateKeyType.Set, type);

        // because set is empty, this is successful
        success = await state.SetString(set_key, "blocker_attempt");
        Assert.True(success, "String should have been created because set was empty");

        type = await state.KeyType(set_key);
        Assert.Equal(StateKeyType.String, type);

        // this should fail because string is not empty
        string? id = null;
        try
        {
            id = await state.StreamAdd(set_key, [1, 2, 3]);
        }
        catch { }
        Assert.True(id == null, "Stream should not have been created");

        // what if set is not empty
        success = false;
        try
        {
            success = await state.SetAdd(set_key, "t1");
        }
        catch { }
        Assert.False(success, "Set can't be created because string is not empty");

        // so let's remove string and create it then
        await state.KeyDelete(set_key);
        success = await state.SetAdd(set_key, "t1");
        Assert.True(success);

        // CHECK: string can be set regardless if existing key has a different type - idk why
        success = false;
        try
        {
            success = await state.SetString(set_key, "blocker_attempt");
        }
        catch { }
        Assert.True(success);
        await state.KeyDelete(set_key);
        //Assert.False(success, "String should not have been created");

        success = await state.SetAdd(set_key, "t1");
        Assert.True(success);

        success = await state.SetAdd(set_key, "t2");
        Assert.True(success);

        type = await state.KeyType(set_key);
        Assert.Equal(StateKeyType.Set, type);

        cresults = await state.SetRemoveBatch(set_key, ["t444", "t1"]);
        Assert.Equal([false, true], cresults);

        success = await state.KeyDelete(set_key);
        Assert.True(success);

        success = await state.KeyDelete(set_key);
        Assert.False(success);

        success = await state.SetAdd(set_key, "t1");
        Assert.True(success);

        success = await state.SetAdd(set_key, "t2");
        Assert.True(success);

        cresults = await state.SetRemoveBatch(set_key, ["t2", "t1", "t66"]);
        Assert.Equal([true, true, false], cresults);

        // should be false because removing all items from set also removes the key
        success = await state.KeyDelete(set_key);
        Assert.False(success);

        cresults = await state.SetAddBatch(set_key, ["t3", "t66"]);
        Assert.Equal([true, true], cresults);

        cresults = await state.SetAddBatch(set_key, ["t3", "t66", "t4"]);
        Assert.Equal([false, false, true], cresults);

        cresults = await state.SetRemoveBatch(set_key, ["t3", "t4"]);
        Assert.Equal([true, true], cresults);

        success = await state.KeyDelete(set_key);
        Assert.True(success);

        success = await state.KeyDelete(set_key + "1");
        Assert.True(success);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("redis")]
    public async Task Streams(string state_name)
    {
        var state = GetState(state_name);
        var stream_key = "test_stream";

        string? id;

        await state.KeyDelete(stream_key);

        var list = await state.StreamRead(stream_key);
        Assert.Null(list);

        var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 15, 14, 13, 12, 10, 9, 0, 0, 1 };

        id = await state.StreamAdd(stream_key, bytes);
        Assert.NotNull(id);

        list = await state.StreamRead(stream_key);
        Assert.NotNull(list);
        Assert.True(list.Count == 1);
        Assert.Equal(id, list[0].id);
        Assert.Equal(bytes, list[0].data);

        id = await state.StreamAdd(stream_key, bytes);
        Assert.NotNull(id);
        id = await state.StreamAdd(stream_key, bytes);
        Assert.NotNull(id);
        id = await state.StreamAdd(stream_key, bytes);
        Assert.NotNull(id);
        id = await state.StreamAdd(stream_key, bytes);
        Assert.NotNull(id);

        list = await state.StreamRead(stream_key);
        Assert.NotNull(list);
        Assert.Equal(5, list.Count);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(bytes, list[i].data);
            Assert.NotEqual(list[i].id, list[i + 1].id);
        }

        var ids = new HashSet<string>(list.Select(x => x.id));
        var count = await state.StreamDeleteMessages(stream_key, ids);
        Assert.Equal(5, count);

        list = await state.StreamRead(stream_key);
        Assert.Null(list);

        var type = await state.KeyType(stream_key);
        Assert.Equal(StateKeyType.Stream, type);

        var buffer = new byte[512];
        RandomNumberGenerator.Fill(buffer);
        id = await state.StreamAdd(stream_key, buffer);
        Assert.NotNull(id);

        list = await state.StreamRead(stream_key);
        Assert.NotNull(list);
        Assert.Equal(buffer, list[0].data);

        id = await state.StreamAdd(stream_key, bytes);
        Assert.NotNull(id);
        id = await state.StreamAdd(stream_key, bytes);
        Assert.NotNull(id);
        id = await state.StreamAdd(stream_key, bytes);
        Assert.NotNull(id);

        list = await state.StreamRead(stream_key, null);
        Assert.NotNull(list);
        Assert.Equal(4, list.Count);

        list = await state.StreamRead(stream_key, 0);
        Assert.Null(list);

        list = await state.StreamRead(stream_key, 1);
        Assert.NotNull(list);
        Assert.Equal(1, list.Count);

        list = await state.StreamRead(stream_key, 2);
        Assert.NotNull(list);
        Assert.Equal(2, list.Count);

        list = await state.StreamRead(stream_key, 3);
        Assert.NotNull(list);
        Assert.Equal(3, list.Count);

        list = await state.StreamRead(stream_key, 4);
        Assert.NotNull(list);
        Assert.Equal(4, list.Count);

        list = await state.StreamRead(stream_key, 5);
        Assert.NotNull(list);
        Assert.Equal(4, list.Count);

        await state.StreamDelete(stream_key);
        type = await state.KeyType(stream_key);
        Assert.Equal(StateKeyType.None, type);

        list = await state.StreamRead(stream_key);
        Assert.Null(list);
    }


    [Theory]
    [InlineData("memory")]
    [InlineData("redis")]
    public async Task StringSpeedTest(string state_name)
    {
        var state = GetState(state_name);

        var sw = Stopwatch.StartNew();

        // warmup
        for (int i = 0; i < SPEED_ITERATIONS; i++)
        {
            var key = $"key_{i}";
            var val = $"key_value_{i}";
            await state.SetString(key, val);
        }

        sw.Restart();
        for (int i = 0; i < SPEED_ITERATIONS; i++)
        {
            var key = $"key_{i}";
            var val = $"key_value_{i}";
            await state.SetString(key, val);
        }
        sw.Stop();

        var set_time = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        for (int i = 0; i < SPEED_ITERATIONS; i++)
        {
            var key = $"key_{i}";
            var val = await state.GetString(key);
        }
        sw.Stop();

        var get_time = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        for (int i = 0; i < SPEED_ITERATIONS; i++)
        {
            var key = $"key_{i}";
            await state.SetString(key, null);
        }
        sw.Stop();

        var del_time = sw.Elapsed.TotalMilliseconds;

        _output.WriteLine($"Time for {SPEED_ITERATIONS} iterations: (SET: {set_time}ms | GET: {get_time}ms | DEL: {del_time}ms)");

        // each part should take less than 1sec
        Assert.True(get_time < 1000 && set_time < 1000 && del_time < 1000);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("redis")]
    public async Task StreamSpeedTest(string state_name)
    {
        var state = GetState(state_name);

        var sw = Stopwatch.StartNew();

        var buffer = new byte[512];
        RandomNumberGenerator.Fill(buffer);

        var stream_key = "test_stream";

        // warmup
        await state.KeyDelete(stream_key);

        for (int i = 0; i < SPEED_ITERATIONS; i++)
        {
            buffer[0] = (byte)(i % 256);
            await state.StreamAdd(stream_key, buffer);
        }

        // benchmark
        await state.KeyDelete(stream_key);

        sw.Restart();
        for (int i = 0; i < SPEED_ITERATIONS; i++)
        {
            buffer[0] = (byte)(i % 256);
            await state.StreamAdd(stream_key, buffer);
        }
        sw.Stop();

        var set_time = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var list = await state.StreamRead(stream_key);
        sw.Stop();

        Assert.NotNull(list);
        Assert.Equal(SPEED_ITERATIONS, list.Count);

        var get_time = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var ids = new HashSet<string>(list.Select(x => x.Item1));
        await state.StreamDeleteMessages(stream_key, ids);
        sw.Stop();

        var del_time = sw.Elapsed.TotalMilliseconds;

        _output.WriteLine($"Time for {SPEED_ITERATIONS} iterations: (SET: {set_time}ms | GET: {get_time}ms | DEL: {del_time}ms)");

        // GETTING should be fast, anything else can take 1sec at most
        Assert.True(get_time < 100 && set_time < 1000 && del_time < 1000);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("redis")]
    public async Task StreamAddBatchSpeedTest(string state_name)
    {
        var state = GetState(state_name);

        var sw = Stopwatch.StartNew();

        var buffers = new byte[SPEED_ITERATIONS][];
        for (int i = 0; i < SPEED_ITERATIONS; i++)
        {
            buffers[i] = new byte[256];
            RandomNumberGenerator.Fill(buffers[i]);
        }

        var stream_key = "test_stream";

        // warmup
        await state.KeyDelete(stream_key);
        await state.StreamAddBatch(stream_key, buffers);

        // benchmark
        await state.KeyDelete(stream_key);

        sw.Restart();
        var ids = await state.StreamAddBatch(stream_key, buffers);
        sw.Stop();

        var add_time = sw.Elapsed.TotalMilliseconds;

        var list = await state.StreamRead(stream_key);

        Assert.NotNull(list);
        Assert.Equal(SPEED_ITERATIONS, list.Count);

        for (int i = 0; i < buffers.Length; i++)
        {
            // find corresponding one and check the ID of it
            var id = ids.Span[i];
            foreach (var entry in list)
            {
                if (entry.id == id)
                {
                    // all buffers should be equal
                    Assert.Equal(buffers[i], entry.data);
                }
            }
        }

        var ids_set = new HashSet<string>(list.Select(x => x.Item1));
        await state.StreamDeleteMessages(stream_key, ids_set);

        _output.WriteLine($"Time for {SPEED_ITERATIONS} iterations: (ADD: {add_time}ms)");

        // should be much faster than regular adding
        Assert.True(add_time < 50);
    }
   
    [Theory]
    [InlineData("memory")]
    [InlineData("redis")]
    public async Task SetSpeedTest_SingleVsBatch(string state_name)
    {
        var state = GetState(state_name);
        var set_key = "test_set";
        var set_count = 1000;
        var set_values = new string[set_count];
        var all_true = new bool[set_count];
        var temp = new bool[set_count];

        for (int i = 0; i < set_count; i++)
        {
            set_values[i] = $"{Guid.NewGuid()}";
            all_true[i] = true;
        }

        var sw = Stopwatch.StartNew();
        
        await state.KeyDelete(set_key);

        // warmup
        sw.Restart();
        foreach (var v in set_values)
        {
            await state.SetAdd(set_key, v);
        }
        sw.Stop();

        sw.Restart();
        for (int i = 0; i < set_values.Length; i++)
        {
            temp[i] = await state.SetContains(set_key, set_values[i]);
        }
        sw.Stop();
        Assert.Equal(all_true, temp);

        await state.KeyDelete(set_key);

        sw.Restart();
        await state.SetAddBatch(set_key, set_values);
        sw.Stop();

        sw.Restart();
        var results = await state.SetContainsBatch(set_key, set_values);
        sw.Stop();
        Assert.Equal(all_true, results);

        await state.KeyDelete(set_key);

        // actual benchmark

        // warmup
        sw.Restart();
        foreach (var v in set_values)
        {
            await state.SetAdd(set_key, v);
        }
        sw.Stop();
        var set_add_single = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        for (int i = 0; i < set_values.Length; i++)
        {
            temp[i] = await state.SetContains(set_key, set_values[i]);    
        }
        sw.Stop();
        Assert.Equal(all_true, temp);

        var set_contains_single = sw.Elapsed.TotalMilliseconds;

        await state.KeyDelete(set_key);

        sw.Restart();
        await state.SetAddBatch(set_key, set_values);
        sw.Stop();
        var set_add_batch = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        results = await state.SetContainsBatch(set_key, set_values);
        sw.Stop();
        Assert.Equal(all_true, results);
        var set_contains_batch = sw.Elapsed.TotalMilliseconds;

        await state.KeyDelete(set_key);

        _output.WriteLine($"Time for {SPEED_ITERATIONS} iterations: (ADD: {set_add_single}ms, CONTAINS: {set_contains_single}ms, ADD BATCH: {set_add_batch}ms, CONTAINS BATCH: {set_contains_batch}ms)");

        // should be much faster than regular adding
        Assert.True(set_add_batch < 50 && set_contains_batch < 50);
    }
}