using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Ipc;
using Scio.Server;
using Xunit;

namespace Scio.Core.Tests
{
    public sealed class CoreArchitectureTests
    {
        private sealed class SampleInputDto
        {
            [ScioParam("Category name to filter", Required = true)]
            public string Category { get; set; } = string.Empty;

            [ScioParam("Maximum items to return", DefaultValue = 50)]
            public int? Limit { get; set; }

            [ScioParam("Target mode", AllowedValues = new[] { "summary", "standard", "full" })]
            public string? Verbosity { get; set; }

            [ScioParam("Element IDs")]
            public List<long>? ElementIds { get; set; }
        }

        [ScioTool("scio_sample_query", "read", "Sample read tool for schema tests", ReadOnly = true)]
        private sealed class SampleHandler { }

        [Fact]
        public void JsonSchemaBuilder_GeneratesExpectedSchemaAndDeterministicHash()
        {
            var desc1 = JsonSchemaBuilder.DescribeTool(typeof(SampleHandler), typeof(SampleInputDto));
            var desc2 = JsonSchemaBuilder.DescribeTool(typeof(SampleHandler), typeof(SampleInputDto));

            Assert.Equal("scio_sample_query", desc1.Name);
            Assert.Equal("read", desc1.Pack);
            Assert.True(desc1.ReadOnly);

            var props = desc1.InputSchema["properties"] as JObject;
            Assert.NotNull(props);
            Assert.Equal("string", props!["category"]!["type"]!.ToString());
            Assert.Equal("integer", props["limit"]!["type"]!.ToString());
            Assert.Equal(50, props["limit"]!["default"]!.Value<int>());
            Assert.Equal("array", props["element_ids"]!["type"]!.ToString());
            Assert.Equal("integer", props["element_ids"]!["items"]!["type"]!.ToString());

            var required = desc1.InputSchema["required"] as JArray;
            Assert.NotNull(required);
            Assert.Single(required!);
            Assert.Equal("category", required![0].ToString());

            string hash1 = JsonSchemaBuilder.ComputeContractHash(new[] { desc1 });
            string hash2 = JsonSchemaBuilder.ComputeContractHash(new[] { desc2 });
            Assert.Equal(hash1, hash2);
            Assert.Equal(24, hash1.Length);
        }

        [Fact]
        public void ToolPackResolver_ResolvesTransitiveDependenciesAndPermissions()
        {
            var packs = ToolPackResolver.ResolveActivePacks(new[] { "planimetry" });
            Assert.Contains("core", packs);
            Assert.Contains("planimetry", packs);
            Assert.Contains("documentation", packs);
            Assert.Contains("read", packs);
            Assert.DoesNotContain("mep", packs);

            var writeTool = new ToolDescriptor { Name = "scio_fix_planimetry", Pack = "planimetry", ReadOnly = false };
            Assert.False(ToolPackResolver.IsToolVisible(writeTool, packs, PermissionProfile.ReadOnly));
            Assert.True(ToolPackResolver.IsToolVisible(writeTool, packs, PermissionProfile.SafeWrite));
        }

        [Fact]
        public async Task ScioPipeTransport_RoundTripsRequestAndResponse()
        {
            string pipeName = "scio-v4-test-" + Guid.NewGuid().ToString("N");
            using var server = new ScioPipeServer(pipeName, (req, ct) =>
            {
                var data = new JObject { ["echo"] = req.Arguments["msg"] };
                return Task.FromResult(ToolResponse.Success(req.RequestId, data, "Echoed ok"));
            });
            server.Start();

            var client = new ScioPipeClient(pipeName);
            var response = await client.SendAsync(new ToolRequest
            {
                Tool = "scio_echo",
                Arguments = new JObject { ["msg"] = "hola-scio-v4" }
            });

            Assert.True(response.Ok);
            Assert.Equal("hola-scio-v4", response.Data!["echo"]!.ToString());
        }

        [Fact]
        public async Task McpStdioHost_SupportsInitializeToolsListAndFacadeMode()
        {
            var registry = new ServerToolRegistry();
            registry.RegisterRemoteTool(JsonSchemaBuilder.DescribeTool(typeof(SampleHandler), typeof(SampleInputDto)));

            var host = new McpStdioHost(registry, schemaMode: "facade");

            var initResp = await host.HandleJsonRpcLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}",
                CancellationToken.None);
            Assert.NotNull(initResp);
            Assert.Equal("scio-revit-mcp", initResp!["result"]!["serverInfo"]!["name"]!.ToString());

            var listResp = await host.HandleJsonRpcLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}",
                CancellationToken.None);
            var tools = listResp!["result"]!["tools"] as JArray;
            Assert.NotNull(tools);
            Assert.Equal(4, tools!.Count); // scio_target + 3 facade tools

            var descResp = await host.HandleJsonRpcLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"scio_describe_tool\",\"arguments\":{\"tool\":\"scio_sample_query\"}}}",
                CancellationToken.None);
            Assert.False(descResp!["result"]!["isError"]!.Value<bool>());
            Assert.Equal("scio_sample_query", descResp["result"]!["structuredContent"]!["data"]!["name"]!.ToString());
        }
    }
}
