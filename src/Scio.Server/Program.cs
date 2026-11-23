using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Scio.Abstractions;

namespace Scio.Server
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            var registry = new ServerToolRegistry();

            string? packsEnv = Environment.GetEnvironmentVariable("SCIO_TOOL_PACKS")
                            ?? Environment.GetEnvironmentVariable("SCIO_TOOLSETS");
            if (!string.IsNullOrWhiteSpace(packsEnv))
            {
                registry.ActivePacks = ToolPackResolver.ResolveActivePacks(
                    packsEnv.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries));
            }

            if (int.TryParse(Environment.GetEnvironmentVariable("SCIO_REVIT_YEAR"), out int year))
            {
                registry.PreferredRevitYear = year;
            }

            var host = new McpStdioHost(registry);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            await host.RunAsync(Console.In, Console.Out, cts.Token).ConfigureAwait(false);
            return 0;
        }
    }
}
