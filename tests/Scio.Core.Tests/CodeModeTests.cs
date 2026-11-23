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
        }
    }
}
