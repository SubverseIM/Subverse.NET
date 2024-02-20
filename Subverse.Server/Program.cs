using Subverse.Server;
using Subverse.Abstractions.Server;
using Hangfire;
using Hangfire.MemoryStorage;
using Subverse.Abstractions;

var builder = Host.CreateApplicationBuilder(args);

// Hangfire
GlobalConfiguration.Configuration
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseColouredConsoleLogProvider()
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseMemoryStorage();

// Helpers
builder.Services.AddSingleton<IPgpKeyProvider, PgpKeyProvider>();

// Mission-critical
builder.Services.AddSingleton<IHubService, RoutedHubService>();
builder.Services.AddSingleton<IMessageQueue<string>, PersistentMessageQueue>();
// Big TODO: Implement ICookieStorage<KNodeId256> with Subverse.Kademlia

// Main
builder.Services.AddHostedService<QuicListenerService>();
// TODO: Add this as a Windows service too

var host = builder.Build();
host.Run();
