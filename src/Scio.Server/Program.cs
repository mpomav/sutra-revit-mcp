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
            if (args != null && args.Length == 1 && TryPrintHiddenEpigraph(args[0]))
            {
                return 0;
            }

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

            bool mockFlag = (args != null && Array.Exists(args, a => string.Equals(a, "--mock", StringComparison.OrdinalIgnoreCase)))
                            || string.Equals(Environment.GetEnvironmentVariable("SCIO_MOCK"), "1", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(Environment.GetEnvironmentVariable("SCIO_MOCK"), "true", StringComparison.OrdinalIgnoreCase);
            registry.MockMode = mockFlag;

            var host = new McpStdioHost(registry);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            using var telemetryCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            _ = TelemetryTool.StartAutoFlushLoop(telemetryCts.Token);

            try
            {
                await host.RunAsync(Console.In, Console.Out, cts.Token).ConfigureAwait(false);
            }
            finally
            {
                telemetryCts.Cancel();
                await TelemetryTool.FlushOnShutdownAsync().ConfigureAwait(false);
            }
            return 0;
        }

        private static bool TryPrintHiddenEpigraph(string flag)
        {
            if (string.IsNullOrWhiteSpace(flag)) return false;
            string f = flag.Trim().ToLowerInvariant();
            if (f != "--marco" && f != "--scio" && f != "--filosofia" && f != "--wisdom")
                return false;

            string[] aphorisms =
            {
                "\"Modelamos como quien ya construyo.\" — Marco Poma · SCIO DC",
                "\"No es sabio el que promete en papel, sino el que mide en obra y vuelve a leer la realidad.\" — Marco Poma · SCIO",
                "\"La forma obedece al peso, el modelo a la obra, y el codigo a la verdad verificada.\" — Sutra · SCIO DC · Lima, Peru",
                "\"Quien construye dos veces —primero en pensamiento y luego en concreto— descubre que el rigor es una forma de respeto.\" — Marco Poma · SCIO",
                "\"Todo puente verdadero une dos orillas sin mentir sobre el vacio que cruza.\" — Sutra Revit MCP · Marco Poma"
            };

            int idx = f == "--marco" ? 0
                    : f == "--scio" ? 2
                    : Math.Abs(DateTime.UtcNow.DayOfYear) % aphorisms.Length;

            Console.Out.WriteLine(aphorisms[idx]);
            return true;
        }
    }
}
