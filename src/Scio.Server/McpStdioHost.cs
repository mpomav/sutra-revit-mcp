using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Ipc;

namespace Scio.Server
{
    /// <summary>
    /// Clean MCP stdio JSON-RPC 2.0 host supporting full, facade, and codemode discovery.
    /// </summary>
    public sealed class McpStdioHost
    {
        private const string ProtocolVersion = "2025-03-26";
        private readonly ServerToolRegistry _registry;
        private readonly string _schemaMode;

        public McpStdioHost(ServerToolRegistry registry, string? schemaMode = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _schemaMode = ResolveSchemaMode(schemaMode);

            RegisterBuiltInCoreTools();
        }

        private static string ResolveSchemaMode(string? explicitMode)
        {
            string? raw = explicitMode ?? Environment.GetEnvironmentVariable("SCIO_SCHEMA_MODE");
            if (string.IsNullOrWhiteSpace(raw))
            {
                raw = ReadSettingString("schema_mode");
            }
            string norm = (raw ?? "codemode").Trim().ToLowerInvariant();
            if (norm == "code_mode" || norm == "code-mode") norm = "codemode";
            return (norm == "full" || norm == "facade" || norm == "codemode") ? norm : "codemode";
        }

        private static string? ReadSettingString(string key)
        {
            try
            {
                string path = Path.Combine(ScioRuntimePaths.DataRoot, "settings.json");
                if (!File.Exists(path)) return null;
                var obj = JObject.Parse(File.ReadAllText(path));
                return obj[key]?.ToString();
            }
            catch { return null; }
        }

        private void RegisterBuiltInCoreTools()
        {
            _registry.RegisterServerTool(
                new ToolDescriptor
                {
                    Name = "scio_target",
                    Pack = "core",
                    Description = "List active Revit v4 sessions or pin a specific Revit year/PID for subsequent tool calls.",
                    ReadOnly = true,
                    InputSchema = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["revit_year"] = new JObject { ["type"] = "integer", ["description"] = "Optional Revit year (2023-2027) to target." },
                            ["pid"] = new JObject { ["type"] = "integer", ["description"] = "Optional Revit process ID to pin." },
                            ["clear"] = new JObject { ["type"] = "boolean", ["description"] = "Clear any pinned session and return to auto-discovery." }
                        },
                        ["additionalProperties"] = false
                    }
                },
                async (req, ct) =>
                {
                    var sessions = SessionRegistry.DiscoverLiveSessions();
                    bool targetChanged = false;
                    if (req.Arguments.Value<bool?>("clear") == true)
                    {
                        _registry.PinnedPipeName = null;
                        _registry.PreferredRevitYear = null;
                        targetChanged = true;
                    }
                    else
                    {
                        int? year = req.Arguments.Value<int?>("revit_year");
                        int? pid = req.Arguments.Value<int?>("pid");
                        if (pid.HasValue)
                        {
                            var byPid = sessions.FirstOrDefault(s => s.Pid == pid.Value);
                            if (byPid == null)
                                return ToolResponse.Refusal(req.RequestId, "session_not_found", $"No live Revit v4 session with PID {pid.Value}.");
                            _registry.PinnedPipeName = byPid.PipeName;
                            targetChanged = true;
                        }
                        else if (year.HasValue)
                        {
                            _registry.PreferredRevitYear = year.Value;
                            _registry.PinnedPipeName = null;
                            targetChanged = true;
                        }
                    }

                    await _registry.HydrateRemoteCatalogAsync(ct, force: targetChanged).ConfigureAwait(false);

                    var data = new JObject
                    {
                        ["active_pipe"] = _registry.ResolveTargetPipe(),
                        ["preferred_year"] = _registry.PreferredRevitYear,
                        ["sessions"] = JArray.FromObject(sessions)
                    };
                    return ToolResponse.Success(req.RequestId, data, $"Found {sessions.Count} live Revit v4 session(s).");
                });

            _registry.RegisterServerTool(
                CodeModeTool.CreateDescriptor(),
                (req, ct) => CodeModeTool.ExecuteAsync(_registry, req, ct));

            LocalToolsRegistrar.RegisterAll(_registry);

            _registry.RegisterRemoteTool(new ToolDescriptor
            {
                Name = "scio_health",
                Pack = "core",
                Description = "Check SCIO Revit MCP v4 bridge health, active document, loaded modules, and contract hash.",
                ReadOnly = true,
                InputSchema = new JObject { ["type"] = "object", ["properties"] = new JObject(), ["additionalProperties"] = false }
            });

            if (_schemaMode == "facade" || _schemaMode == "codemode")
            {
                RegisterFacadeTools();
            }
        }

        private void RegisterFacadeTools()
        {
            _registry.RegisterServerTool(
                new ToolDescriptor
                {
                    Name = "scio_list_tools",
                    Pack = "core",
                    Description = "Discover available SCIO Revit MCP v4 tools filtered by pack or keyword.",
                    ReadOnly = true,
                    InputSchema = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["pack"] = new JObject { ["type"] = "string" },
                            ["query"] = new JObject { ["type"] = "string" }
                        },
                        ["additionalProperties"] = false
                    }
                },
                async (req, ct) =>
                {
                    await _registry.HydrateRemoteCatalogAsync(ct).ConfigureAwait(false);

                    string? pack = req.Arguments.Value<string>("pack");
                    string? query = req.Arguments.Value<string>("query");

                    var scoped = _registry.ListVisibleTools()
                        .Where(t => string.IsNullOrWhiteSpace(pack) || string.Equals(t.Pack, pack, StringComparison.OrdinalIgnoreCase));

                    var filtered = string.IsNullOrWhiteSpace(query)
                        ? scoped
                        : (BimSearchIndex.Tokenize(query).Length > 0
                            ? BimSearchIndex.SearchTools(scoped, query, null, limit: 50)
                            : scoped.Where(t =>
                                t.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                t.Description.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0));

                    var matches = filtered.Select(t => new JObject
                        {
                            ["name"] = t.Name,
                            ["pack"] = t.Pack,
                            ["read_only"] = t.ReadOnly,
                            ["description"] = t.Description
                        });

                    return ToolResponse.Success(req.RequestId, new JArray(matches), "Listed matching tools.");
                });

            _registry.RegisterServerTool(
                new ToolDescriptor
                {
                    Name = "scio_describe_tool",
                    Pack = "core",
                    Description = "Fetch the full JSON Schema for a specific SCIO tool by name.",
                    ReadOnly = true,
                    InputSchema = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["tool"] = new JObject { ["type"] = "string" }
                        },
                        ["required"] = new JArray("tool"),
                        ["additionalProperties"] = false
                    }
                },
                async (req, ct) =>
                {
                    string name = req.Arguments.Value<string>("tool") ?? string.Empty;
                    var desc = _registry.FindDescriptor(name);
                    if (desc == null)
                    {
                        await _registry.HydrateRemoteCatalogAsync(ct).ConfigureAwait(false);
                        desc = _registry.FindDescriptor(name);
                    }
                    if (desc == null)
                        return ToolResponse.Refusal(req.RequestId, "unknown_tool", $"Unknown tool '{name}'.");
                    return ToolResponse.Success(req.RequestId, JObject.FromObject(desc), $"Schema for {name}.");
                });

            _registry.RegisterServerTool(
                new ToolDescriptor
                {
                    Name = "scio_call_tool",
                    Pack = "core",
                    Description = "Dispatch any permitted SCIO tool through the unified facade.",
                    ReadOnly = false,
                    InputSchema = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["tool"] = new JObject { ["type"] = "string" },
                            ["arguments"] = new JObject { ["type"] = "object" }
                        },
                        ["required"] = new JArray("tool"),
                        ["additionalProperties"] = false
                    }
                },
                async (req, ct) =>
                {
                    var inner = new ToolRequest
                    {
                        RequestId = req.RequestId,
                        Tool = req.Arguments.Value<string>("tool") ?? string.Empty,
                        Arguments = req.Arguments["arguments"] as JObject ?? new JObject()
                    };
                    return await _registry.DispatchAsync(inner, ct).ConfigureAwait(false);
                });
        }

        public async Task RunAsync(TextReader input, TextWriter output, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                string? line = await input.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                JObject? response = await HandleJsonRpcLineAsync(line, ct).ConfigureAwait(false);
                if (response != null)
                {
                    await output.WriteLineAsync(response.ToString(Formatting.None)).ConfigureAwait(false);
                    await output.FlushAsync().ConfigureAwait(false);
                }
            }
        }

        public async Task<JObject?> HandleJsonRpcLineAsync(string line, CancellationToken ct)
        {
            JObject msg;
            try
            {
                msg = JObject.Parse(line);
            }
            catch (Exception ex)
            {
                return MakeJsonRpcError(null, -32700, $"JSON parse error: {ex.Message}");
            }

            JToken? id = msg["id"];
            string method = msg.Value<string>("method") ?? string.Empty;
            var @params = msg["params"] as JObject ?? new JObject();

            // Notifications (no id) require no response
            if (id == null) return null;

            switch (method)
            {
                case "initialize":
                    return MakeJsonRpcResult(id, new JObject
                    {
                        ["protocolVersion"] = ProtocolVersion,
                        ["capabilities"] = new JObject
                        {
                            ["tools"] = new JObject { ["listChanged"] = true },
                            ["resources"] = new JObject { ["listChanged"] = false }
                        },
                        ["serverInfo"] = new JObject
                        {
                            ["name"] = "scio-revit-mcp",
                            ["version"] = "4.0.0-alpha.1"
                        }
                    });

                case "ping":
                    return MakeJsonRpcResult(id, new JObject());

                case "tools/list":
                    await _registry.HydrateRemoteCatalogAsync(ct).ConfigureAwait(false);
                    return MakeJsonRpcResult(id, BuildToolsList());

                case "tools/call":
                    return await HandleToolCallAsync(id, @params, ct).ConfigureAwait(false);

                case "resources/list":
                    await _registry.HydrateRemoteCatalogAsync(ct).ConfigureAwait(false);
                    return MakeJsonRpcResult(id, BuildResourcesList());

                case "resources/read":
                    return await HandleResourceReadAsync(id, @params, ct).ConfigureAwait(false);

                default:
                    return MakeJsonRpcError(id, -32601, $"Method not found: {method}");
            }
        }

        private JObject BuildToolsList()
        {
            var visible = _registry.ListVisibleTools();
            if (_schemaMode == "facade")
            {
                visible = visible
                    .Where(t => t.Name == "scio_list_tools" || t.Name == "scio_describe_tool" || t.Name == "scio_call_tool" || t.Name == "scio_target")
                    .ToList();
            }
            else if (_schemaMode == "codemode")
            {
                visible = visible
                    .Where(t => t.Name == CodeModeTool.ToolName || t.Name == "scio_list_tools" || t.Name == "scio_describe_tool" || t.Name == "scio_target")
                    .ToList();
            }

            var tools = new JArray(visible.Select(t => new JObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["inputSchema"] = t.InputSchema,
                ["annotations"] = new JObject
                {
                    ["readOnlyHint"] = t.ReadOnly,
                    ["destructiveHint"] = t.Destructive,
                    ["openWorldHint"] = t.OpenWorld
                }
            }));

            return new JObject { ["tools"] = tools };
        }

        private async Task<JObject> HandleToolCallAsync(JToken id, JObject @params, CancellationToken ct)
        {
            string toolName = @params.Value<string>("name") ?? string.Empty;
            var args = @params["arguments"] as JObject ?? new JObject();

            if (_schemaMode == "facade" && string.Equals(toolName, CodeModeTool.ToolName, StringComparison.Ordinal))
            {
                var refused = ToolResponse.Refusal(
                    Guid.NewGuid().ToString("N"),
                    "mode_restricted",
                    "scio_code_mode is disabled when SCIO_SCHEMA_MODE=facade. Use full or codemode.");
                var refPayload = JObject.FromObject(refused);
                return MakeJsonRpcResult(id, new JObject
                {
                    ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = refPayload.ToString(Formatting.Indented) }),
                    ["structuredContent"] = refPayload,
                    ["isError"] = true
                });
            }

            var req = new ToolRequest
            {
                Tool = toolName,
                Arguments = args,
                TargetDocument = args.Value<string>("target_document"),
                DryRun = args.Value<bool?>("dry_run"),
                Verbosity = args.Value<string>("verbosity") ?? "standard"
            };

            var reply = await _registry.DispatchAsync(req, ct).ConfigureAwait(false);
            var payload = JObject.FromObject(reply);

            return MakeJsonRpcResult(id, new JObject
            {
                ["content"] = new JArray(new JObject
                {
                    ["type"] = "text",
                    ["text"] = payload.ToString(Formatting.Indented)
                }),
                ["structuredContent"] = payload,
                ["isError"] = !reply.Ok
            });
        }

        private JObject BuildResourcesList()
        {
            var resources = new JArray(_registry.ListVisibleTools().Select(t => new JObject
            {
                ["uri"] = $"scio://contract/tools/{t.Name}",
                ["name"] = t.Name,
                ["mimeType"] = "application/json",
                ["description"] = $"Full contract schema for {t.Name}"
            }));
            return new JObject { ["resources"] = resources };
        }

        private async Task<JObject> HandleResourceReadAsync(JToken id, JObject @params, CancellationToken ct)
        {
            string uri = @params.Value<string>("uri") ?? string.Empty;
            const string prefix = "scio://contract/tools/";
            if (!uri.StartsWith(prefix, StringComparison.Ordinal))
                return MakeJsonRpcError(id, -32602, $"Unknown resource URI: {uri}");

            string toolName = uri.Substring(prefix.Length);
            var desc = _registry.FindDescriptor(toolName);
            if (desc == null)
            {
                await _registry.HydrateRemoteCatalogAsync(ct).ConfigureAwait(false);
                desc = _registry.FindDescriptor(toolName);
            }
            if (desc == null)
                return MakeJsonRpcError(id, -32602, $"Tool schema not found: {toolName}");

            return MakeJsonRpcResult(id, new JObject
            {
                ["contents"] = new JArray(new JObject
                {
                    ["uri"] = uri,
                    ["mimeType"] = "application/json",
                    ["text"] = JObject.FromObject(desc).ToString(Formatting.Indented)
                })
            });
        }

        private static JObject MakeJsonRpcResult(JToken id, JObject result) => new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result
        };

        private static JObject MakeJsonRpcError(JToken? id, int code, string message) => new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id ?? JValue.CreateNull(),
            ["error"] = new JObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };
    }
}
