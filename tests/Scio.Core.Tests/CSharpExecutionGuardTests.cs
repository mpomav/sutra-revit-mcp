using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Server;
using Xunit;

namespace Scio.Core.Tests
{
    public sealed class CSharpExecutionGuardTests
    {
        [Fact]
        public void WrapIfNeeded_WrapsBareStatementsIntoScioDynamicScript()
        {
            string wrapped = CSharpScriptHelper.WrapIfNeeded("return doc.Title;");
            Assert.Contains("public static class ScioDynamicScript", wrapped);
            Assert.Contains("public static object? Run(", wrapped);
            Assert.Contains("return doc.Title;", wrapped);
        }

        [Fact]
        public void WrapIfNeeded_PreservesExplicitScioDynamicScriptClass()
        {
            string raw = "public static class ScioDynamicScript { public static object? Run(object a, object b, object c, object d) => 42; }";
            Assert.Equal(raw, CSharpScriptHelper.WrapIfNeeded(raw));
        }

        [Fact]
        public async Task CodeMode_BlocksScioExecuteCSharp()
        {
            var registry = new ServerToolRegistry
            {
                MockMode = true,
                PermissionProfile = PermissionProfile.UnsafeCode
            };
            _ = new McpStdioHost(registry, "codemode");

            var resp = await registry.DispatchAsync(new ToolRequest
            {
                Tool = "scio_code_mode",
                Arguments = new JObject
                {
                    ["read_only"] = false,
                    ["code"] = "scio.call('scio_execute_csharp', { code: 'return 1;' });"
                }
            }, CancellationToken.None);

            Assert.False(resp.Ok);
            Assert.Contains("scio_execute_csharp", resp.Error?.Message ?? string.Empty);
        }
    }
}
