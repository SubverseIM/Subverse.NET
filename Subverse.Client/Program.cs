using PgpCore;
using Subverse;
using Subverse.Implementations;
using Subverse.Models;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += Console_CancelKeyPress;

void Console_CancelKeyPress(object? sender, ConsoleCancelEventArgs e)
{
    cts.Cancel();
    e.Cancel = true;
}

var publicKeyFile = new FileInfo("session_pub.asc");
var privateKeyFile = new FileInfo("session_prv.asc");
var privateKeyPassPhrase = RandomNumberGenerator.GetHexString(10);

using (var pgp = new PGP())
{
    await pgp.GenerateKeyAsync(
        publicKeyFileInfo: publicKeyFile,
        privateKeyFileInfo: privateKeyFile,
        username: Environment.MachineName,
        password: privateKeyPassPhrase
        );
}

Console.WriteLine("Generated Node Session Key (NSK)!");

QuicHubConnection hubConnection;
SubverseNode nodeSelf = new SubverseNode(new());
Console.WriteLine($"Connecting to Subverse Network using DNS endpoint: {args[0]}");
try
{
#pragma warning disable CA1416 // Validate platform compatibility
    var quicConnection = await QuicConnection.ConnectAsync(
        new QuicClientConnectionOptions
        {
            RemoteEndPoint = new DnsEndPoint(args[0], 30603),

            DefaultStreamErrorCode = 0x0A, // Protocol-dependent error code.
            DefaultCloseErrorCode = 0x0B, // Protocol-dependent error code.

            ClientAuthenticationOptions = new()
            {
                ApplicationProtocols = new List<SslApplicationProtocol>() { new("SubverseV1") },
                TargetHost = args[0],
            },

            MaxInboundBidirectionalStreams = 10,
        }, cts.Token);
#pragma warning restore CA1416 // Validate platform compatibility

    hubConnection = new QuicHubConnection(quicConnection, publicKeyFile,
        privateKeyFile, privateKeyPassPhrase);

    await hubConnection.CompleteHandshakeAsync(nodeSelf);
    if (hubConnection.ConnectionId is not null && hubConnection.ServiceId is not null)
    {
        nodeSelf = new SubverseNode(new(hubConnection.ConnectionId.Value));
    }
    else
    {
        throw new InvalidOperationException("Could not establish connection to hub service!!");
    }

    Console.WriteLine($"Connected to hub successfully! Using ServiceId: {hubConnection.ServiceId}");
    hubConnection.MessageReceived += HubConnection_MessageReceived;

    while (!cts.IsCancellationRequested)
    {
        var pingMsg = new SubverseMessage(
            [hubConnection.ServiceId.Value, hubConnection.ConnectionId.Value],
            QuicEntityConnection.DEFAULT_CONFIG_START_TTL,
            Encoding.UTF8.GetBytes("\0SubverseV1::Command::PING")
            );
        await hubConnection.SendMessageAsync(pingMsg);
        await Task.Delay(5000, cts.Token);
    }

    hubConnection.Dispose();
}
catch (OperationCanceledException) { }

void HubConnection_MessageReceived(object? sender, Subverse.Abstractions.MessageReceivedEventArgs e)
{    
    Console.WriteLine($"Message from [{e.Message.Tags[0]}]:\n\"{Encoding.UTF8.GetString(e.Message.Content)}\"");
}