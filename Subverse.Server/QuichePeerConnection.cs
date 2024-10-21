using Newtonsoft.Json;
using Newtonsoft.Json.Bson;
using Quiche.NET;
using Subverse.Abstractions;
using Subverse.Implementations;
using Subverse.Models;
using Subverse.Types;
using System.Buffers;
using System.Collections.Concurrent;
using System.Text;

using static Subverse.Models.SubverseMessage;

namespace Subverse.Server
{
    public class QuichePeerConnection : IPeerConnection
    {
        private static readonly ArrayPool<byte> DEFAULT_ARRAY_POOL;

        public static int DEFAULT_CONFIG_START_TTL;

        static QuichePeerConnection()
        {
            DEFAULT_ARRAY_POOL = ArrayPool<byte>.Create();
            DEFAULT_CONFIG_START_TTL = 99;
        }

        private readonly ILogger<QuichePeerConnection> _logger;

        private readonly QuicheConnection _connection;

        private readonly ConcurrentDictionary<SubversePeerId, QuicheStream> _streamMap;
        private readonly ConcurrentDictionary<SubversePeerId, CancellationTokenSource> _ctsMap;
        private readonly ConcurrentDictionary<SubversePeerId, Task> _taskMap;

        private readonly TaskCompletionSource<SubverseMessage> _initialMessageSource;

        private bool disposedValue;

        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;

        public QuichePeerConnection(ILogger<QuichePeerConnection> logger, QuicheConnection connection)
        {
            _logger = logger;
            _connection = connection;

            _streamMap = new();

            _ctsMap = new();
            _taskMap = new();

            _initialMessageSource = new();
        }

        private QuicheStream? GetBestPeerStream(SubversePeerId? peerId)
        {
            QuicheStream? quicheStream;
            if (peerId is null || !_streamMap.TryGetValue(peerId.Value, out quicheStream))
            {
                quicheStream = _streamMap.Values.SingleOrDefault();
            }
            return quicheStream;
        }

        private Task RecieveAsync(QuicheStream quicheStream, CancellationToken cancellationToken)
        {
            return Task.Run(async Task? () =>
            {
                byte[]? rawMessageBytes = null;
                byte[] rawMessageCountBytes = new byte[sizeof(int)];
                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        int rawMessageCount;
                        for (int readCount = 0; readCount < sizeof(int);)
                        {
                            int justRead = quicheStream.Read(rawMessageCountBytes.AsSpan(readCount));
                            readCount += justRead;
                            if (justRead == 0 && quicheStream.CanRead)
                            {
                                await Task.Delay(150, cancellationToken);
                            }
                            else if (!quicheStream.CanRead)
                            {
                                throw new EndOfStreamException("Network stream has reached end of input and is closed.");
                            }
                        }

                        rawMessageCount = BitConverter.ToInt32(rawMessageCountBytes);
                        rawMessageBytes = DEFAULT_ARRAY_POOL.Rent(++rawMessageCount);

                        for (int readCount = 0; readCount < rawMessageCount;)
                        {
                            int justRead = quicheStream.Read(rawMessageBytes.AsSpan(readCount, rawMessageCount - readCount));
                            readCount += justRead;
                            if (justRead == 0 && quicheStream.CanRead)
                            {
                                await Task.Delay(150, cancellationToken);
                            }
                            else if (!quicheStream.CanRead)
                            {
                                throw new EndOfStreamException("Network stream has reached end of input and is closed.");
                            }
                        }

                        using (MemoryStream rawMessageStream = new(rawMessageBytes))
                        using (BsonDataReader bsonReader = new(rawMessageStream))
                        {
                            JsonSerializer serializer = new()
                            {
                                Converters = { new PeerIdConverter() },
                            };

                            var message = serializer.Deserialize<SubverseMessage>(bsonReader) ??
                                    throw new InvalidOperationException("Expected SubverseMessage, got malformed data instead!");
                            if (message.Recipient is not null)
                            {
                                _initialMessageSource.TrySetResult(message);
                            }
                            OnMessageRecieved(new MessageReceivedEventArgs(message));
                        }

                        DEFAULT_ARRAY_POOL.Return(rawMessageBytes);
                        rawMessageBytes = null;
                    }
                }
                catch (OperationCanceledException)
                {
                    _initialMessageSource.TrySetCanceled(cancellationToken);
                    throw;
                }
                catch (Exception ex)
                {
                    _initialMessageSource.TrySetException(ex);
                    _logger.LogError(ex, null);
                    throw;
                }
                finally
                {
                    if (_initialMessageSource.Task.IsCompletedSuccessfully)
                    {
                        _logger.LogInformation($"Stopped receiving from proxy of {_initialMessageSource.Task.Result.Recipient}.");
                    }
                    else
                    {
                        _logger.LogInformation($"Stopped receiving from undesignated proxy.");
                    }

                    if (rawMessageBytes is not null)
                    {
                        DEFAULT_ARRAY_POOL.Return(rawMessageBytes);
                    }
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

            CancellationTokenSource newCts;
            Task newTask;

            SubversePeerId recipient;

            await _connection.ConnectionEstablished.WaitAsync(cancellationToken);

            if (message?.Recipient is not null)
            {
                quicheStream = await _connection.CreateOutboundStreamAsync(QuicheStream.Direction.Bidirectional, cancellationToken);

                SendMessage(message, quicheStream);

                newCts = new();
                newTask = RecieveAsync(quicheStream, newCts.Token);

                recipient = message.Recipient.Value;
            }
            else
            {
                quicheStream = await _connection.AcceptInboundStreamAsync(cancellationToken);

                newCts = new();
                newTask = RecieveAsync(quicheStream, newCts.Token);

                SubverseMessage initialMessage = await _initialMessageSource.Task.WaitAsync(cancellationToken);
                if (initialMessage.Recipient is null)
                {
                    throw new InvalidOperationException("Invalid initial message was received!");
                }
                recipient = initialMessage.Recipient.Value;
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
                    catch (AggregateException ex) when (ex.InnerExceptions.All(
                        x => x is QuicheException ||
                        x is NotSupportedException ||
                        x is OperationCanceledException))
                    { }

                    return newTask;
                });

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

            byte[] rawMessageBytes;
            using (MemoryStream rawMessageStream = new())
            using (BsonDataWriter bsonWriter = new(rawMessageStream))
            {
                JsonSerializer serializer = new()
                {
                    Converters = { new PeerIdConverter() },
                };
                serializer.Serialize(bsonWriter, message);
                rawMessageBytes = rawMessageStream.ToArray();
            }

            lock (quicheStream)
            {
                using BinaryWriter binaryWriter =
                    new(quicheStream, Encoding.UTF8, leaveOpen: true);
                binaryWriter.Write(rawMessageBytes.Length);
                binaryWriter.Write(rawMessageBytes);
                binaryWriter.Write((byte)0);
            }
        }

        public bool HasValidConnectionTo(SubversePeerId? peerId)
        {
            QuicheStream? quicheStream = GetBestPeerStream(peerId);
            return quicheStream?.CanRead & quicheStream?.CanWrite ?? false;
        }
    }
}
