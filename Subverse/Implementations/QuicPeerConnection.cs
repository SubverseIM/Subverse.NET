using Alethic.Kademlia;
using Newtonsoft.Json;
using Newtonsoft.Json.Bson;
using Subverse.Abstractions;
using Subverse.Models;
using System.Collections.Concurrent;
using System.Net.Quic;

namespace Subverse.Implementations
{
    public class QuicPeerConnection : IPeerConnection
    {
        public static int DEFAULT_CONFIG_START_TTL = 99;

        private readonly QuicConnection _quicConnection;

        private readonly ConcurrentDictionary<KNodeId160, QuicStream> _quicStreamMap;
        private readonly ConcurrentDictionary<KNodeId160, CancellationTokenSource> _ctsMap;
        private readonly ConcurrentDictionary<KNodeId160, Task> _taskMap;

        private readonly TaskCompletionSource<SubverseMessage> _initialMessageSource;

        private bool disposedValue;

        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;

        public QuicPeerConnection(QuicConnection quicConnection)
        {
            _quicConnection = quicConnection;

            _quicStreamMap = new();
            _ctsMap = new();
            _taskMap = new();

            _initialMessageSource = new();
        }

        public async Task<KNodeId160> CompleteHandshakeAsync(SubverseMessage? message, CancellationToken cancellationToken)
        {
            QuicStream newQuicStream;
            KNodeId160 recipient;
            if (message is null)
            {
                newQuicStream = await _quicConnection
                    .AcceptInboundStreamAsync(cancellationToken);

                CancellationTokenSource newCts = new CancellationTokenSource();
                Task newTask = RecieveAsync(newQuicStream, newCts.Token);

                SubverseMessage initialMessage = await _initialMessageSource.Task;
                recipient = initialMessage.Recipient;

                _ = _ctsMap.AddOrUpdate(initialMessage.Recipient, newCts,
                        (key, oldCts) =>
                        {
                            oldCts.Dispose();
                            return newCts;
                        });

                _ = _taskMap.AddOrUpdate(initialMessage.Recipient, newTask,
                    (key, oldTask) =>
                    {
                        try
                        {
                            oldTask.Wait();
                        }
                        catch (OperationCanceledException) { }
                        return newTask;
                    });
            }
            else
            {
                newQuicStream = await _quicConnection.OpenOutboundStreamAsync(
                    QuicStreamType.Unidirectional, cancellationToken);
                recipient = message.Recipient;
            }

            _ = _quicStreamMap.AddOrUpdate(recipient, newQuicStream,
                    (key, oldQuicStream) =>
                    {
                        oldQuicStream.Dispose();
                        return newQuicStream;
                    });

            if (message is not null) 
            {
                await SendMessageAsync(message, cancellationToken);
            }

            return recipient;
        }

        protected virtual void OnMessageRecieved(MessageReceivedEventArgs ev)
        {
            MessageReceived?.Invoke(this, ev);
        }

        internal Task RecieveAsync(QuicStream quicStream, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                using (var bsonReader = new BsonDataReader(quicStream) { CloseInput = false, SupportMultipleContent = true })
                {
                    var serializer = new JsonSerializer() { TypeNameHandling = TypeNameHandling.Objects, Converters = { new NodeIdConverter() } };
                    while (!cancellationToken.IsCancellationRequested && !quicStream.WritesClosed.IsCompleted)
                    {
                        var message = serializer.Deserialize<SubverseMessage>(bsonReader)
                            ?? throw new InvalidOperationException("Expected to recieve SubverseMessage, got malformed data instead!");

                        _initialMessageSource.TrySetResult(message);
                        OnMessageRecieved(new MessageReceivedEventArgs(message));

                        cancellationToken.ThrowIfCancellationRequested();
                        bsonReader.Read();
                    }
                }
            }, cancellationToken);
        }

        public void SendMessage(SubverseMessage message)
        {
            QuicStream quicStream = _quicStreamMap[message.Recipient];
            lock (quicStream)
            {
                using (var bsonWriter = new BsonDataWriter(quicStream) { CloseOutput = false, AutoCompleteOnClose = true })
                {
                    var serializer = new JsonSerializer() { TypeNameHandling = TypeNameHandling.Auto, Converters = { new NodeIdConverter() } };
                    serializer.Serialize(bsonWriter, message);
                }
            }
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
                            cts.Dispose();
                        }

                        Task.WhenAll(_taskMap.Values).Wait();
                    }
                    finally
                    {
                        foreach (var (_, quicStream) in _quicStreamMap)
                        {
                            quicStream.Dispose();
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
    }
}
