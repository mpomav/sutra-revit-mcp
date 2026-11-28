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
using Scio.Revit.Ui;

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
            public CancellationTokenRegistration CancelRegistration { get; set; }

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
        public bool IsPaused { get; set; }
        public List<string> LoadedModules { get; } = new List<string>();
        public List<string> ModuleLoadErrors { get; } = new List<string>();

        public RevitCommandDispatcher(int revitYear)
        {
            _revitYear = revitYear;
            Register(new HealthCommand(this));
        }

        public void LoadModules(string? baseDirectory = null)
        {
            var report = RevitModuleLoader.DiscoverAndLoad(baseDirectory);
            LoadedModules.Clear();
            LoadedModules.AddRange(report.LoadedModules);
            ModuleLoadErrors.Clear();
            ModuleLoadErrors.AddRange(report.Errors);
            foreach (var cmd in report.Commands)
            {
                Register(cmd);
            }
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
            if (ct.CanBeCanceled)
            {
                work.CancelRegistration = ct.Register(() => work.Completion.TrySetCanceled(ct));
            }
            _queue.Enqueue(work);
            _externalEvent.Raise();

            return work.Completion.Task;
        }

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var work))
            {
                Interlocked.Decrement(ref _queuedCount);
                work.CancelRegistration.Dispose();
                if (work.Completion.Task.IsCanceled) continue;

                long queuedMs = work.EnqueuedAt.ElapsedMilliseconds;
                var execSw = Stopwatch.StartNew();
                ToolResponse reply;

                try
                {
                    if (string.Equals(work.Request.Tool, "_scio_catalog", StringComparison.Ordinal))
                    {
                        var catalog = new JObject
                        {
                            ["revit_year"] = _revitYear,
                            ["contract_hash"] = ContractHash,
                            ["permission_profile"] = PermissionProfile.ToString(),
                            ["loaded_modules"] = new JArray(LoadedModules),
                            ["tools"] = JArray.FromObject(ListDescriptors())
                        };
                        reply = ToolResponse.Success(work.Request.RequestId, catalog, $"Catalog with {_commands.Count} tools.");
                    }
                    else if (IsPaused && !string.Equals(work.Request.Tool, "scio_health", StringComparison.Ordinal))
                    {
                        reply = ToolResponse.Refusal(work.Request.RequestId, "bridge_paused", "SCIO Revit MCP bridge is currently paused by the user in Revit.");
                    }
                    else if (!_commands.TryGetValue(work.Request.Tool, out var command))
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
                    ["product"] = "Sutra — Revit MCP",
                    ["version"] = RevitBridgeApp.CurrentVersion,
                    ["revit_year"] = context.RevitYear,
                    ["permission_profile"] = context.PermissionProfile.ToString(),
                    ["paused"] = _owner.IsPaused,
                    ["contract_hash"] = _owner.ContractHash,
                    ["registered_tools"] = _owner._commands.Count,
                    ["loaded_modules"] = new JArray(_owner.LoadedModules),
                    ["module_load_errors"] = new JArray(_owner.ModuleLoadErrors),
                    ["active_document"] = doc == null ? null : new JObject
                    {
                        ["title"] = doc.Title,
                        ["path"] = doc.PathName,
                        ["is_workshared"] = doc.IsWorkshared,
                        ["is_family_document"] = doc.IsFamilyDocument
                    }
                };
                var cachedUpdate = SutraUpdateChecker.TryGetCachedUpdate(RevitBridgeApp.CurrentVersion);
                if (cachedUpdate != null && cachedUpdate.UpdateAvailable)
                {
                    data["update_available"] = new JObject
                    {
                        ["latest_version"] = cachedUpdate.LatestVersion,
                        ["download_url"] = cachedUpdate.DownloadUrl,
                        ["release_url"] = cachedUpdate.ReleaseUrl
                    };
                }
                return ToolResponse.Success(request.RequestId, data, $"Healthy on Revit {context.RevitYear} ({_owner._commands.Count} tools).");
            }
        }
    }

    /// <summary>
    /// Clean-room Revit ExternalApplication entry point for SCIO Revit MCP v4.
    /// </summary>
    public sealed class RevitBridgeApp : IExternalApplication
    {
        internal static string CurrentVersion { get; } = ResolveAssemblyVersion();

        private static string ResolveAssemblyVersion()
        {
            var asm = typeof(RevitBridgeApp).Assembly;
            var info = (System.Reflection.AssemblyInformationalVersionAttribute?)Attribute.GetCustomAttribute(
                asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
            if (info != null && !string.IsNullOrWhiteSpace(info.InformationalVersion))
            {
                int plus = info.InformationalVersion.IndexOf('+');
                return plus > 0 ? info.InformationalVersion.Substring(0, plus) : info.InformationalVersion;
            }
            var v = asm.GetName().Version;
            return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.3.0";
        }

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
            _dispatcher.LoadModules();
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
                Version = CurrentVersion,
                ContractHash = _dispatcher.ContractHash
            });

            ScioBridgeUiHooks.GetStatus = () => new ScioBridgeStatusSnapshot
            {
                Version = CurrentVersion,
                RevitYear = _revitYear,
                PipeName = pipeName,
                ContractHash = _dispatcher.ContractHash,
                RegisteredTools = _dispatcher.ListDescriptors().Count,
                PermissionProfile = _dispatcher.PermissionProfile,
                IsPaused = _dispatcher.IsPaused,
                LoadedModules = new List<string>(_dispatcher.LoadedModules)
            };
            ScioBridgeUiHooks.TogglePause = () =>
            {
                _dispatcher.IsPaused = !_dispatcher.IsPaused;
                return _dispatcher.IsPaused;
            };
            ScioBridgeUiHooks.TogglePythonAccess = () =>
            {
                _dispatcher.PermissionProfile = _dispatcher.PermissionProfile == PermissionProfile.UnsafeCode
                    ? PermissionProfile.SafeWrite
                    : PermissionProfile.UnsafeCode;
                return _dispatcher.PermissionProfile;
            };
            ScioBridgeUiHooks.SetPermissionProfile = profile =>
            {
                _dispatcher.PermissionProfile = profile;
                return _dispatcher.PermissionProfile;
            };
            ScioRibbon.Build(application);

            // First-run welcome dialog for strictly opt-in anonymous telemetry,
            // plus a silent 24h-cached background check for newer Sutra releases.
            // Deferred to ApplicationInitialized so Revit's main window is ready
            // and never blocks startup.
            int startupChecked = 0;
            application.ControlledApplication.ApplicationInitialized += (sender, args) =>
            {
                if (Interlocked.Exchange(ref startupChecked, 1) != 0) return;
                try
                {
                    IntPtr owner = IntPtr.Zero;
                    try { owner = Process.GetCurrentProcess().MainWindowHandle; } catch { }

                    bool showedWelcome = false;
                    if (!TelemetryPreferences.HasAnsweredWelcomePrompt())
                    {
                        WelcomeTelemetryDialog.ShowIfFirstRun(owner, CurrentVersion);
                        showedWelcome = true;
                    }

                    // Skip update modal on the very first run to avoid stacking two dialogs,
                    // but still refresh the background cache for subsequent startups / scio_health.
                    UpdateAvailableDialog.CheckInBackground(owner, CurrentVersion, showModal: !showedWelcome);
                }
                catch { /* never fail Revit startup */ }
            };

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
