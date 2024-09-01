using Hangfire;
using Hangfire.MemoryStorage;

using Subverse.Abstractions;
using Subverse.Server;

var builder = Host.CreateApplicationBuilder(args);

// Hangfire
GlobalConfiguration.Configuration
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseColouredConsoleLogProvider()
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseMemoryStorage();

// Logging
builder.Services.AddTransient(provider =>
{
    var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
    const string categoryName = "ALL";
    return loggerFactory.CreateLogger(categoryName);
});

// Helpers
builder.Services.AddSingleton<IPgpKeyProvider, PgpKeyProvider>();
builder.Services.AddSingleton<IStunUriProvider, AlwaysOnlineStunUriProvider>();

// Mission-critical
builder.Services.AddSingleton<IPeerService, RoutedPeerService>();
builder.Services.AddSingleton<IMessageQueue<string>, PersistentMessageQueue>();

// Main
builder.Configuration.AddEnvironmentVariables("Subverse_");
builder.Services.AddHostedService<QuicListenerService>();
builder.Services.AddHostedService<PeerBootstrapService>();

// Windows-specific
builder.Services.AddWindowsService(options => 
{
    options.ServiceName = "Subverse.NET Hub Service";
});

var host = builder.Build();
host.Run();
