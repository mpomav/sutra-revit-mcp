using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Server;
using Xunit;

namespace Scio.Core.Tests
{
    public sealed class CodeModeTests
    {
        [Fact]
        public async Task CodeMode_OrchestratesToolsAndEnforcesSafetyGates()
        {
            var registry = new ServerToolRegistry();
            registry.RegisterServerTool(
                new ToolDescriptor { Name = "scio_mock_items", Pack = "read", ReadOnly = true, Description = "Returns mock BIM items" },
                (req, ct) => Task.FromResult(ToolResponse.Success(req.RequestId, new JArray(
                    new JObject { ["id"] = 1, ["vol"] = 10.5 },
                    new JObject { ["id"] = 2, ["vol"] = 24.5 }
                ))));

            registry.RegisterServerTool(
                new ToolDescriptor { Name = "scio_mock_write", Pack = "model", ReadOnly = false, Description = "Mutates model" },
                (req, ct) => Task.FromResult(ToolResponse.Success(req.RequestId, new JObject { ["written"] = true })));

            var host = new McpStdioHost(registry, schemaMode: "codemode");

            // 1. In codemode, tools/list stays compact (4 tools)
            var listResp = await host.HandleJsonRpcLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}",
                CancellationToken.None);
            Assert.Equal(4, ((JArray)listResp!["result"]!["tools"]!).Count);

            // 2. Orchestrate loop + aggregation in one turn
            var runResp = await host.HandleJsonRpcLineAsync(
                new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = 2,
                    ["method"] = "tools/call",
                    ["params"] = new JObject
                    {
                        ["name"] = "scio_code_mode",
                        ["arguments"] = new JObject
                        {
                            ["read_only"] = true,
                            ["code"] = "const items = scio.mock_items({}); let total = 0; for (const it of items) { total += it.vol; } return { count: items.length, total };"
                        }
                    }
                }.ToString(),
                CancellationToken.None);

            Assert.False(runResp!["result"]!["isError"]!.Value<bool>());
            var res = runResp["result"]!["structuredContent"]!["data"]!["result"]!;
            Assert.Equal(2, res["count"]!.Value<int>());
            Assert.Equal(35.0, res["total"]!.Value<double>());

            // 3. read_only=true blocks mutating tool
            var blockedResp = await host.HandleJsonRpcLineAsync(
                new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = 3,
                    ["method"] = "tools/call",
                    ["params"] = new JObject
                    {
                        ["name"] = "scio_code_mode",
                        ["arguments"] = new JObject
                        {
                            ["read_only"] = true,
                            ["code"] = "return scio.mock_write({});"
                        }
                    }
                }.ToString(),
                CancellationToken.None);

            Assert.True(blockedResp!["result"]!["isError"]!.Value<bool>());

            // 4. Trace preserves verification & host_verified verdict
            var trace = runResp["result"]!["structuredContent"]!["data"]!["trace"] as JArray;
            Assert.NotNull(trace);
            Assert.Equal("read_only", trace![0]!["verification"]!.Value<string>());
            Assert.False(trace[0]!["host_verified"]!.Value<bool>());
        }

        [Fact]
        public async Task CodeMode_SecurityHardening_FailClosed_BlocksCallTool_MemoryCap_AndFacadeGate()
        {
            var registry = new ServerToolRegistry();
            registry.RegisterServerTool(
                new ToolDescriptor { Name = "scio_mock_write", Pack = "model", ReadOnly = false, Description = "Mutates model" },
                (req, ct) => Task.FromResult(ToolResponse.Success(req.RequestId, new JObject { ["written"] = true })));

            var codeHost = new McpStdioHost(registry, schemaMode: "codemode");

            // A. read_only defaults to true -> mutating tool is blocked even when read_only is omitted
            var defaultRo = await codeHost.HandleJsonRpcLineAsync(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 10,
                ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["name"] = "scio_code_mode",
                    ["arguments"] = new JObject { ["code"] = "return scio.mock_write({});" }
                }
            }.ToString(), CancellationToken.None);
            Assert.True(defaultRo!["result"]!["isError"]!.Value<bool>());

            // B. read_only=true is fail-closed for tools with no registered descriptor/contract
            var missingContract = await codeHost.HandleJsonRpcLineAsync(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 11,
                ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["name"] = "scio_code_mode",
                    ["arguments"] = new JObject
                    {
                        ["read_only"] = true,
                        ["code"] = "return call('scio_unregistered_mystery_tool', {});"
                    }
                }
            }.ToString(), CancellationToken.None);
            Assert.True(missingContract!["result"]!["isError"]!.Value<bool>());

            // C. scio_call_tool is always blocked inside Code Mode (even with read_only=false)
            var callToolBypass = await codeHost.HandleJsonRpcLineAsync(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 12,
                ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["name"] = "scio_code_mode",
                    ["arguments"] = new JObject
                    {
                        ["read_only"] = false,
                        ["code"] = "return call('scio_call_tool', { tool: 'scio_execute_python', arguments: {} });"
                    }
                }
            }.ToString(), CancellationToken.None);
            Assert.True(callToolBypass!["result"]!["isError"]!.Value<bool>());

            // D. String doubling (s = s + s) hits memory_limit cleanly instead of exhausting RAM
            var memBomb = await codeHost.HandleJsonRpcLineAsync(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 13,
                ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["name"] = "scio_code_mode",
                    ["arguments"] = new JObject
                    {
                        ["code"] = "let s = 'abcdefgh'; for (let i = 0; i < 30; i++) { s = s + s; } return s.length;"
                    }
                }
            }.ToString(), CancellationToken.None);
            Assert.True(memBomb!["result"]!["isError"]!.Value<bool>());

            // E. In facade mode, scio_code_mode is refused
            var facadeHost = new McpStdioHost(new ServerToolRegistry(), schemaMode: "facade");
            var facadeResp = await facadeHost.HandleJsonRpcLineAsync(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 14,
                ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["name"] = "scio_code_mode",
                    ["arguments"] = new JObject { ["code"] = "return 1;" }
                }
            }.ToString(), CancellationToken.None);
            Assert.True(facadeResp!["result"]!["isError"]!.Value<bool>());
        }
    }
}
