using Newtonsoft.Json;
using Newtonsoft.Json.Bson;
using Quiche.NET;
using Subverse.Abstractions;
using Subverse.Implementations;
using Subverse.Models;
using Subverse.Types;
using System.Collections.Concurrent;

namespace Subverse.Server
{
    public class QuichePeerConnection : IPeerConnection
    {
        public static int DEFAULT_CONFIG_START_TTL = 99;

        private readonly QuicheConnection _connection;

        private readonly ConcurrentDictionary<SubversePeerId, QuicheStream> _streamMap;
        private readonly ConcurrentDictionary<SubversePeerId, CancellationTokenSource> _ctsMap;
        private readonly ConcurrentDictionary<SubversePeerId, Task> _taskMap;

        private readonly TaskCompletionSource<SubverseMessage> _initialMessageSource;

        private bool disposedValue;

        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;

        public QuichePeerConnection(QuicheConnection connection)
        {
            _connection = connection;

            _streamMap = new();

            _ctsMap = new();
            _taskMap = new();

            _initialMessageSource = new();
        }

        private QuicheStream? GetBestPeerStream(SubversePeerId peerId)
        {
            QuicheStream? quicheStream;
            if (!_streamMap.TryGetValue(peerId, out quicheStream))
            {
                quicheStream = _streamMap.Values.SingleOrDefault();
            }
            return quicheStream;
        }

        private Task RecieveAsync(QuicheStream quicheStream, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                using var bsonReader = new BsonDataReader(quicheStream)
                {
                    CloseInput = false,
                    SupportMultipleContent = true,
                };

                var serializer = new JsonSerializer()
                {
                    TypeNameHandling = TypeNameHandling.Objects,
                    Converters = { new PeerIdConverter() },
                };

                if (!quicheStream.CanRead) throw new NotSupportedException();

                while (!cancellationToken.IsCancellationRequested && quicheStream.CanRead)
                {
                    var message = serializer.Deserialize<SubverseMessage>(bsonReader)
                        ?? throw new InvalidOperationException(
                            "Expected to recieve SubverseMessage, " +
                            "got malformed data instead!");

                    _initialMessageSource.TrySetResult(message);
                    OnMessageRecieved(new MessageReceivedEventArgs(message));

                    cancellationToken.ThrowIfCancellationRequested();
                    bsonReader.Read();
                }
            }, cancellationToken);
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
                        foreach (var (_, cts) in _ctsMap)
                        {
                            if (!cts.IsCancellationRequested)
                            {
                                cts.Dispose();
                            }
                        }

                        Task.WhenAll(_taskMap.Values).Wait();
                    }
                    catch (AggregateException ex) when (ex.InnerExceptions.All(
                        x => x is QuicheException ||
                        x is NotSupportedException ||
                        x is OperationCanceledException))
                    { }
                    finally
                    {
                        foreach (var (_, stream) in _streamMap)
                        {
                            stream.Dispose();
                        }
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

        public async Task<SubversePeerId> CompleteHandshakeAsync(SubverseMessage? message, CancellationToken cancellationToken)
        {
            QuicheStream quicheStream;
            SubversePeerId recipient;

            await _connection.ConnectionEstablished.WaitAsync(cancellationToken);

            if (message is not null)
            {
                quicheStream = await _connection.CreateOutboundStreamAsync(QuicheStream.Direction.Bidirectional, cancellationToken);
                SendMessage(message, quicheStream);

                recipient = message.Recipient;
            }
            else
            {
                quicheStream = await _connection.AcceptInboundStreamAsync(cancellationToken);

                CancellationTokenSource newCts = new();
                Task newTask = RecieveAsync(quicheStream, newCts.Token);

                SubverseMessage initialMessage = await _initialMessageSource.Task.WaitAsync(cancellationToken);
                recipient = initialMessage.Recipient;

                _ = _ctsMap.AddOrUpdate(recipient, newCts,
                    (key, oldCts) =>
                    {
                        if (!oldCts.IsCancellationRequested)
                        {
                            oldCts.Dispose();
                        }

                        return newCts;
                    });

                _ = _taskMap.AddOrUpdate(recipient, newTask,
                    (key, oldTask) =>
                    {
                        try
                        {
                            oldTask.Wait();
                        }
                        catch (AggregateException ex) when (ex.InnerExceptions.All(
                            x => x is QuicheException ||
                            x is NotSupportedException ||
                            x is OperationCanceledException))
                        { }

                        return newTask;
                    });
            }

            _ = _streamMap.AddOrUpdate(recipient, quicheStream,
                (key, oldQuicheStream) =>
                {
                    oldQuicheStream.Dispose();
                    return quicheStream;
                });

            return recipient;
        }

        public void SendMessage(SubverseMessage message) => SendMessage(message, null);

        private void SendMessage(SubverseMessage message, QuicheStream? quicheStream)
        {
            quicheStream = quicheStream ?? GetBestPeerStream(message.Recipient);
            if (quicheStream is null)
            {
                throw new InvalidOperationException("Suitable transport for this message could not be found.");
            }
            else if (!quicheStream.CanWrite)
            {
                throw new NotSupportedException("Stream cannot be written to at this time.");
            }

            lock (quicheStream)
            {
                var bsonWriter = new BsonDataWriter(quicheStream)
                {
                    CloseOutput = false,
                    AutoCompleteOnClose = true,
                };

                using (bsonWriter)
                {
                    var serializer = new JsonSerializer()
                    {
                        TypeNameHandling = TypeNameHandling.Auto,
                        Converters = { new PeerIdConverter() },
                    };
                    serializer.Serialize(bsonWriter, message);
                }

                quicheStream.Flush();
            }
        }

        public bool HasValidConnectionTo(SubversePeerId peerId)
        {
            QuicheStream? quicheStream = GetBestPeerStream(peerId);
            return quicheStream?.CanRead & quicheStream?.CanWrite ?? false;
        }
    }
}
