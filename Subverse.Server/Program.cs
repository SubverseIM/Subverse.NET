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
    const string categoryName = "Any";
    return loggerFactory.CreateLogger(categoryName);
});

// Helpers
builder.Services.AddSingleton<IPgpKeyProvider, PgpKeyProvider>();
builder.Services.AddSingleton<IStunUriProvider, AlwaysOnlineStunUriProvider>();

// Kademlia
builder.Services.AddSingleton<IKMessageFormat<KNodeId256>, KJsonMessageFormat<KNodeId256>>();
builder.Services.AddSingleton<IKService, KRefresher<KNodeId256>>();
builder.Services.AddSingleton<IKConnector<KNodeId256>, KConnector<KNodeId256>>();
builder.Services.AddSingleton<IKInvoker<KNodeId256>, KInvoker<KNodeId256>>();
builder.Services.AddSingleton<IKInvokerPolicy<KNodeId256>,KInvokerPolicy<KNodeId256>>();
builder.Services.AddSingleton<IKRequestHandler<KNodeId256>, KRequestHandler<KNodeId256>>();
builder.Services.AddSingleton<IKRouter<KNodeId256>, KFixedTableRouter<KNodeId256>>();
builder.Services.AddSingleton<IKLookup<KNodeId256>, KLookup<KNodeId256>>();
builder.Services.AddSingleton<IKValueAccessor<KNodeId256>, KValueAccessor<KNodeId256>>();
builder.Services.AddSingleton<IKStore<KNodeId256>, KInMemoryStore<KNodeId256>>();
builder.Services.AddSingleton<IKPublisher<KNodeId256>, KInMemoryPublisher<KNodeId256>>();
builder.Services.AddSingleton<IKHost<KNodeId256>, KHost<KNodeId256>>();
builder.Services.AddSingleton<IKProtocol<KNodeId256>, KUdpProtocol<KNodeId256>>();
builder.Services.AddSingleton<IKService, KStaticDiscovery<KNodeId256>>();

builder.Services.Configure<KHostOptions<KNodeId256>>(options => { options.NetworkId = 30603; });
builder.Services.Configure<KUdpOptions>(options => { options.Bind = new IPEndPoint(IPAddress.Any, 30603); });

// Mission-critical
builder.Services.AddSingleton<IHubService, RoutedHubService>();
builder.Services.AddSingleton<IMessageQueue<string>, PersistentMessageQueue>();
builder.Services.AddSingleton<ICookieStorage<KNodeId256>, KCookieStorage>();

// Main
builder.Services.AddHostedService<KHostedService>();
builder.Services.AddHostedService<QuicListenerService>();
builder.Services.AddHostedService<HubBootstrapService>();

// TODO: Add this as a Windows service too

var host = builder.Build();
host.Run();
