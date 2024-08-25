using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// Main
builder.Configuration.AddEnvironmentVariables("Subverse_");
builder.Services.AddHostedService<ClientHostedService>();

// Windows-specific
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "Subverse.NET Client Service";
});

var host = builder.Build();
host.Run();
