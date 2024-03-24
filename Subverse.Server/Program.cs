using Alethic.Kademlia;
using Alethic.Kademlia.InMemory;
using Alethic.Kademlia.Json;
using Alethic.Kademlia.Network.Udp;

using Hangfire;
using Hangfire.MemoryStorage;

using Subverse.Abstractions;
using Subverse.Abstractions.Server;
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

// Kademlia
builder.Services.AddSingleton<IKMessageFormat<KNodeId160>, KJsonMessageFormat<KNodeId160>>();
builder.Services.AddSingleton<IKService, KRefresher<KNodeId160>>();
builder.Services.AddSingleton<IKConnector<KNodeId160>, KConnector<KNodeId160>>();
builder.Services.AddSingleton<IKInvoker<KNodeId160>, KInvoker<KNodeId160>>();
builder.Services.AddSingleton<IKInvokerPolicy<KNodeId160>,KInvokerPolicy<KNodeId160>>();
builder.Services.AddSingleton<IKRequestHandler<KNodeId160>, KRequestHandler<KNodeId160>>();
builder.Services.AddSingleton<IKRouter<KNodeId160>, KFixedTableRouter<KNodeId160>>();
builder.Services.AddSingleton<IKLookup<KNodeId160>, KLookup<KNodeId160>>();
builder.Services.AddSingleton<IKValueAccessor<KNodeId160>, KValueAccessor<KNodeId160>>();
builder.Services.AddSingleton<IKStore<KNodeId160>, KInMemoryStore<KNodeId160>>();
builder.Services.AddSingleton<IKPublisher<KNodeId160>, KInMemoryPublisher<KNodeId160>>();
builder.Services.AddSingleton<IKHost<KNodeId160>, KHost<KNodeId160>>();
builder.Services.AddSingleton<IKProtocol<KNodeId160>, KUdpProtocol<KNodeId160>>();
builder.Services.AddSingleton<IKService, KStaticDiscovery<KNodeId160>>();

builder.Services.Configure<KHostOptions<KNodeId160>>(options => { options.NetworkId = 30603; });
builder.Services.Configure<KUdpOptions>(options => { options.Bind = new IPEndPoint(IPAddress.Any, 30604); });

// Mission-critical
builder.Services.AddSingleton<IHubService, RoutedHubService>();
builder.Services.AddSingleton<IMessageQueue<string>, PersistentMessageQueue>();
builder.Services.AddSingleton<ICookieStorage<KNodeId160>, KCookieStorage>();

// Main
builder.Configuration.AddEnvironmentVariables("Subverse_");
builder.Services.AddHostedService<QuicListenerService>();
builder.Services.AddHostedService<HubBootstrapService>();
builder.Services.AddHostedService<KHostedService>();

// Windows-specific
builder.Services.AddWindowsService(options => 
{
    options.ServiceName = "Subverse.NET Hub Service";
});

var host = builder.Build();
host.Run();
