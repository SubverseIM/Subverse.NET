using PgpCore;
using Subverse.Implementations;
using Subverse.Models;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;

using static Subverse.Models.SubverseMessage;
using static Subverse.Implementations.QuicEntityConnection;
using SIPSorcery.SIP;
using System.Globalization;
using System.Collections.Concurrent;
using Alethic.Kademlia;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += Console_CancelKeyPress;
void Console_CancelKeyPress(object? sender, ConsoleCancelEventArgs e)
{
    cts.Cancel();
    e.Cancel = true;
}

var publicKeyFile = new FileInfo("session_pub.asc");
var privateKeyFile = new FileInfo("session_prv.asc");
var privateKeyPassPhrase = File.Exists(".password") ?
    File.ReadAllText(".password") :
    RandomNumberGenerator.GetHexString(10);

if (!File.Exists(".password"))
{
    using (var pgp = new PGP())
    {
        await pgp.GenerateKeyAsync(
            publicKeyFileInfo: publicKeyFile,
            privateKeyFileInfo: privateKeyFile,
            username: Environment.MachineName,
            password: privateKeyPassPhrase
            );
    }
    File.WriteAllText(".password", privateKeyPassPhrase);
}

Console.WriteLine("Generated Node Session Key (NSK)!");

SIPTransport? sipTransport = null;
SIPChannel? sipChannel = null;
QuicHubConnection? hubConnection = null;

void ProcessMessageReceived(object? sender, Subverse.Abstractions.MessageReceivedEventArgs e)
{
    Console.WriteLine($"Message from [{e.Message.Tags[0]}]:\n\"{Encoding.UTF8.GetString(e.Message.Content)}\"");
    switch (e.Message.Code)
    {
        case ProtocolCode.Application:
            ProcessSipMessage(e.Message);
            break;
        case ProtocolCode.Command:
            ProcessCommandMessage(e.Message);
            break;
    }
}

void ProcessCommandMessage(SubverseMessage message)
{
    string command = Encoding.UTF8.GetString(message.Content);
    switch (command)
    {
        case "PING":
            _ = hubConnection.SendMessageAsync(
                new SubverseMessage(
                    [
                        hubConnection.ConnectionId.GetValueOrDefault(),
                        hubConnection.ServiceId.GetValueOrDefault()
                    ],
                    DEFAULT_CONFIG_START_TTL, ProtocolCode.Command,
                    Encoding.UTF8.GetBytes("PONG")
                    ));
            break;
    }
}

var callerMap = new ConcurrentDictionary<string, string>();

void ProcessSipMessage(SubverseMessage message)
{
    SIPRequest? request = null;
    try
    {
        request = SIPRequest.ParseSIPRequest(Encoding.UTF8.GetString(message.Content));
        callerMap.TryAdd(request.Header.CallId, request.Header.From.FromURI.User);
    }
    catch (SIPValidationException) { }

    sipTransport.SendRawAsync(sipChannel.ListeningSIPEndPoint,
        new SIPEndPoint(SIPProtocolsEnum.udp, IPAddress.Loopback, 5060),
        message.Content).Wait();
}

// Solution from: https://stackoverflow.com/a/321404
// Adapted for increased performance
static byte[] StringToByteArray(string hex)
{
    return Enumerable.Range(0, hex.Length)
                     .Where(x => (x & 1) == 0)
                     .Select(x => byte.Parse(hex.AsSpan().Slice(x, 2), NumberStyles.HexNumber))
                     .ToArray();
}

async Task SIPTransportResponseReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPResponse sipResponse)
{
    await hubConnection.SendMessageAsync(
                new SubverseMessage(
                    [
                        hubConnection.ConnectionId.GetValueOrDefault(),
                        new(StringToByteArray(callerMap[sipResponse.Header.CallId]))
                    ],
                    DEFAULT_CONFIG_START_TTL, ProtocolCode.Application,
                    sipResponse.GetBytes()
                    ));
}

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
    if (hubConnection.ServiceId is not null && hubConnection.ConnectionId is not null)
    {
        nodeSelf = new SubverseNode(new(hubConnection.ServiceId.Value));
    }
    else
    {
        throw new InvalidOperationException("Could not establish connection to hub service!!");
    }

    sipChannel = new SIPUDPChannel(IPAddress.Any, 0);
    sipTransport = new SIPTransport(true);
    sipTransport.AddSIPChannel(sipChannel);

    Console.WriteLine($"Connected to hub successfully! Using ConnectionId: {hubConnection.ConnectionId}");
    hubConnection.MessageReceived += ProcessMessageReceived;
    sipTransport.SIPTransportResponseReceived += SIPTransportResponseReceived;
    sipTransport.SIPTransportRequestReceived += SIPTransportRequestReceived;

    while (!cts.IsCancellationRequested) { await Task.Delay(5000, cts.Token); }
}
catch (OperationCanceledException) { }
finally
{
    hubConnection?.Dispose();
}

async Task SIPTransportRequestReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPRequest sipRequest)
{
    await hubConnection.SendMessageAsync(
                new SubverseMessage(
                    [
                        hubConnection.ConnectionId.GetValueOrDefault(),
                        new(StringToByteArray(sipRequest.Header.To.ToURI.User))
                    ],
                    DEFAULT_CONFIG_START_TTL, ProtocolCode.Application,
                    sipRequest.GetBytes()
                    ));
}