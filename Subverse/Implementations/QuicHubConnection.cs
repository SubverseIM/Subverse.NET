using Alethic.Kademlia;
using Subverse.Abstractions;
using Subverse.Models;
using Org.BouncyCastle.Bcpg;
using PgpCore;
using System.Net.Quic;

namespace Subverse.Implementations
{
    public class QuicHubConnection : IEntityConnection
    {
        private readonly QuicConnection _quicConnection;
        private readonly FileInfo _pgpKeyFile;
        private readonly string _pgpKeyPassPhrase;

        private QuicEntityConnection? _entityConnection;
        private CancellationTokenSource? _cts;
        private Task? _entityReceiveTask;

        private bool disposedValue;

        public KNodeId256? ServiceId { get; private set; }
        public KNodeId256? ConnectionId { get; private set; }

        public QuicHubConnection(QuicConnection quicConnection, FileInfo pgpKeyFile, string pgpKeyPassPhrase)
        {
            _quicConnection = quicConnection;
            _pgpKeyFile = pgpKeyFile;
            _pgpKeyPassPhrase = pgpKeyPassPhrase;
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
                if(_entityConnection is not null)
                {
                    _entityConnection.MessageReceived -= value;
                }
            }
        }

        public async Task CompleteHandshakeAsync()
        {
#pragma warning disable CA1416 // Validate platform compatibility
            var quicStream = await _quicConnection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
#pragma warning restore CA1416 // Validate platform compatibility

            // Accpet handshake by storing their public key
            EncryptionKeys challengeKeys;
            using (var pgpKeyFileStream = _pgpKeyFile.OpenRead())
            {
                challengeKeys = new EncryptionKeys(quicStream, pgpKeyFileStream, _pgpKeyPassPhrase);
            }
            ConnectionId = new(challengeKeys.PublicKey.GetFingerprint());

            // Send my own public key to the other party
            using (var armoredOut = new ArmoredOutputStream(quicStream))
            {
                var myKeys = new EncryptionKeys(_pgpKeyFile, _pgpKeyPassPhrase);
                myKeys.PublicKey.Encode(armoredOut);

                ServiceId = new(myKeys.PublicKey.GetFingerprint());
            }

            // Receive nonce from other party, decrypt/verify it
            byte[] receivedNonce;
            using (var outputNonceStream = new MemoryStream())
            using (var pgp = new PGP(challengeKeys))
            {
                pgp.DecryptAndVerify(quicStream, outputNonceStream);
                receivedNonce = outputNonceStream.ToArray();
            }

            // Encrypt/sign nonce and send it to remote party
            using (var inputNonceStream = new MemoryStream(receivedNonce))
            using (var pgp = new PGP(challengeKeys))
            {
                pgp.EncryptAndSign(inputNonceStream, quicStream);
            }

            _cts = new CancellationTokenSource();
            _entityConnection = new QuicEntityConnection(quicStream, _pgpKeyFile, _pgpKeyPassPhrase) 
                { ServiceId = ServiceId, ConnectionId = ConnectionId };
            _entityReceiveTask = _entityConnection.RecieveAsync(_cts.Token);
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
