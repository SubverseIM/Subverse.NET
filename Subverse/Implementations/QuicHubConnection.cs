using Alethic.Kademlia;
using Subverse.Abstractions;
using Subverse.Models;
using Org.BouncyCastle.Bcpg;
using PgpCore;
using System.Net.Quic;
using System.Text;

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

        public KNodeId160? ServiceId { get; private set; }
        public KNodeId160? ConnectionId { get; private set; }

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

            // Accpet handshake by storing their public key
            EncryptionKeys challengeKeys;
            using (var quicStreamReader = new BinaryReader(quicStream, Encoding.UTF8, true))
            using (var privateKeyStream = _privateKeyFile.OpenRead())
            {
                var keyLength = quicStreamReader.ReadInt32();
                var keyBytes = quicStreamReader.ReadBytes(keyLength);

                using var publicKeyStream = new MemoryStream(keyBytes);
                challengeKeys = new EncryptionKeys(publicKeyStream, privateKeyStream, _privateKeyPassPhrase);
            }
            ConnectionId = new(challengeKeys.PublicKey.GetFingerprint());

            byte[] blobBytes;

            // Send my own public key to the other party
            using (var memoryStream = new MemoryStream())
            using (var publicKeyStream = _publicKeyFile.OpenRead())
            using (var quicStreamWriter = new BinaryWriter(quicStream, Encoding.UTF8, true))
            {
                publicKeyStream.CopyTo(memoryStream);
                publicKeyStream.Position = 0;

                var myKeys = new EncryptionKeys(_publicKeyFile, _privateKeyFile, _privateKeyPassPhrase);

                quicStreamWriter.Write((int)memoryStream.Length);
                quicStreamWriter.Write(memoryStream.ToArray());

                ServiceId = new(myKeys.PublicKey.GetFingerprint());
                blobBytes = new LocalCertificateCookie(publicKeyStream, myKeys, self).ToBlobBytes();
            }

            // Receive nonce from other party, decrypt/verify it
            byte[] receivedNonce;
            using (var quicStreamReader = new BinaryReader(quicStream, Encoding.UTF8, true))
            using (var outputNonceStream = new MemoryStream())
            using (var pgp = new PGP(challengeKeys))
            {
                var nonceLength = quicStreamReader.ReadInt32();
                var nonceBytes = quicStreamReader.ReadBytes(nonceLength);

                using var recievedNonceStream = new MemoryStream(nonceBytes);
                pgp.DecryptAndVerify(recievedNonceStream, outputNonceStream);

                receivedNonce = outputNonceStream.ToArray();
            }

            // Encrypt/sign nonce and send it to remote party
            using (var inputNonceStream = new MemoryStream(receivedNonce))
            using (var sendNonceStream = new MemoryStream())
            using (var quicStreamWriter = new BinaryWriter(quicStream, Encoding.UTF8, true))
            using (var pgp = new PGP(challengeKeys))
            {
                pgp.EncryptAndSign(inputNonceStream, sendNonceStream);
                quicStreamWriter.Write((int)sendNonceStream.Length);
                quicStreamWriter.Write(sendNonceStream.ToArray());
            }

            _cts = new CancellationTokenSource();
            _entityConnection = new QuicEntityConnection(quicStream, _publicKeyFile, _privateKeyFile, _privateKeyPassPhrase)
            { ServiceId = ServiceId, ConnectionId = ConnectionId };
            _entityReceiveTask = _entityConnection.RecieveAsync(_cts.Token);

            // Self-announce to other party
            await _entityConnection.SendMessageAsync(new SubverseMessage([ServiceId.Value], 128, blobBytes));
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
