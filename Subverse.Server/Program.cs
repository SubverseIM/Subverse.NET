using Subverse.Server;
using Subverse.Abstractions.Server;

var builder = Host.CreateApplicationBuilder(args);

// Helpers
builder.Services.AddSingleton<IPgpKeyProvider, PgpKeyProvider>();

// Mission-critical
builder.Services.AddSingleton<IHubService, RoutedHubService>();
// TODO: Implement ICookieStorage with local db
// TODO: Implement IMessageQueue with local db

// Main
builder.Services.AddHostedService<QuicListenerService>();
// TODO: Add this as a Windows service too

var host = builder.Build();
host.Run();
