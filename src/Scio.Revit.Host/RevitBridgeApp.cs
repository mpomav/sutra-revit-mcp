using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Ipc;
using Scio.Revit.Sdk;

namespace Scio.Revit.Host
{
    /// <summary>
    /// Bounded FIFO ExternalEvent dispatcher executing IRevitCommands safely on Revit's UI thread.
    /// </summary>
    public sealed class RevitCommandDispatcher : IExternalEventHandler
    {
        public const int MaxQueueDepth = 16;

        private sealed class PendingWork
        {
            public ToolRequest Request { get; }
            public TaskCompletionSource<ToolResponse> Completion { get; }
            public Stopwatch EnqueuedAt { get; } = Stopwatch.StartNew();

            public PendingWork(ToolRequest request)
            {
                Request = request;
                Completion = new TaskCompletionSource<ToolResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private readonly Dictionary<string, IRevitCommand> _commands = new Dictionary<string, IRevitCommand>(StringComparer.Ordinal);
        private readonly ConcurrentQueue<PendingWork> _queue = new ConcurrentQueue<PendingWork>();
        private readonly int _revitYear;
        private ExternalEvent? _externalEvent;
        private int _queuedCount;

        public PermissionProfile PermissionProfile { get; set; } = PermissionProfile.SafeWrite;

        public RevitCommandDispatcher(int revitYear)
        {
            _revitYear = revitYear;
            Register(new HealthCommand(this));
        }

        public void AttachExternalEvent(ExternalEvent externalEvent)
        {
            _externalEvent = externalEvent ?? throw new ArgumentNullException(nameof(externalEvent));
        }

        public void Register(IRevitCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            _commands[command.Descriptor.Name] = command;
        }

        public IReadOnlyList<ToolDescriptor> ListDescriptors() =>
            _commands.Values.Select(c => c.Descriptor).OrderBy(d => d.Name, StringComparer.Ordinal).ToList();

        public string ContractHash => JsonSchemaBuilder.ComputeContractHash(ListDescriptors());

        public Task<ToolResponse> EnqueueAsync(ToolRequest request, CancellationToken ct)
        {
            if (_externalEvent == null)
                return Task.FromResult(ToolResponse.Failure(request.RequestId, "host_not_ready", "Revit ExternalEvent dispatcher is not attached."));

            if (Interlocked.Increment(ref _queuedCount) > MaxQueueDepth)
            {
                Interlocked.Decrement(ref _queuedCount);
                return Task.FromResult(ToolResponse.Refusal(request.RequestId, "queue_full", $"Revit bridge queue is full (max {MaxQueueDepth})."));
            }

            var work = new PendingWork(request);
            _queue.Enqueue(work);
            _externalEvent.Raise();

            if (ct.CanBeCanceled)
            {
                ct.Register(() => work.Completion.TrySetCanceled(ct));
            }

            return work.Completion.Task;
        }

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var work))
            {
                Interlocked.Decrement(ref _queuedCount);
                if (work.Completion.Task.IsCanceled) continue;

                long queuedMs = work.EnqueuedAt.ElapsedMilliseconds;
                var execSw = Stopwatch.StartNew();
                ToolResponse reply;

                try
                {
                    if (!_commands.TryGetValue(work.Request.Tool, out var command))
                    {
                        reply = ToolResponse.Refusal(work.Request.RequestId, "unknown_tool", $"Tool '{work.Request.Tool}' is not registered in Revit Host.");
                    }
                    else if (PermissionProfile == PermissionProfile.ReadOnly && !command.Descriptor.ReadOnly)
                    {
                        reply = ToolResponse.Refusal(work.Request.RequestId, "write_not_authorized", $"Tool '{work.Request.Tool}' requires SafeWrite or UnsafeCode permission.");
                    }
                    else
                    {
                        var ctx = new RevitContext(app, PermissionProfile, _revitYear);
                        reply = command.Execute(ctx, work.Request);
                    }
                }
                catch (Exception ex)
                {
                    reply = ToolResponse.Failure(work.Request.RequestId, "revit_command_exception", ex.Message);
                }

                reply.QueuedMs = queuedMs;
                reply.ElapsedMs = execSw.ElapsedMilliseconds;
                work.Completion.TrySetResult(reply);
            }
        }

        public string GetName() => "SCIO Revit MCP v4 Dispatcher";

        [ScioTool("scio_health", "core", "Check SCIO Revit MCP v4 bridge health, active document, loaded modules, and contract hash.", ReadOnly = true)]
        private sealed class HealthCommand : IRevitCommand
        {
            private readonly RevitCommandDispatcher _owner;

            public HealthCommand(RevitCommandDispatcher owner) => _owner = owner;

            public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(HealthCommand));

            public ToolResponse Execute(RevitContext context, ToolRequest request)
            {
                var doc = context.ActiveDocument;
                var data = new JObject
                {
                    ["status"] = "healthy",
                    ["product"] = "SCIO Revit MCP",
                    ["version"] = "4.0.0-alpha.1",
                    ["revit_year"] = context.RevitYear,
                    ["permission_profile"] = context.PermissionProfile.ToString(),
                    ["contract_hash"] = _owner.ContractHash,
                    ["registered_tools"] = _owner._commands.Count,
                    ["active_document"] = doc == null ? null : new JObject
                    {
                        ["title"] = doc.Title,
                        ["path"] = doc.PathName,
                        ["is_workshared"] = doc.IsWorkshared,
                        ["is_family_document"] = doc.IsFamilyDocument
                    }
                };
                return ToolResponse.Success(request.RequestId, data, $"Healthy on Revit {context.RevitYear} ({_owner._commands.Count} tools).");
            }
        }
    }

    /// <summary>
    /// Clean-room Revit ExternalApplication entry point for SCIO Revit MCP v4.
    /// </summary>
    public sealed class RevitBridgeApp : IExternalApplication
    {
        private ScioPipeServer? _pipeServer;
        private RevitCommandDispatcher? _dispatcher;
        private int _revitYear;
        private int _pid;

        public Result OnStartup(UIControlledApplication application)
        {
            _pid = Process.GetCurrentProcess().Id;
            int.TryParse(application.ControlledApplication.VersionNumber, out _revitYear);
            if (_revitYear == 0) _revitYear = 2026;

            _dispatcher = new RevitCommandDispatcher(_revitYear);
            var externalEvent = ExternalEvent.Create(_dispatcher);
            _dispatcher.AttachExternalEvent(externalEvent);

            string pipeName = SessionRegistry.BuildPipeName(_revitYear, _pid);
            _pipeServer = new ScioPipeServer(pipeName, _dispatcher.EnqueueAsync);
            _pipeServer.Start();

            SessionRegistry.WriteBeacon(new RevitSessionBeacon
            {
                PipeName = pipeName,
                Pid = _pid,
                RevitYear = _revitYear,
                Version = "4.0.0-alpha.1",
                ContractHash = _dispatcher.ContractHash
            });

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            SessionRegistry.RemoveBeacon(_revitYear, _pid);
            _pipeServer?.Dispose();
            _pipeServer = null;
            return Result.Succeeded;
        }
    }
}
