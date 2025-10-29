﻿using System.Buffers;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text;

namespace Alpaca.Markets;

internal sealed class WebSocketsTransport(
    Func<IWebSocket> webSocketFactory,
    Uri url) : IDisposable
{
    private sealed class DuplexPipe : IDuplexPipe
    {
        private DuplexPipe(PipeReader reader, PipeWriter writer)
        {
            Input = reader;
            Output = writer;
        }

        public PipeReader Input { get; }

        public PipeWriter Output { get; }

        public static DuplexPipePair CreateConnectionPair(
            PipeOptions inputOptions,
            PipeOptions outputOptions)
        {
            var input = new Pipe(inputOptions);
            var output = new Pipe(outputOptions);

            var transportToApplication = new DuplexPipe(output.Reader, input.Writer);
            var applicationToTransport = new DuplexPipe(input.Reader, output.Writer);

            return new DuplexPipePair(applicationToTransport, transportToApplication);
        }

        // This class exists to work around issues with value tuple on .NET Framework
        public readonly record struct DuplexPipePair(
            IDuplexPipe Transport, IDuplexPipe Application);
    }

    private Task _running = Task.CompletedTask;

    private CancellationTokenSource _cancellationTokenSource;

    private SpinLock _sendLock = new(false);

    private volatile Boolean _aborted;

    private IDuplexPipe? _application;

    private IDuplexPipe? _transport;

    private IWebSocket? _webSocket;

    public event Action? Opened;

    public event Action? Closed;

    public event Action<String, Byte[]>? MessageReceived;

    public event Action<Byte[]>? DataReceived;

    public event Action<Exception>? Error;

    [SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "Expected behavior - we report exceptions via Error event.")]
    public async Task StartAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            _webSocket = webSocketFactory();
            if (_webSocket is null)
            {
                Error?.Invoke(new InvalidOperationException(
                    "Unable to create web socket using custom web socket factory."));
                return;
            }

            var options = new PipeOptions(
                writerScheduler: PipeScheduler.Inline,
                readerScheduler: PipeScheduler.Inline,
                useSynchronizationContext: false);
            var pair = DuplexPipe.CreateConnectionPair(options, options);

            _transport = pair.Transport;
            _application = pair.Application;

            await _webSocket.ConnectAsync(url, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
            _webSocket?.Dispose();
            return;
        }

        Opened?.Invoke();
        _running = processSocketAsync(_webSocket, _application, _transport);
    }

    [SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "Expected behavior - we report exceptions via Error event.")]
    public async Task StopAsync(
        CancellationToken cancellationToken = default)
    {
        if (_transport is not null)
        {
            await _transport.Output.CompleteAsync().ConfigureAwait(false);
            await _transport.Input.CompleteAsync().ConfigureAwait(false);
        }

        // Cancel any pending reads from the application, this should start the entire shutdown process
        _application?.Input.CancelPendingRead();

        try
        {
            var taskCompletionSource = new TaskCompletionSource<Boolean>();
            cancellationToken.Register(() => taskCompletionSource.TrySetCanceled());
            await Task.WhenAny(_running, taskCompletionSource.Task)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // exceptions have been handled in the Running task continuation by closing the channel with the exception
            Trace.TraceInformation(exception.Message);
        }
        finally
        {
            _webSocket?.Dispose();
        }
    }

    public async ValueTask SendAsync(
        String message,
        CancellationToken cancellationToken)
    {
        await SendAsync(Encoding.UTF8.GetBytes(message), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SendAsync(
        Byte[] message,
        CancellationToken cancellationToken)
    {
        if (_transport is null)
        {
            return;

        }

        var lockTaken = false;
        _sendLock.TryEnter(ref lockTaken);
        if (!lockTaken)
        {
            return;
        }

        try
        {
            await _transport.Output
                .WriteAsync(message, cancellationToken)
                .ConfigureAwait(false);
            await _transport.Output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Exit(false);
        }
    }

    public async ValueTask SendAsync(
        byte[] binaryData,
        CancellationToken cancellationToken)
    {
        if (_transport is null)
        {
            return;

        }

        var lockTaken = false;
        _sendLock.TryEnter(ref lockTaken);
        if (!lockTaken)
        {
            return;
        }

        try
        {
            await _transport.Output
                .WriteAsync(binaryData, cancellationToken)
                .ConfigureAwait(false);
            await _transport.Output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Exit(false);
        }
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _webSocket?.Dispose();
    }

    private Task processSocketAsync(
        IWebSocket socket,
        IDuplexPipe application,
        IDuplexPipe transport)
    {
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = new();
        var keepRunning = default(bool);
        return Task.Factory.StartNew(async () =>
        {
            do
            {
                try
                {
                    using (socket)
                    {
                        // Begin sending and receiving.
                        var receiving = startReceiving(socket, application, transport);
                        var sending = startSending(socket, application);

                        // Wait for send or receive to complete
                        var trigger = await Task.WhenAny(receiving, sending)
                            .ConfigureAwait(false);

                        if (trigger == receiving)
                        {
                            // We're waiting for the application to finish and there are 2 things it could be doing
                            // 1. Waiting for application data
                            // 2. Waiting for a websocket send to complete

                            // Cancel the application so that ReadAsync yields
                            application.Input.CancelPendingRead();

                            using var delayCts = new CancellationTokenSource();

                            var resultTask = await Task.WhenAny(sending,
                                    Task.Delay(TimeSpan.FromSeconds(30), delayCts.Token))
                                .ConfigureAwait(false);

                            if (resultTask != sending)
                            {
                                _aborted = true;
                                Error?.Invoke(new TimeoutException("The operation timed out while attempting to send data to the client. " +
                                    "The connection has been aborted to prevent hanging."));
                                // Abort the websocket if we're stuck in a pending send to the client
                                socket.Abort();
                            }
                            else
                            {
                                // Cancel the timeout
#if NET8_0_OR_GREATER
                                await delayCts.CancelAsync().ConfigureAwait(false);
#else
                                delayCts.Cancel();
#endif
                            }
                        }
                        else
                        {
                            // We're waiting on the websocket to close and there are 2 things it could be doing
                            // 1. Waiting for websocket data
                            // 2. Waiting on a flush to complete (back pressure being applied)
                            _aborted = true;

                            // Abort the websocket if we're stuck in a pending reception from the client
                            socket.Abort();

                            // Cancel any pending flush so that we can quit
                            application.Output.CancelPendingFlush();
                        }

                        Closed?.Invoke();
                        // Gracefully exit the loop after successful completion
                        keepRunning = false;
                    }
                }
                catch (Exception exception)
                {
                    Error?.Invoke(exception);
                    // Continue the loop after handling the error
                    keepRunning = true;
                }
            } while (keepRunning && !_cancellationTokenSource.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)));
        }, _cancellationTokenSource.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    [SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "Expected behavior - we report exceptions via Error event.")]
    private async Task startReceiving(
        IWebSocket socket,
        IDuplexPipe application,
        IDuplexPipe transport)
    {
        try
        {
            while (await startReceivingImpl(socket, application, transport).ConfigureAwait(false)) { }
        }
        catch (OperationCanceledException exception)
        {
            Error?.Invoke(exception);
            Trace.TraceInformation(exception.Message);
        }
        catch (WebSocketException exception) when
            (exception.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            Error?.Invoke(exception);
            Trace.TraceInformation(exception.Message);
        }
        catch (Exception exception)
        {
            Error?.Invoke(exception);
            if (!_aborted)
            {
                await application.Output.CompleteAsync(exception).ConfigureAwait(false);
                Trace.TraceInformation(exception.Message);
            }
        }
        finally
        {
            // We're done writing
            await application.Output.CompleteAsync().ConfigureAwait(false);
        }
    }

    private async Task<Boolean> startReceivingImpl(
        IWebSocket socket,
        IDuplexPipe application,
        IDuplexPipe transport)
    {
        var memory = application.Output.GetMemory();
        var (webSocketMessageType, endOfMessage, count) =
            await socket.ReceiveAsync(memory).ConfigureAwait(false);

        if (isReceiveResultClose(webSocketMessageType, socket.CloseStatus))
        {
            return false;
        }

        application.Output.Advance(count);

        var flushResult = await application.Output.FlushAsync().ConfigureAwait(false);

        // We canceled in the middle of applying back pressure
        // or if the consumer is done
        if (flushResult.IsCanceled || flushResult.IsCompleted)
        {
            return false;
        }

        if (!endOfMessage)
        {
            return true;
        }

        var readResult = await transport.Input.ReadAsync().ConfigureAwait(false);

        // ReSharper disable once ConvertIfStatementToSwitchStatement
        if (webSocketMessageType == WebSocketMessageType.Binary)
        {
            DataReceived?.Invoke(readResult.Buffer.ToArray());
        }
        else if (webSocketMessageType == WebSocketMessageType.Text)
        {
            var bytes = readResult.Buffer.ToArray();
            MessageReceived?.Invoke(Encoding.UTF8.GetString(bytes), bytes);
        }
        else
        {
            Trace.WriteLine($"This WS message type will be ignored: {webSocketMessageType}");
        }

        transport.Input.AdvanceTo(readResult.Buffer.End);
        return true;
    }

    private static Boolean isReceiveResultClose(
        WebSocketMessageType messageType,
        WebSocketCloseStatus? closeStatus)
    {
        if (messageType != WebSocketMessageType.Close)
        {
            return false;
        }

        return closeStatus != WebSocketCloseStatus.NormalClosure
            ? throw new InvalidOperationException($"Websocket closed with error: {closeStatus}.")
            : true;
    }

    [SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "Expected behavior - we report exceptions via Error event.")]
    private async Task startSending(
        IWebSocket socket,
        IDuplexPipe application)
    {
        Exception? error = null;

        try
        {
            while (await startSendingImpl(socket, application).ConfigureAwait(false)) { }
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
            error = ex;
        }
        finally
        {
            await closeWebSocketAsync(socket, application, error).ConfigureAwait(false);
        }
    }

    [SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "Expected behavior - we report exceptions via OnError event.")]
    private async Task<Boolean> startSendingImpl(
        IWebSocket socket,
        IDuplexPipe application)
    {
        var result = await application.Input.ReadAsync().ConfigureAwait(false);
        var buffer = result.Buffer;

        // Get a frame from the application
        try
        {
            if (result.IsCanceled)
            {
                return false;
            }

            if (buffer.IsEmpty)
            {
                return !result.IsCompleted;
            }

            try
            {
                if (webSocketCanSend(socket))
                {
                    await socket.SendAsync(buffer).ConfigureAwait(false);
                }
                else
                {
                    return false;
                }
            }
            catch (Exception exception)
            {
                if (!_aborted)
                {
                    Trace.TraceInformation(exception.Message);
                }
                return false;
            }

            return !result.IsCompleted;
        }
        finally
        {
            application.Input.AdvanceTo(buffer.End);
        }
    }

    [SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "Expected behavior - we report exceptions via OnError event.")]
    private static async Task closeWebSocketAsync(
        IWebSocket socket,
        IDuplexPipe application,
        Exception? error)
    {
        if (webSocketCanSend(socket))
        {
            try
            {
                // We're done sending, send the close frame to the client if the websocket is still open
                await socket.CloseOutputAsync(error is not null
                            ? WebSocketCloseStatus.InternalServerError
                            : WebSocketCloseStatus.NormalClosure)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Trace.TraceInformation(exception.Message);
            }
        }

        await application.Input.CompleteAsync().ConfigureAwait(false);
    }

    private static Boolean webSocketCanSend(
        IWebSocket ws) =>
        ws.State is not (
            WebSocketState.Aborted or
            WebSocketState.Closed or
            WebSocketState.CloseSent);
}
