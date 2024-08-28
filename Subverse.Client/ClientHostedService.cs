using Alethic.Kademlia;
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

        SubverseNode nodeSelf = new SubverseNode(new());
        QuicHubConnection? hubConnection = null;

        EncryptionKeys myEntityKeys = new EncryptionKeys(privateKeyFile, privateKeyPassPhrase);
        ConcurrentDictionary<KNodeId160, TaskCompletionSource<EncryptionKeys>> entityKeysSources = new();

        async void ProcessMessageReceived(object? sender, Subverse.Abstractions.MessageReceivedEventArgs e)
        {
            Console.WriteLine($"Message from [{e.Message.Tags[0]}]:\n\"{Encoding.UTF8.GetString(e.Message.Content)}\"");
            switch (e.Message.Code)
            {
                case ProtocolCode.Application:
                    await ProcessSipMessageAsync(e.Message);
                    break;
                case ProtocolCode.Command:
                    await ProcessCommandMessageAsync(e.Message);
                    break;
                case ProtocolCode.Entity:
                    await ProcessEntityMessageAsync(e.Message);
                    break;
            }
        }

        async Task ProcessEntityMessageAsync(SubverseMessage message)
        {
            CertificateCookie theirCookie;
            TaskCompletionSource<EncryptionKeys>? entityKeysSource;

            if (!entityKeysSources.TryGetValue(message.Tags[0], out entityKeysSource))
            {
                entityKeysSource = new TaskCompletionSource<EncryptionKeys>();
                entityKeysSources.TryAdd(message.Tags[0], entityKeysSource);
            }

            theirCookie = (CertificateCookie)CertificateCookie.FromBlobBytes(message.Content);
            if (!entityKeysSource.TrySetResult(theirCookie.KeyContainer)) { return; }

            LocalCertificateCookie myCookie = new LocalCertificateCookie(publicKeyFile.OpenRead(), myEntityKeys, nodeSelf);
            await hubConnection.SendMessageAsync(new SubverseMessage(
                    [hubConnection.LocalConnectionId.GetValueOrDefault(), message.Tags[0]],
                    DEFAULT_CONFIG_START_TTL, ProtocolCode.Entity, myCookie.ToBlobBytes()
                    ));

        }

        async Task ProcessCommandMessageAsync(SubverseMessage message)
        {
            string command = Encoding.UTF8.GetString(message.Content);
            switch (command)
            {
                case "PING":
                    await hubConnection.SendMessageAsync(
                        new SubverseMessage(
                            [
                                hubConnection.LocalConnectionId.GetValueOrDefault(),
                                hubConnection.RemoteConnectionId.GetValueOrDefault()
                            ],
                            DEFAULT_CONFIG_START_TTL, ProtocolCode.Command,
                            Encoding.UTF8.GetBytes("PONG")
                            ));
                    break;
            }
        }

        var callerMap = new ConcurrentDictionary<string, string>();

        async Task ProcessSipMessageAsync(SubverseMessage message)
        {
            byte[] messageBytes;
            using (var pgp = new PGP(myEntityKeys))
            using (var bufferStream = new MemoryStream(message.Content))
            using (var decryptStream = new MemoryStream())
            {
                await pgp.DecryptAsync(bufferStream, decryptStream);
                messageBytes = decryptStream.ToArray();
            }

            SIPRequest? request = null;
            try
            {
                request = SIPRequest.ParseSIPRequest(Encoding.UTF8.GetString(messageBytes));
                request.Header.From.FromURI.Host = "subverse";

                messageBytes = request.GetBytes();

                string fromEntityStr = request.Header.From.FromURI.User;
                callerMap.TryAdd(request.Header.CallId, fromEntityStr);
            }
            catch (SIPValidationException) { }

            await sipTransport.SendRawAsync(
                sipChannel.ListeningSIPEndPoint,
                new SIPEndPoint(SIPProtocolsEnum.udp, IPAddress.Loopback, 50600),
                messageBytes
                );
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

        async Task<EncryptionKeys> GetEntityKeysAsync(KNodeId160 entityId) 
        {
            TaskCompletionSource<EncryptionKeys>? entityKeysSource;

            if (!entityKeysSources.TryGetValue(entityId, out entityKeysSource))
            {
                LocalCertificateCookie myCookie = new LocalCertificateCookie(
                    publicKeyFile.OpenRead(), myEntityKeys, nodeSelf);

                await hubConnection.SendMessageAsync(new SubverseMessage(
                        [hubConnection.LocalConnectionId.GetValueOrDefault(), entityId],
                        DEFAULT_CONFIG_START_TTL, ProtocolCode.Entity, myCookie.ToBlobBytes()
                        ));

                entityKeysSource = entityKeysSources.GetOrAdd(entityId,
                    new TaskCompletionSource<EncryptionKeys>());
            }

            return await entityKeysSource.Task;
        }

        async Task SIPTransportRequestReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPRequest sipRequest)
        {
            string toEntityStr = sipRequest.Header.To.ToURI.User;
            KNodeId160 toEntityId = new(StringToByteArray(toEntityStr));

            EncryptionKeys entityKeys = await GetEntityKeysAsync(toEntityId);

            using (var pgp = new PGP(entityKeys))
            using (var bufferStream = new MemoryStream(sipRequest.GetBytes()))
            using (var encryptStream = new MemoryStream())
            {
                await pgp.EncryptAsync(bufferStream, encryptStream);

                await hubConnection.SendMessageAsync(
                    new SubverseMessage(
                        [hubConnection.LocalConnectionId.GetValueOrDefault(), toEntityId],
                        DEFAULT_CONFIG_START_TTL, ProtocolCode.Application,
                        encryptStream.ToArray()
                        ));
            }
        }

        async Task SIPTransportResponseReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPResponse sipResponse)
        {
            if(!callerMap.TryGetValue(
                sipResponse.Header.CallId, 
                out string? fromEntityStr)) 
            {
                return;
            }

            KNodeId160 fromEntityId = new(StringToByteArray(fromEntityStr));
            EncryptionKeys entityKeys = await GetEntityKeysAsync(fromEntityId);

            using (var pgp = new PGP(entityKeys))
            using (var bufferStream = new MemoryStream(sipResponse.GetBytes()))
            using (var encryptStream = new MemoryStream())
            {
                await pgp.EncryptAsync(bufferStream, encryptStream);

                await hubConnection.SendMessageAsync(
                    new SubverseMessage(
                        [hubConnection.LocalConnectionId.GetValueOrDefault(), fromEntityId],
                        DEFAULT_CONFIG_START_TTL, ProtocolCode.Application,
                        encryptStream.ToArray()
                        ));
            }
        }

        var hostname = _configuration.GetSection("Client")
            .GetValue<string>("Hostname") ?? "localhost";

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
                if (hubConnection.RemoteConnectionId is not null && hubConnection.LocalConnectionId is not null)
                {
                    nodeSelf = new SubverseNode(new(hubConnection.RemoteConnectionId.Value));
                }
                else
                {
                    throw new InvalidOperationException("Could not establish connection to hub service!!");
                }

                sipChannel = new SIPUDPChannel(IPAddress.Loopback, 5060);
                sipTransport = new SIPTransport(true, Encoding.UTF8, Encoding.UTF8);
                sipTransport.AddSIPChannel(sipChannel);

                Console.WriteLine($"Connected to hub successfully! Using ConnectionId: {hubConnection.LocalConnectionId}");
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