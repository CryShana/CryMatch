global using Serilog;
global using CryMatch.Core;
global using CryMatch.Core.Enums;
global using CryMatch.Core.Interfaces;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(theme: Serilog.Sinks.SystemConsole.Themes.AnsiConsoleTheme.Code)
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    #if DEBUG
    .MinimumLevel.Debug()
    #endif
    .CreateLogger();

try
{
    var app = Setup.BuildApp(args);

    Setup.ConfigureApp(app);

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal("App error: {error}", ex.Message);
}