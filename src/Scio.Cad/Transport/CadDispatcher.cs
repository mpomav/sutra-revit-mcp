using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using Scio.Cad.Core;

namespace Scio.Cad.Transport
{
    internal delegate JObject CadCommandHandler(JObject args);

    /// <summary>
    /// Marshals incoming named-pipe commands onto AutoCAD's main UI thread,
    /// serialized one at a time in FIFO order (matching Scio.Revit's Dispatcher).
    /// </summary>
    internal sealed class CadDispatcher
    {
        private readonly Dispatcher _uiDispatcher;
        private readonly Dictionary<string, CadCommandHandler> _handlers =
            new Dictionary<string, CadCommandHandler>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _executionGate = new SemaphoreSlim(1, 1);

        public CadDispatcher(Dispatcher uiDispatcher)
        {
            _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        }

        public void Register(string command, CadCommandHandler handler)
        {
            _handlers[command] = handler;
        }

        public List<string> CommandNames()
        {
            var list = new List<string>(_handlers.Keys);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        public JObject Execute(string id, string command, JObject args, int timeoutMs)
        {
            if (!_handlers.TryGetValue(command ?? "", out CadCommandHandler handler))
            {
                return Envelope(id, false, null, "Unknown CAD command '" + command + "'.");
            }

            if (!_executionGate.Wait(timeoutMs))
            {
                return Envelope(id, false, null, "Timed out waiting for AutoCAD's command queue (" + timeoutMs + " ms).");
            }

            try
            {
                JObject data = null;
                Exception error = null;

                using (var done = new ManualResetEventSlim(false))
                {
                    _uiDispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            data = handler(args ?? new JObject());
                        }
                        catch (Exception ex)
                        {
                            error = ex;
                        }
                        finally
                        {
                            done.Set();
                        }
                    }), DispatcherPriority.Normal);

                    if (!done.Wait(timeoutMs))
                    {
                        CadLog.Warn("Command '" + command + "' timed out on AutoCAD UI thread after " + timeoutMs + " ms.");
                        return Envelope(id, false, null, "AutoCAD did not finish '" + command + "' within " + timeoutMs + " ms (a modal dialog may be open).");
                    }
                }

                if (error != null)
                {
                    CadLog.Error("Command '" + command + "' failed", error);
                    return Envelope(id, false, null, error.Message);
                }

                return Envelope(id, true, data ?? new JObject(), null);
            }
            finally
            {
                _executionGate.Release();
            }
        }

        private static JObject Envelope(string id, bool success, JObject data, string error)
        {
            return new JObject
            {
                ["id"] = id,
                ["success"] = success,
                ["data"] = data,
                ["error"] = error
            };
        }
    }
}
