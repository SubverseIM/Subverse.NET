using Newtonsoft.Json;
using Newtonsoft.Json.Bson;
using Subverse.Abstractions;
using Subverse.Implementations;
using Subverse.Models;
using Subverse.Types;
using System.Collections.Concurrent;
using System.Net.Quic;

namespace Subverse.Server
{
    public class QuicPeerConnection : IPeerConnection
    {
        public static int DEFAULT_CONFIG_START_TTL = 99;

        private readonly QuicConnection _quicConnection;

        private readonly ConcurrentDictionary<SubversePeerId, QuicStream> _quicStreamMap;
        private readonly ConcurrentDictionary<SubversePeerId, CancellationTokenSource> _ctsMap;
        private readonly ConcurrentDictionary<SubversePeerId, Task> _taskMap;

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

        public async Task<SubversePeerId> CompleteHandshakeAsync(SubverseMessage? message, CancellationToken cancellationToken)
        {
            QuicStream newQuicStream;
            SubversePeerId recipient;

            CancellationTokenSource newCts;
            Task newTask;

            if (message is null)
            {
                newQuicStream = await _quicConnection.AcceptInboundStreamAsync(cancellationToken);

                newCts = new ();
                newTask = RecieveAsync(newQuicStream, newCts.Token);

                SubverseMessage initialMessage = await _initialMessageSource.Task;
                recipient = initialMessage.Recipient;
            }
            else
            {
                newQuicStream = await _quicConnection.OpenOutboundStreamAsync(
                    QuicStreamType.Bidirectional, cancellationToken);

                newCts = new();
                newTask = RecieveAsync(newQuicStream, newCts.Token);

                recipient = message.Recipient;
            }

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
                    catch (QuicException) { }
                    catch (OperationCanceledException) { }
                    
                    return newTask;
                });

            _ = _quicStreamMap.AddOrUpdate(recipient, newQuicStream,
                (key, oldQuicStream) =>
                {
                    oldQuicStream.Dispose();
                    return newQuicStream;
                });

            if (message is not null) 
            {
                SendMessage(message);
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
                if (!quicStream.CanRead)
                {
                    throw new NotSupportedException();
                }

                using (var bsonReader = new BsonDataReader(quicStream) { CloseInput = false, SupportMultipleContent = true })
                {
                    var serializer = new JsonSerializer() { TypeNameHandling = TypeNameHandling.Objects, Converters = { new PeerIdConverter() } };
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
            if (quicStream.CanWrite)
            {
                lock (quicStream)
                {
                    using (var bsonWriter = new BsonDataWriter(quicStream) { CloseOutput = false, AutoCompleteOnClose = true })
                    {
                        var serializer = new JsonSerializer() { TypeNameHandling = TypeNameHandling.Auto, Converters = { new PeerIdConverter() } };
                        serializer.Serialize(bsonWriter, message);
                    }
                }
            }
            else { throw new NotSupportedException(); }
        }

        public bool HasValidConnectionTo(SubversePeerId peerId) 
        {
            if (_quicStreamMap.TryGetValue(peerId, out QuicStream? quicStream))
            {
                return !quicStream.ReadsClosed.IsCompleted;
            }
            else 
            {
                return false;
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
                            if (!cts.IsCancellationRequested)
                            {
                                cts.Dispose();
                            }
                        }

                        Task.WhenAll(_taskMap.Values).Wait();
                    }
                    catch (QuicException) { }
                    catch (OperationCanceledException) { }
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
