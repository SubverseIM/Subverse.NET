using Subverse;
using Subverse.Server;

var builder = Host.CreateApplicationBuilder(args);

// Helpers
builder.Configuration.AddEnvironmentVariables("Subverse_");
builder.Services.AddSingleton<IPgpKeyProvider, PgpKeyProvider>();

// Mission-critical
builder.Services.AddHostedService<RoutedPeerService>();
builder.Services.AddHostedService<NatTunnelService>();

// Windows-specific
builder.Services.AddWindowsService(options => 
{
    options.ServiceName = "Subverse.NET Hub Service";
});

var host = builder.Build();
host.Run();
