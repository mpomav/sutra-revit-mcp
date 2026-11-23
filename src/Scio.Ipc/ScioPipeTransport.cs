using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Scio.Abstractions;

namespace Scio.Ipc
{
    /// <summary>
    /// Async Named Pipe server hosted inside Revit to receive ToolRequests from Scio.Server.
    /// </summary>
    public sealed class ScioPipeServer : IDisposable
    {
        private readonly string _pipeName;
        private readonly Func<ToolRequest, CancellationToken, Task<ToolResponse>> _handler;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task? _listenTask;

        public ScioPipeServer(string pipeName, Func<ToolRequest, CancellationToken, Task<ToolResponse>> handler)
        {
            _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public void Start()
        {
            if (_listenTask != null) return;
            _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
        }

        private async Task ListenLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    var accepted = server;
                    server = null;

                    _ = Task.Run(() => HandleClientAsync(accepted, ct), ct);
                }
                catch (OperationCanceledException)
                {
                    server?.Dispose();
                    break;
                }
                catch
                {
                    server?.Dispose();
                    try { await Task.Delay(100, ct).ConfigureAwait(false); } catch { break; }
                }
            }
        }

        private async Task HandleClientAsync(NamedPipeServerStream stream, CancellationToken ct)
        {
            using (stream)
            {
                while (stream.IsConnected && !ct.IsCancellationRequested)
                {
                    ToolRequest? request;
                    try
                    {
                        request = await PipeFraming.ReadMessageAsync<ToolRequest>(stream, ct).ConfigureAwait(false);
                        if (request == null) break;
                    }
                    catch
                    {
                        break;
                    }

                    ToolResponse response;
                    try
                    {
                        response = await _handler(request, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        response = ToolResponse.Failure(request.RequestId, "unhandled_exception", ex.Message);
                    }

                    try
                    {
                        await PipeFraming.WriteMessageAsync(stream, response, ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        break;
                    }
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listenTask?.Wait(500); } catch { }
            _cts.Dispose();
        }
    }

    /// <summary>
    /// Async Named Pipe client used by Scio.Server to invoke tools inside a live Revit session.
    /// </summary>
    public sealed class ScioPipeClient
    {
        private readonly string _pipeName;

        public ScioPipeClient(string pipeName)
        {
            _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
        }

        public async Task<ToolResponse> SendAsync(ToolRequest request, int connectTimeoutMs = 5000, CancellationToken ct = default)
        {
            using (var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeoutCts.CancelAfter(Math.Max(request.TimeoutMs, connectTimeoutMs));

                try
                {
                    await client.ConnectAsync(connectTimeoutMs, timeoutCts.Token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    return ToolResponse.Failure(
                        request.RequestId,
                        "revit_unreachable",
                        $"Timed out connecting to Revit IPC pipe '{_pipeName}'. Ensure Autodesk Revit is open with SCIO Revit MCP v4 loaded.");
                }
                catch (Exception ex)
                {
                    return ToolResponse.Failure(
                        request.RequestId,
                        "ipc_connect_failed",
                        $"Failed to connect to Revit IPC pipe '{_pipeName}': {ex.Message}");
                }

                await PipeFraming.WriteMessageAsync(client, request, timeoutCts.Token).ConfigureAwait(false);
                var response = await PipeFraming.ReadMessageAsync<ToolResponse>(client, timeoutCts.Token).ConfigureAwait(false);
                return response ?? ToolResponse.Failure(request.RequestId, "empty_response", "Revit closed the IPC connection without a reply.");
            }
        }
    }
}
