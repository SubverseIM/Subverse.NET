using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using PgpCore;
using SIPSorcery.SIP;
using Subverse.Implementations;
using Subverse.Models;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;

using static Subverse.Implementations.QuicEntityConnection;
using static Subverse.Models.SubverseMessage;

internal class ClientHostedService : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;

    public ClientHostedService(IConfiguration configuration, IHostEnvironment environment)
    {
        _configuration = configuration;
        _environment = environment;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var passwordFilePath = Path.Combine(_environment.ContentRootPath, ".password");

        var publicKeyFile = new FileInfo(Path.Combine(_environment.ContentRootPath, "session_pub.asc"));
        var privateKeyFile = new FileInfo(Path.Combine(_environment.ContentRootPath, "session_prv.asc"));
        var privateKeyPassPhrase = File.Exists(passwordFilePath) ?
            File.ReadAllText(passwordFilePath) :
            RandomNumberGenerator.GetHexString(10);

        if (!File.Exists(passwordFilePath))
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
            File.WriteAllText(passwordFilePath, privateKeyPassPhrase);
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

        var hostname = _configuration.GetSection("Client").GetValue<string>("Hostname") ?? "localhost";
        SubverseNode nodeSelf = new SubverseNode(new());
        Console.WriteLine($"Connecting to Subverse Network using DNS endpoint: {hostname}");
        do
        {
            try
            {
#pragma warning disable CA1416 // Validate platform compatibility
                var quicConnection = await QuicConnection.ConnectAsync(
                    new QuicClientConnectionOptions
                    {
                        RemoteEndPoint = new DnsEndPoint(hostname, 30603),

                        DefaultStreamErrorCode = 0x0A, // Protocol-dependent error code.
                        DefaultCloseErrorCode = 0x0B, // Protocol-dependent error code.

                        ClientAuthenticationOptions = new()
                        {
                            ApplicationProtocols = new List<SslApplicationProtocol>() { new("SubverseV1") },
                            TargetHost = hostname,
                        },

                        MaxInboundBidirectionalStreams = 10,
                    }, stoppingToken);
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

                sipChannel = new SIPUDPChannel(IPAddress.Loopback, 5059);
                sipTransport = new SIPTransport(true, Encoding.UTF8, Encoding.Unicode);
                sipTransport.AddSIPChannel(sipChannel);

                Console.WriteLine($"Connected to hub successfully! Using ConnectionId: {hubConnection.ConnectionId}");
                hubConnection.MessageReceived += ProcessMessageReceived;
                sipTransport.SIPTransportResponseReceived += SIPTransportResponseReceived;
                sipTransport.SIPTransportRequestReceived += SIPTransportRequestReceived;

                await (hubConnection.EntityReceiveTask?.WaitAsync(stoppingToken) ?? Task.CompletedTask);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { }
            finally
            {
                hubConnection?.Dispose();
                sipTransport?.Shutdown();
            }
        } while (!stoppingToken.IsCancellationRequested);
    }
}