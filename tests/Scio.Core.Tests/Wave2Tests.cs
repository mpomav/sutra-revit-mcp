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
    public sealed class Wave2Tests
    {
        [ScioTool("scio_sample_annotated", "core", "Sample annotated command", ReadOnly = true)]
        private sealed class AnnotatedCommand
        {
            [ScioParam("category", "Target category", Required = true)]
            public string CategoryName { get; set; } = string.Empty;

            [ScioParam("mode", "Query mode", EnumValues = new[] { "fast", "full" })]
            public string Mode { get; set; } = "fast";

            public string UnannotatedHelper { get; set; } = string.Empty;
        }

        [Fact]
        public void DescribeTool_ReadsAnnotatedPropertiesDirectlyFromCommandClass()
        {
            var desc = JsonSchemaBuilder.DescribeTool(typeof(AnnotatedCommand));
            Assert.Equal("scio_sample_annotated", desc.Name);
            var props = desc.InputSchema["properties"] as JObject;
            Assert.NotNull(props);
            Assert.NotNull(props!["category"]);
            Assert.NotNull(props["mode"]);
            Assert.Null(props["unannotated_helper"]);
            Assert.Equal(new[] { "fast", "full" }, props["mode"]!["enum"]!.ToObject<string[]>());
            Assert.Contains("category", desc.InputSchema["required"]!.ToObject<string[]>()!);
        }

        [Fact]
        public async Task ServerRegistry_HydratesRemoteCatalogOverNamedPipe()
        {
            string pipeName = "scio_test_cat_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            var remoteTools = new List<ToolDescriptor>
            {
                new ToolDescriptor
                {
                    Name = "scio_list_elements",
                    Pack = "core",
                    Description = "List elements",
                    ReadOnly = true,
                    InputSchema = new JObject { ["type"] = "object", ["properties"] = new JObject() }
                },
                new ToolDescriptor
                {
                    Name = "scio_write_params_verified",
                    Pack = "core",
                    Description = "Write params",
                    ReadOnly = false,
                    InputSchema = new JObject { ["type"] = "object", ["properties"] = new JObject() }
                }
            };

            using var server = new ScioPipeServer(pipeName, (req, ct) =>
            {
                if (req.Tool == "_scio_catalog")
                {
                    return Task.FromResult(ToolResponse.Success(req.RequestId, new JObject
                    {
                        ["revit_year"] = 2026,
                        ["contract_hash"] = JsonSchemaBuilder.ComputeContractHash(remoteTools),
                        ["tools"] = JArray.FromObject(remoteTools)
                    }));
                }
                return Task.FromResult(ToolResponse.Refusal(req.RequestId, "unknown", "unknown"));
            });
            server.Start();

            var registry = new ServerToolRegistry { PinnedPipeName = pipeName };
            int added = await registry.HydrateRemoteCatalogAsync(CancellationToken.None);

            Assert.Equal(2, added);
            Assert.NotNull(registry.FindDescriptor("scio_list_elements"));
            Assert.NotNull(registry.FindDescriptor("scio_write_params_verified"));
        }

        [Fact]
        public async Task SuggestWorkflowTool_ReturnsMatchingToolsViaStdioHost()
        {
            var registry = new ServerToolRegistry();
            var host = new McpStdioHost(registry, "full");
            registry.RegisterRemoteTool(new ToolDescriptor
            {
                Name = "scio_model_scan",
                Pack = "core",
                Description = "Scan model categories and warnings",
                ReadOnly = true
            });

            string rpc = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 42,
                ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["name"] = "scio_suggest_workflow",
                    ["arguments"] = new JObject { ["goal"] = "scan model warnings" }
                }
            }.ToString();

            var resp = await host.HandleJsonRpcLineAsync(rpc, CancellationToken.None);
            Assert.NotNull(resp);
            Assert.False(resp!["result"]!["isError"]!.Value<bool>());
            var data = resp["result"]!["structuredContent"]!["data"] as JObject;
            Assert.NotNull(data);
            Assert.True(data!.Value<int>("match_count") >= 1);
        }
    }
}
