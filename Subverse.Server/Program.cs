using Alethic.Kademlia;
using Alethic.Kademlia.InMemory;
using Alethic.Kademlia.Json;
using Alethic.Kademlia.Network.Udp;

using Hangfire;
using Hangfire.MemoryStorage;

using Subverse.Abstractions;
using Subverse.Server;

using System.Net;

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
builder.Services.AddSingleton<IPeerService, RoutedHubService>();
builder.Services.AddSingleton<IMessageQueue<string>, PersistentMessageQueue>();

// Main
builder.Configuration.AddEnvironmentVariables("Subverse_");
builder.Services.AddHostedService<QuicListenerService>();

// Windows-specific
builder.Services.AddWindowsService(options => 
{
    options.ServiceName = "Subverse.NET Hub Service";
});

var host = builder.Build();
host.Run();
