using Alethic.Kademlia;
using PgpCore;
using Subverse.Abstractions;
using Subverse.Models;
using System.Net.Quic;

using static Subverse.Models.SubverseMessage;
using static Subverse.Implementations.QuicEntityConnection;

namespace Subverse.Implementations
{
    public class QuicHubConnection : IEntityConnection
    {
        private readonly QuicConnection _quicConnection;
        private readonly FileInfo _publicKeyFile, _privateKeyFile;
        private readonly string? _privateKeyPassPhrase;

        private QuicEntityConnection? _entityConnection;
        private CancellationTokenSource? _cts;
        private Task? _entityReceiveTask;

        private bool disposedValue;

        public KNodeId160? ConnectionId { get; private set; }
        public KNodeId160? ServiceId { get; private set; }

        public QuicHubConnection(QuicConnection quicConnection, FileInfo publicKeyFile, FileInfo privateKeyFile, string? privateKeyPassPhrase)
        {
            _quicConnection = quicConnection;
            _publicKeyFile = publicKeyFile;
            _privateKeyFile = privateKeyFile;
            _privateKeyPassPhrase = privateKeyPassPhrase;
        }

        public event EventHandler<MessageReceivedEventArgs>? MessageReceived
        {
            add
            {
                if (_entityConnection is not null)
                {
                    _entityConnection.MessageReceived += value;
                }
            }

            remove
            {
                if (_entityConnection is not null)
                {
                    _entityConnection.MessageReceived -= value;
                }
            }
        }

        public async Task CompleteHandshakeAsync(SubverseEntity self)
        {
#pragma warning disable CA1416 // Validate platform compatibility
            var quicStream = await _quicConnection.AcceptInboundStreamAsync();
#pragma warning restore CA1416 // Validate platform compatibility

            // Accept handshake by storing their public key
            EncryptionKeys challengeKeys;
            using (var privateKeyStream = _privateKeyFile.OpenRead())
            {
                using var publicKeyStream = Utils.ExtractPGPBlockFromStream(quicStream, "PUBLIC KEY BLOCK");
                challengeKeys = new EncryptionKeys(publicKeyStream, privateKeyStream, _privateKeyPassPhrase);
            }
            ServiceId = new(challengeKeys.PublicKey.GetFingerprint());

            byte[] blobBytes;

            // Send my own public key to the other party
            using (var publicKeyStream = _publicKeyFile.OpenRead())
            {
                publicKeyStream.CopyTo(quicStream);
                publicKeyStream.Position = 0;

                var myKeys = new EncryptionKeys(_publicKeyFile, _privateKeyFile, _privateKeyPassPhrase);

                ConnectionId = new(myKeys.PublicKey.GetFingerprint());
                if (self is SubverseNode node)
                {
                    blobBytes = new LocalCertificateCookie(publicKeyStream, myKeys, node with { MostRecentlySeenBy = new(ConnectionId.Value) }).ToBlobBytes();
                }
                else
                {
                    blobBytes = new LocalCertificateCookie(publicKeyStream, myKeys, self).ToBlobBytes();
                }
            }

            // Receive nonce from other party, decrypt/verify it
            byte[] receivedNonce;
            using (var outputNonceStream = new MemoryStream())
            using (var pgp = new PGP(challengeKeys))
            {
                using var receivedNonceStream = Utils.ExtractPGPBlockFromStream(quicStream, "MESSAGE");
                pgp.DecryptAndVerify(receivedNonceStream, outputNonceStream);
                receivedNonce = outputNonceStream.ToArray();
            }

            // Encrypt/sign nonce and send it to remote party
            using (var inputNonceStream = new MemoryStream(receivedNonce))
            using (var sendNonceStream = new MemoryStream())
            using (var pgp = new PGP(challengeKeys))
            {
                pgp.EncryptAndSign(inputNonceStream, sendNonceStream);
                sendNonceStream.Position = 0;
                sendNonceStream.CopyTo(quicStream);
            }

            _cts = new CancellationTokenSource();
            _entityConnection = new QuicEntityConnection(quicStream, _publicKeyFile, _privateKeyFile, _privateKeyPassPhrase)
            { ConnectionId = ConnectionId, ServiceId = ServiceId };
            _entityReceiveTask = _entityConnection.RecieveAsync(_cts.Token);

            // Self-announce to other party
            await _entityConnection.SendMessageAsync(new SubverseMessage([ConnectionId.Value], DEFAULT_CONFIG_START_TTL, ProtocolCode.Entity, blobBytes));
        }

        public Task SendMessageAsync(SubverseMessage message)
        {
            return _entityConnection?.SendMessageAsync(message) ?? Task.CompletedTask;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    try
                    {
                        _cts?.Dispose();
                        _entityReceiveTask?.Wait();
                    }
                    finally
                    {
                        _entityConnection?.Dispose();
                    }
                }

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
