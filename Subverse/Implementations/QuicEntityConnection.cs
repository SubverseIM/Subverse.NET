using Alethic.Kademlia;
using Subverse.Abstractions;
using Subverse.Exceptions;
using Subverse.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Bson;
using Org.BouncyCastle.Bcpg;
using PgpCore;
using System.Net.Quic;
using System.Security.Cryptography;

namespace Subverse
{
#pragma warning disable CA1416 // Validate platform compatibility
    public class QuicEntityConnection : IEntityConnection
    {
        private readonly QuicStream _quicStream;
        private readonly FileInfo _pgpKeyFile;
        private readonly string _pgpKeyPassPhrase;

        private CancellationTokenSource? _cts;
        private Task? _receiveTask;

        private bool disposedValue;

        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;

        public KNodeId256? ServiceId { get; internal set; }
        public KNodeId256? ConnectionId { get; internal set; }

        public QuicEntityConnection(QuicStream quicStream, FileInfo pgpKeyFile, string pgpKeyPassPhrase)
        {
            _quicStream = quicStream;

            _pgpKeyFile = pgpKeyFile;
            _pgpKeyPassPhrase = pgpKeyPassPhrase;
        }

        public Task CompleteHandshakeAsync()
        {
            return Task.Run(() =>
            {
                // Initiate handshake by exporting our public key to the remote party
                using (var armoredOut = new ArmoredOutputStream(_quicStream))
                {
                    var myKeys = new EncryptionKeys(_pgpKeyFile, _pgpKeyPassPhrase);
                    myKeys.PublicKey.Encode(armoredOut);

                    ServiceId = new(myKeys.PublicKey.GetFingerprint());
                }

                // Continue handshake by storing their public key after they do the same
                EncryptionKeys challengeKeys;
                using (var pgpKeyFileStream = _pgpKeyFile.OpenRead())
                {
                    challengeKeys = new EncryptionKeys(_quicStream, pgpKeyFileStream, _pgpKeyPassPhrase);
                }
                ConnectionId = new(challengeKeys.PublicKey.GetFingerprint());

                // Generate nonce
                byte[] originalNonce = RandomNumberGenerator.GetBytes(64);

                // Encrypt/sign nonce and send it to remote party
                using (var inputNonceStream = new MemoryStream(originalNonce))
                using (var pgp = new PGP(challengeKeys))
                {
                    pgp.EncryptAndSign(inputNonceStream, _quicStream);
                }

                // IMPLICIT: other party receives encrypted nonce, decrypts/verifies, and encrypts/signs to send back to us.

                byte[] receivedNonce;
                using (var outputNonceStream = new MemoryStream())
                using (var pgp = new PGP(challengeKeys))
                {
                    pgp.DecryptAndVerify(_quicStream, outputNonceStream);
                    receivedNonce = outputNonceStream.ToArray();
                }

                if (!originalNonce.SequenceEqual(receivedNonce))
                {
                    throw new InvalidEntityException($"Connection to entity with ID: \"{ConnectionId}\" could not be verified as authentic!");
                }

                _cts = new CancellationTokenSource();
                _receiveTask = RecieveAsync(_cts.Token);
            });
        }

        internal Task RecieveAsync(CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                using (var reader = new BsonDataReader(_quicStream))
                {
                    var serializer = new JsonSerializer() { TypeNameHandling = TypeNameHandling.Auto };
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var message = serializer.Deserialize<SubverseMessage>(reader)
                            ?? throw new InvalidOperationException("Expected to recieve SubverseMessage, got malformed data instead!");
                        OnMessageRecieved(new MessageReceivedEventArgs(message));
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
            }, cancellationToken);
        }

        public Task SendMessageAsync(SubverseMessage message)
        {
            using (var writer = new BsonDataWriter(_quicStream))
            {
                var serializer = new JsonSerializer() { TypeNameHandling = TypeNameHandling.Auto };
                serializer.Serialize(writer, message);
            }

            return Task.CompletedTask;
        }

        protected virtual void OnMessageRecieved(MessageReceivedEventArgs ev)
        {
            MessageReceived?.Invoke(this, ev);
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
                        _receiveTask?.Wait();
                    }
                    finally
                    {
                        _quicStream.Dispose();
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
#pragma warning restore CA1416 // Validate platform compatibility
}
