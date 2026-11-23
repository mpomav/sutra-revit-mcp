#nullable disable
// -----------------------------------------------------------------------------
// SCIO Revit MCP - hand a task from the pane to the connected AI client.
//
// The pane runs typed commands; it has no language model. When a request needs
// one - a procedure, a free-text question, "fix what this audit found" - the
// honest move is to pass it to the client the person already connected (Claude
// Code, Claude Desktop, Codex, Cursor...) with everything the pane already knows
// written in: the document, the view, the selection, the result just produced.
//
// It goes through the CLIPBOARD, which every client accepts, and nothing is sent
// anywhere by this code. The text names the MCP prompt as a slash command for
// clients that expose prompts that way (Claude Code: /mcp__scio-revit__<id>) and
// also spells the request out in words for those that do not.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Scio.Revit.Ui
{
    internal static class ClaudeHandoff
    {
        /// <summary>The MCP server name SCIO registers under in every client (see connect-client.ps1).</summary>
        internal const string ServerName = "scio-revit";

        internal static string SlashCommand(string promptId) => "/mcp__" + ServerName + "__" + promptId;

        /// <summary>A procedure, with the arguments the person typed.</summary>
        internal static string ForProcedure(CatalogEntry e, IDictionary<string, string> args, AssistantContext ctx, bool es)
        {
            var sb = new StringBuilder();
            sb.AppendLine(SlashCommand(e.PromptId) + Inline(args));
            sb.AppendLine();
            sb.AppendLine(es
                ? "Usa el MCP de SCIO (" + ServerName + ") para ejecutar el procedimiento \"" + e.Title(true) + "\" (prompt " + e.PromptId + ")."
                : "Use the SCIO MCP (" + ServerName + ") to run the \"" + e.Title(false) + "\" procedure (prompt " + e.PromptId + ").");
            AppendArgs(sb, args, es);
            AppendContext(sb, ctx, es);
            AppendRules(sb, es);
            return sb.ToString();
        }

        /// <summary>A typed tool the person would rather have the AI drive (or explain).</summary>
        internal static string ForTool(CatalogEntry e, JObject args, AssistantContext ctx, bool es)
        {
            var sb = new StringBuilder();
            sb.AppendLine(es
                ? "Con el MCP de SCIO (" + ServerName + "), ejecuta " + e.Tool + " (" + e.Title(true) + ") con estos argumentos y explÃ­came el resultado:"
                : "With the SCIO MCP (" + ServerName + "), run " + e.Tool + " (" + e.Title(false) + ") with these arguments and explain the result:");
            sb.AppendLine(args?.ToString(Newtonsoft.Json.Formatting.None) ?? "{}");
            AppendContext(sb, ctx, es);
            AppendRules(sb, es);
            return sb.ToString();
        }

        /// <summary>A result card: continue from what the pane just produced.</summary>
        internal static string ForResult(string title, string summary, IList<long> ids, string file, AssistantContext ctx, bool es)
        {
            var sb = new StringBuilder();
            sb.AppendLine(es
                ? "Acabo de ejecutar \"" + title + "\" desde el panel SCIO en Revit. Resultado:"
                : "I just ran \"" + title + "\" from the SCIO pane in Revit. Result:");
            sb.AppendLine(summary ?? "");
            if (ids != null && ids.Count > 0)
                sb.AppendLine((es ? "Elementos implicados: " : "Elements involved: ") + string.Join(", ", ids.Take(200)));
            if (!string.IsNullOrEmpty(file)) sb.AppendLine((es ? "Reporte: " : "Report: ") + file);
            sb.AppendLine(es
                ? "AyÃºdame a decidir el siguiente paso usando el MCP de SCIO (" + ServerName + ")."
                : "Help me decide the next step using the SCIO MCP (" + ServerName + ").");
            AppendContext(sb, ctx, es);
            AppendRules(sb, es);
            return sb.ToString();
        }

        /// <summary>Free text the pane cannot answer on its own.</summary>
        internal static string ForQuestion(string question, AssistantContext ctx, bool es)
        {
            var sb = new StringBuilder();
            sb.AppendLine(question?.Trim() ?? "");
            sb.AppendLine();
            sb.AppendLine(es ? "Usa el MCP de SCIO (" + ServerName + ")." : "Use the SCIO MCP (" + ServerName + ").");
            AppendContext(sb, ctx, es);
            AppendRules(sb, es);
            return sb.ToString();
        }

        private static string Inline(IDictionary<string, string> args)
        {
            if (args == null) return "";
            var parts = args.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Value.Trim());
            string s = string.Join(" ", parts);
            return s.Length == 0 ? "" : " " + s;
        }

        private static void AppendArgs(StringBuilder sb, IDictionary<string, string> args, bool es)
        {
            if (args == null) return;
            var filled = args.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToList();
            if (filled.Count == 0) return;
            sb.AppendLine(es ? "Argumentos:" : "Arguments:");
            foreach (var kv in filled) sb.AppendLine("- " + kv.Key + ": " + kv.Value.Trim());
        }

        private static void AppendContext(StringBuilder sb, AssistantContext ctx, bool es)
        {
            if (ctx == null) return;
            sb.AppendLine();
            sb.AppendLine(es ? "Contexto de Revit (leÃ­do por el panel SCIO):" : "Revit context (read by the SCIO pane):");
            sb.AppendLine("- " + (es ? "Documento activo: " : "Active document: ") + (ctx.HasDocument ? ctx.DocumentTitle : (es ? "ninguno" : "none")));
            if (!string.IsNullOrEmpty(ctx.ActiveViewName)) sb.AppendLine("- " + (es ? "Vista activa: " : "Active view: ") + ctx.ActiveViewName);
            if (ctx.HasSelection)
            {
                string cats = string.Join(", ", ctx.Categories.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " (" + kv.Value + ")"));
                sb.AppendLine("- " + (es ? "SelecciÃ³n: " : "Selection: ") + ctx.SelectedIds.Count + (cats.Length > 0 ? " â€” " + cats : ""));
                sb.AppendLine("- element_ids: [" + string.Join(", ", ctx.SelectedIds.Take(200)) + (ctx.SelectedIds.Count > 200 ? ", ..." : "") + "]");
            }
        }

        private static void AppendRules(StringBuilder sb, bool es)
        {
            sb.AppendLine();
            sb.AppendLine(es
                ? "Llama primero a scio_health. Ensaya toda escritura (dry_run) y pÃ­deme confirmaciÃ³n antes de aplicarla."
                : "Call scio_health first. Rehearse every write (dry_run) and ask me before applying it.");
        }
    }
}

