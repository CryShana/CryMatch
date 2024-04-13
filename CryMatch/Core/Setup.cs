using CryMatch.Storage;
using CryMatch.Services;
using CryMatch.Director;
using CryMatch.Matchmaker;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using CryMatch.Matchmaker.Plugins;

namespace CryMatch.Core;

public static class Setup
{
    const string CONFIG_PATH = "config.json";

    public static WebApplication BuildApp(string[] args)
    {
        // LOAD CONFIGURATION
        var config = Configuration.Load(CONFIG_PATH);
        if (config == null)
        {
            config = new Configuration();
            config.Save(CONFIG_PATH);
            Log.Warning("No configuration found, created '{path}' with default values", CONFIG_PATH);
        }

        // BUILD
        var builder = WebApplication.CreateSlimBuilder(args);
        builder.Configuration.Sources.Clear();
        builder.WebHost.UseKestrel(o =>
        {
            o.ConfigureEndpointDefaults(eo => eo.Protocols = HttpProtocols.Http2);
            o.Listen(config.ParsedListenEndpoint, lo =>
            {
                var cert = Extensions.ParseCertificate(config.CertificatePath, config.PrivateKeyPath);
                if (cert != null)
                {
                    lo.UseHttps(cert);
                    Log.Information("SSL certificate loaded");
                }
                else
                {
                    Log.Warning("No SSL certificate provided");
                }
            });
        });
        builder.Host.UseSerilog();

        // SERVICES
        builder.Services.AddGrpc();
#if DEBUG
        builder.Services.AddGrpcReflection();
#endif
        builder.Services.AddSingleton(config);


        Log.Information("Starting in {work_mode} mode", config.ParsedMode);
        if (config.ParsedMode != WorkMode.Standalone && !config.UseRedis)
        {
            config.UseRedis = true;
            Log.Warning("Usage of Redis is required in non-standalone mode! Setting it to true.");
        }

        // define state
        if (config.UseRedis)
        {
            builder.Services.AddSingleton<IState, StateRedis>();
        }
        else
        {
            builder.Services.AddSingleton<IState, StateMemory>();
        }

        // load plugins
        var plugin_loader = new PluginLoader("plugins");
        var loaded_plugins = plugin_loader.Load();
        if (loaded_plugins > 0)
        {
            Log.Information("Loaded {plugins} plugin/s: {list}", loaded_plugins, plugin_loader.Loaded());
        }

        // define core services
        builder.Services.AddSingleton(plugin_loader);
        switch (config.ParsedMode)
        {
            case WorkMode.Standalone:
                builder.Services.AddSingleton<DirectorManager>();
                builder.Services.AddSingleton<MatchmakerManager>();
                break;

            case WorkMode.Director:
                builder.Services.AddSingleton<DirectorManager>();
                break;

            case WorkMode.Matchmaker:
                builder.Services.AddSingleton<MatchmakerManager>();
                break;

            default:
                throw new Exception("Invalid mode specified: " + config.ParsedMode);
        }

        return builder.Build();
    }

    public static void ConfigureApp(WebApplication app)
    {
        var config = app.Services.GetRequiredService<Configuration>();

        ValidateConfig(config);

        // map gRPC services
        app.MapGrpcService<MonitorService>().CacheOutput();

#if DEBUG
        // gRPC reflection is useful for tools like "gRPCui" (https://github.com/fullstorydev/grpcui, can run 'grpcui -insecure localhost:5000' for testing)
        // it enables automatic detection of all gRPC services
        app.MapGrpcReflectionService();
#endif

        switch (config.ParsedMode)
        {
            case WorkMode.Standalone:
                app.MapGrpcService<DirectorService>();
                app.MapGrpcService<MatchmakerService>();
                break;

            case WorkMode.Director:
                app.MapGrpcService<DirectorService>();
                break;

            case WorkMode.Matchmaker:
                app.MapGrpcService<MatchmakerService>();
                break;

            default:
                throw new Exception("Invalid mode specified: " + config.ParsedMode);
        }

        // activate all singletons
        app.Services.GetService<IState>();
        app.Services.GetService<DirectorManager>();
        app.Services.GetService<MatchmakerManager>();
    }

    static void ValidateConfig(Configuration config)
    {
        if (config.MaxDowntimeBeforeOffline < Configuration.MIN_DOWNTIME)
        {
            throw new Exception($"Max downtime is too low, " +
                $"must be >= {Configuration.MIN_DOWNTIME} seconds");
        }

        if (config.MaxDowntimeBeforeOffline < config.MatchmakerUpdateDelay ||
            config.MaxDowntimeBeforeOffline < config.DirectorUpdateDelay)
        {
            throw new Exception("Max downtime must be larger than the update delays " +
                "(otherwise services will be considered offline)");
        }

        if (config.MatchmakerUpdateDelay < Configuration.MIN_MATCHMAKER_UPDATE_DELAY)
        {
            throw new Exception($"Matchmaker update delay too low, " +
                $"must be >= {Configuration.MIN_MATCHMAKER_UPDATE_DELAY} seconds");
        }

        if (config.DirectorUpdateDelay < Configuration.MIN_DIRECTOR_UPDATE_DELAY)
        {
            throw new Exception($"Director update delay too low, " +
                $"must be >= {Configuration.MIN_DIRECTOR_UPDATE_DELAY} seconds");
        }

        if (config.DirectorUpdateDelay >= config.MaxDowntimeBeforeOffline)
        {
            throw new Exception($"Director update delay should be lower than max downtime before considered offline");
        }

        if (config.MatchmakerMinGatherTime < 0)
        {
            throw new Exception("Matchmaker min gather time can not be negative!");
        }

        if (config.MatchmakerPoolCapacity < 10)
        {
            throw new Exception("Matchmaker pool capacity should be bigger than 10");
        }

        if (config.MaxMatchFailures <= 0)
        {
            throw new Exception("Matchmaker max match failures should be bigger than 0");
        }
    }
}
