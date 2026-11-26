#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;

namespace Scio.Server
{
    public sealed class ToolRefusalException : Exception
    {
        public JToken Details { get; }

        public ToolRefusalException(string message, JToken details = null) : base(message)
        {
            Details = details;
        }
    }

    /// <summary>
    /// SCIO Revit MCP v4 — Local Code Mode Tool (scio_code_mode).
    /// Orchestrates multiple typed SCIO tools in one turn via CodeModeEngine while
    /// enforcing ServerToolRegistry permissions, tool packs, and read_only gates.
    /// </summary>
    public static class CodeModeTool
    {
        public const string ToolName = "scio_code_mode";
        private const int DefaultMaxToolCalls = 25;
        private const int HardMaxToolCalls = 100;
        private const int DefaultMaxSteps = 50_000;
        private const int HardMaxSteps = 200_000;
        private const int DefaultTimeoutMs = 30_000;
        private const int HardTimeoutMs = 120_000;
        private const int DefaultMaxOutputBytes = 32 * 1024;
        private const int HardMaxOutputBytes = 128 * 1024;

        public static ToolDescriptor CreateDescriptor() => new ToolDescriptor
        {
            Name = ToolName,
            Pack = "core",
            Description =
                "Execute a short sandboxed JS script locally inside Scio.Server to orchestrate multiple SCIO tools " +
                "in one turn (loops, filtering, aggregation, branching) and return only the compact final result. " +
                "Inside the script, use `scio.call(name, args)` (or `call(name, args)` or `scio.<tool>(args)`), " +
                "`scio.search(query)` and `scio.describe(name)`. All SCIO permission, toolset, dry_run and " +
                "verification gates still apply; `scio_execute_python` and recursion are blocked. Set `read_only: true` " +
                "to guarantee zero writes.",
            ReadOnly = false,
            Destructive = true,
            OpenWorld = true,
            ServerSide = true,
            InputSchema = JObject.Parse(@"{""type"":""object"",""properties"":{
""code"":{""type"":""string"",""description"":""JS-subset script to run. Use `return ...;` for the final payload.""},
""inputs"":{""type"":""object"",""description"":""Optional JSON object exposed inside the script as `inputs`.""},
""read_only"":{""type"":""boolean"",""default"":true,""description"":""When true (default), refuses any mutating, destructive, or uncontracted tool call (fail-closed). Set false explicitly to permit writes.""},
""max_tool_calls"":{""type"":""integer"",""minimum"":1,""maximum"":100,""default"":25,""description"":""Maximum SCIO tool calls allowed in this script run.""},
""max_steps"":{""type"":""integer"",""minimum"":100,""maximum"":200000,""default"":50000,""description"":""Maximum AST evaluation steps.""},
""timeout_ms"":{""type"":""integer"",""minimum"":100,""maximum"":120000,""default"":30000,""description"":""Wall-clock timeout in milliseconds.""},
""max_output_bytes"":{""type"":""integer"",""minimum"":512,""maximum"":131072,""default"":32768,""description"":""Maximum UTF-8 byte size for `result`.""}
},""required"":[""code""],""additionalProperties"":false}")
        };

        public static async Task<ToolResponse> ExecuteAsync(
            ServerToolRegistry registry,
            ToolRequest request,
            CancellationToken ct)
        {
            try
            {
                await registry.HydrateRemoteCatalogAsync(ct).ConfigureAwait(false);
                JObject result = ExecuteCore(registry, request.Arguments ?? new JObject(), ct);
                return ToolResponse.Success(
                    request.RequestId,
                    result,
                    $"Code Mode completed ({result["stats"]?["tool_calls"]} tool call(s)).",
                    VerificationStatus.SelfReportedVerified,
                    hostVerified: false);
            }
            catch (ToolRefusalException tre)
            {
                return ToolResponse.Refusal(request.RequestId, "code_mode_refused", tre.Message, details: tre.Details);
            }
            catch (Exception ex)
            {
                return ToolResponse.Failure(request.RequestId, "code_mode_error", ex.Message);
            }
        }

        private static JObject ExecuteCore(ServerToolRegistry registry, JObject args, CancellationToken ct)
        {
            string code = args.Value<string>("code");
            if (string.IsNullOrWhiteSpace(code))
                throw new ToolRefusalException("'code' must be a non-empty string.");

            JObject inputs = args["inputs"] as JObject;
            bool readOnly = args.Value<bool?>("read_only") ?? true;
            int maxToolCalls = Clamp(args.Value<int?>("max_tool_calls") ?? DefaultMaxToolCalls, 1, HardMaxToolCalls);
            int maxSteps = Clamp(args.Value<int?>("max_steps") ?? DefaultMaxSteps, 100, HardMaxSteps);
            int timeoutMs = Clamp(args.Value<int?>("timeout_ms") ?? DefaultTimeoutMs, 100, HardTimeoutMs);
            int maxOutputBytes = Clamp(args.Value<int?>("max_output_bytes") ?? DefaultMaxOutputBytes, 512, HardMaxOutputBytes);

            var trace = new JArray();
            int callCount = 0;
            var sw = Stopwatch.StartNew();

            object InvokeScioTool(string rawName, JObject callArgs)
            {
                if (string.IsNullOrWhiteSpace(rawName))
                    throw new CodeModeException("tool_error", "Tool name must be a non-empty string.");

                string toolName = ResolveToolName(registry, rawName);
                if (toolName == ToolName)
                    throw new CodeModeException("tool_error", "Recursive call to '" + ToolName + "' is not allowed.");
                if (toolName == "scio_execute_python")
                    throw new CodeModeException("tool_error", "'scio_execute_python' is blocked inside " + ToolName + ".");
                if (toolName == "scio_call_tool")
                    throw new CodeModeException("tool_error", "'scio_call_tool' is blocked inside " + ToolName + "; call the target tool directly.");

                if (callCount >= maxToolCalls)
                    throw new CodeModeException("tool_call_limit", $"Exceeded max_tool_calls ({maxToolCalls}).");

                if (toolName == "scio_list_tools")
                {
                    callCount++;
                    string q = callArgs?.Value<string>("query") ?? string.Empty;
                    string pack = callArgs?.Value<string>("pack");
                    var scoped = registry.ListVisibleTools()
                        .Where(t => string.IsNullOrWhiteSpace(pack) || string.Equals(t.Pack, pack, StringComparison.OrdinalIgnoreCase));
                    var filtered = string.IsNullOrWhiteSpace(q)
                        ? scoped
                        : (BimSearchIndex.Tokenize(q).Length > 0
                            ? BimSearchIndex.SearchTools(scoped, q, null, limit: 50)
                            : scoped.Where(t =>
                                t.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                t.Description.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0));
                    var matches = filtered
                        .Select(t => new JObject { ["name"] = t.Name, ["pack"] = t.Pack, ["read_only"] = t.ReadOnly, ["description"] = t.Description });
                    var listObj = new JObject { ["tools"] = new JArray(matches) };
                    trace.Add(new JObject { ["step"] = callCount, ["tool"] = toolName, ["ok"] = true });
                    return CodeModeEngine.FromJToken(listObj);
                }

                if (toolName == "scio_describe_tool")
                {
                    callCount++;
                    string targetName = callArgs?.Value<string>("tool") ?? callArgs?.Value<string>("name") ?? string.Empty;
                    var desc = registry.FindDescriptor(targetName);
                    if (desc == null)
                        throw new CodeModeException("tool_error", $"Unknown tool '{targetName}'.");
                    trace.Add(new JObject { ["step"] = callCount, ["tool"] = toolName, ["ok"] = true });
                    return CodeModeEngine.FromJToken(JObject.FromObject(desc));
                }

                var descriptor = registry.FindDescriptor(toolName);
                if (readOnly && (descriptor == null || !descriptor.ReadOnly))
                {
                    string reason = descriptor == null ? "missing_contract" : "read_only";
                    trace.Add(new JObject { ["step"] = callCount + 1, ["tool"] = toolName, ["ok"] = false, ["blocked"] = reason });
                    throw new CodeModeException("tool_error",
                        descriptor == null
                            ? $"Tool '{toolName}' has no registered contract descriptor and is blocked when read_only=true."
                            : $"Tool '{toolName}' is blocked because read_only=true.");
                }

                int remainingMs = timeoutMs - (int)sw.ElapsedMilliseconds;
                if (remainingMs <= 0)
                    throw new CodeModeException("timeout", $"Code Mode exceeded timeout_ms ({timeoutMs} ms).");

                callCount++;
                var callSw = Stopwatch.StartNew();
                var innerReq = new ToolRequest
                {
                    Tool = toolName,
                    Arguments = callArgs ?? new JObject()
                };

                ToolResponse reply;
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    linkedCts.CancelAfter(remainingMs);
                    reply = registry.DispatchAsync(innerReq, linkedCts.Token).GetAwaiter().GetResult();
                }
                callSw.Stop();

                trace.Add(new JObject
                {
                    ["step"] = callCount,
                    ["tool"] = toolName,
                    ["ms"] = callSw.ElapsedMilliseconds,
                    ["ok"] = reply.Ok,
                    ["verification"] = reply.Verification.ToString(),
                    ["host_verified"] = reply.HostVerified
                });

                if (!reply.Ok)
                {
                    string errMsg = reply.Error?.Message ?? reply.Summary ?? "Unknown tool error.";
                    throw new CodeModeException("tool_error", $"Tool '{toolName}' failed: {errMsg}");
                }

                return CodeModeEngine.FromJToken(reply.Data ?? JValue.CreateNull());
            }

            CodeModeEngine.HostCallable callFn = fnArgs =>
            {
                if (fnArgs.Count == 0)
                    throw new CodeModeException("tool_error", "scio.call(name, args) requires a tool name.");
                string name = CodeModeEngine.FormatValue(fnArgs[0]);
                JObject callArgs = fnArgs.Count >= 2 ? CodeModeEngine.ToJToken(fnArgs[1]) as JObject : new JObject();
                return InvokeScioTool(name, callArgs);
            };

            CodeModeEngine.HostCallable searchFn = fnArgs =>
            {
                var qArgs = new JObject();
                if (fnArgs.Count > 0 && fnArgs[0] is string qs) qArgs["query"] = qs;
                else if (fnArgs.Count > 0 && CodeModeEngine.ToJToken(fnArgs[0]) is JObject jo) qArgs = jo;
                return InvokeScioTool("scio_list_tools", qArgs);
            };

            CodeModeEngine.HostCallable describeFn = fnArgs =>
            {
                if (fnArgs.Count == 0)
                    throw new CodeModeException("tool_error", "scio.describe(name) requires a tool name.");
                var dArgs = new JObject();
                if (fnArgs[0] is string s) dArgs["tool"] = s;
                else if (CodeModeEngine.ToJToken(fnArgs[0]) is JObject jo) dArgs = jo;
                return InvokeScioTool("scio_describe_tool", dArgs);
            };

            var scioObj = new CodeModeEngine.DynamicHostObject(prop =>
                (CodeModeEngine.HostCallable)(fnArgs =>
                {
                    JObject callArgs = fnArgs.Count >= 1 ? CodeModeEngine.ToJToken(fnArgs[0]) as JObject : new JObject();
                    return InvokeScioTool(prop, callArgs);
                }));
            scioObj["call"] = callFn;
            scioObj["search"] = searchFn;
            scioObj["describe"] = describeFn;

            var globals = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["scio"] = scioObj,
                ["call"] = callFn,
                ["search"] = searchFn,
                ["describe"] = describeFn,
                ["inputs"] = inputs != null ? CodeModeEngine.FromJToken(inputs) : new Dictionary<string, object>(StringComparer.Ordinal)
            };

            try
            {
                CodeModeRunOutput run = CodeModeEngine.Execute(code, globals, new CodeModeLimits { MaxSteps = maxSteps, TimeoutMs = timeoutMs }, ct);
                sw.Stop();

                JToken finalResult = run.Result ?? JValue.CreateNull();
                string serialized = finalResult.ToString(Formatting.None);
                int outputBytes = Encoding.UTF8.GetByteCount(serialized);
                bool truncated = false;
                if (outputBytes > maxOutputBytes)
                {
                    truncated = true;
                    byte[] bytes = Encoding.UTF8.GetBytes(serialized);
                    int len = Math.Max(256, maxOutputBytes - 256);
                    while (len > 0 && (bytes[len] & 0xC0) == 0x80) len--;
                    finalResult = new JObject
                    {
                        ["truncated"] = true,
                        ["original_bytes"] = outputBytes,
                        ["preview"] = Encoding.UTF8.GetString(bytes, 0, len) + "..."
                    };
                }

                var payload = new JObject
                {
                    ["ok"] = true,
                    ["result"] = finalResult,
                    ["trace"] = trace,
                    ["stats"] = new JObject
                    {
                        ["tool_calls"] = callCount,
                        ["ast_steps"] = run.StepsExecuted,
                        ["elapsed_ms"] = sw.ElapsedMilliseconds,
                        ["read_only"] = readOnly,
                        ["output_bytes"] = outputBytes,
                        ["output_truncated"] = truncated
                    }
                };
                if (run.Logs.Count > 0) payload["logs"] = new JArray(run.Logs);
                return payload;
            }
            catch (CodeModeException cme)
            {
                sw.Stop();
                throw new ToolRefusalException($"[{cme.Code}] {cme.Message}", new JObject
                {
                    ["error_code"] = cme.Code,
                    ["trace"] = trace
                });
            }
        }

        private static string ResolveToolName(ServerToolRegistry registry, string raw)
        {
            string trimmed = raw.Trim();
            if (trimmed == "scio_list_tools" || trimmed == "scio_describe_tool" || trimmed == ToolName)
                return trimmed;
            if (registry.FindDescriptor(trimmed) != null) return trimmed;
            if (!trimmed.StartsWith("scio_", StringComparison.Ordinal))
            {
                string prefixed = "scio_" + trimmed;
                if (registry.FindDescriptor(prefixed) != null) return prefixed;
            }
            return trimmed;
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
    }
}
