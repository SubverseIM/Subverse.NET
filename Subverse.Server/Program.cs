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

// Helpers
builder.Configuration.AddEnvironmentVariables("Subverse_");
builder.Services.AddSingleton<IPgpKeyProvider, PgpKeyProvider>();

// Mission-critical
builder.Services.AddSingleton<IPeerService, RoutedPeerService>();
builder.Services.AddSingleton<IMessageQueue<string>, PersistentMessageQueue>();

// Main
builder.Services.AddHostedService<QuicListenerService>();
builder.Services.AddHostedService<PeerBootstrapService>();
builder.Services.AddHostedService<NatTunnelService>();

// Windows-specific
builder.Services.AddWindowsService(options => 
{
    options.ServiceName = "Subverse.NET Hub Service";
});

var host = builder.Build();
host.Run();
