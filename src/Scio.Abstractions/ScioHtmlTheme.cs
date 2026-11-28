#nullable disable
// -----------------------------------------------------------------------------
// SCIO Revit MCP - Unified Design Tokens & HTML Theme System.
//
// Revit-free on purpose so both the Revit add-in (HTML reports / Visual QA) and
// the MCP server (ReportTool, BatchAuditTool, MCP Apps) share one visual
// language: official SCIO Orange (#E06400), DM Sans typography, adaptive light
// and dark themes, semantic-only status indicators, and crisp inline SVGs.
// -----------------------------------------------------------------------------
using System.Net;
using System.Text;

namespace Scio.Abstractions
{
    public static class ScioHtmlTheme
    {
        public const string BrandOrange = "#E06400";
        public const string BrandOrangeDark = "#A84A00";
        public const string BrandOrangeMid = "#F28C38";
        public const string BrandOrangeLight = "#FBD3B0";
        public const string BrandOrangeSoft = "#FFF3E8";

        public const string SvgDownload = "<svg width=\"14\" height=\"14\" viewBox=\"0 0 16 16\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.7\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\"><path d=\"M8 2v8m0 0-3-3m3 3 3-3M2.5 11.5v1.5a1 1 0 0 0 1 1h9a1 1 0 0 0 1-1v-1.5\"/></svg>";
        public const string SvgPrint = "<svg width=\"14\" height=\"14\" viewBox=\"0 0 16 16\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.6\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\"><path d=\"M4 5.5V2h8v3.5M4 11.5H2.5a1 1 0 0 1-1-1v-4a1 1 0 0 1 1-1h11a1 1 0 0 1 1 1v4a1 1 0 0 1-1 1H12\"/><rect x=\"4\" y=\"9.5\" width=\"8\" height=\"4.5\" rx=\".5\"/></svg>";
        public const string SvgAlert = "<svg width=\"15\" height=\"15\" viewBox=\"0 0 16 16\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.7\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\"><path d=\"M8 2 1.5 13.5h13L8 2z\"/><path d=\"M8 6.5v3\"/><circle cx=\"8\" cy=\"11.5\" r=\".5\" fill=\"currentColor\"/></svg>";
        public const string SvgCheck = "<svg width=\"14\" height=\"14\" viewBox=\"0 0 16 16\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.8\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\"><path d=\"M3 8.5 6.5 12 13 4.5\"/></svg>";
        public const string SvgFolder = "<svg width=\"14\" height=\"14\" viewBox=\"0 0 16 16\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.6\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\"><path d=\"M2 4.5a1 1 0 0 1 1-1h3.5l1.5 1.5h5a1 1 0 0 1 1 1v6.5a1 1 0 0 1-1 1H3a1 1 0 0 1-1-1V4.5z\"/></svg>";

        /// <summary>
        /// CSS custom properties matching ScioTheme (WPF) in both Light and Dark themes.
        /// Note: uses "DM Sans" with local system fallbacks so offline CSP stays strictly closed.
        /// </summary>
        public static string CssVariables(bool includePrint = true)
        {
            var sb = new StringBuilder();
            sb.AppendLine(@"  :root {
    color-scheme: light dark;
    --scio-accent: #E06400;
    --scio-accent-hover: #C75700;
    --scio-accent-dark: #A84A00;
    --scio-accent-mid: #F28C38;
    --scio-accent-light: #FBD3B0;
    --scio-accent-soft: #FFF3E8;
    --scio-on-accent: #FFFFFF;
    --scio-bg: var(--color-background-primary, #F7F7F8);
    --scio-surface: var(--color-background-secondary, #FFFFFF);
    --scio-surface-alt: #F1F1F3;
    --scio-header-bg: #FFFFFF;
    --scio-fg: var(--color-text-primary, #262626);
    --scio-muted: var(--color-text-secondary, #666666);
    --scio-line: var(--color-border-primary, #DCDCE0);
    --scio-table-line: #BFBFBF;
    --scio-row-alt: #FFF3E8;
    --scio-row-hover: #FDE7D4;
    --scio-ok-fg: #2E7D32;
    --scio-ok-bg: #EDF7EE;
    --scio-warn-fg: #A84A00;
    --scio-warn-bg: #FFF3E8;
    --scio-bad-fg: #C00000;
    --scio-bad-bg: #FDECEC;
    --scio-radius: var(--border-radius-md, 8px);
    --scio-font: ""DM Sans"", ""Segoe UI"", var(--font-sans, ui-sans-serif, system-ui, -apple-system, sans-serif);
    --scio-mono: ""Cascadia Mono"", Consolas, ""Courier New"", monospace;
  }
  :root[data-theme=""dark""] {
    --scio-accent: #E06400;
    --scio-accent-hover: #F28C38;
    --scio-accent-dark: #FBD3B0;
    --scio-accent-mid: #F28C38;
    --scio-accent-light: #6E3A14;
    --scio-accent-soft: #332012;
    --scio-on-accent: #FFFFFF;
    --scio-bg: var(--color-background-primary, #1A1B1E);
    --scio-surface: var(--color-background-secondary, #24262B);
    --scio-surface-alt: #2E3037;
    --scio-header-bg: #24262B;
    --scio-fg: var(--color-text-primary, #ECECEE);
    --scio-muted: var(--color-text-secondary, #A1A1AA);
    --scio-line: var(--color-border-primary, #3E4046);
    --scio-table-line: #3E4046;
    --scio-row-alt: #2B2119;
    --scio-row-hover: #3A2717;
    --scio-ok-fg: #4ADE80;
    --scio-ok-bg: #163222;
    --scio-warn-fg: #FBBF24;
    --scio-warn-bg: #3A2B12;
    --scio-bad-fg: #F87171;
    --scio-bad-bg: #3B1A1A;
  }
  @media (prefers-color-scheme: dark) {
    :root:not([data-theme=""light""]) {
      --scio-accent: #E06400;
      --scio-accent-hover: #F28C38;
      --scio-accent-dark: #FBD3B0;
      --scio-accent-mid: #F28C38;
      --scio-accent-light: #6E3A14;
      --scio-accent-soft: #332012;
      --scio-on-accent: #FFFFFF;
      --scio-bg: var(--color-background-primary, #1A1B1E);
      --scio-surface: var(--color-background-secondary, #24262B);
      --scio-surface-alt: #2E3037;
      --scio-header-bg: #24262B;
      --scio-fg: var(--color-text-primary, #ECECEE);
      --scio-muted: var(--color-text-secondary, #A1A1AA);
      --scio-line: var(--color-border-primary, #3E4046);
      --scio-table-line: #3E4046;
      --scio-row-alt: #2B2119;
      --scio-row-hover: #3A2717;
      --scio-ok-fg: #4ADE80;
      --scio-ok-bg: #163222;
      --scio-warn-fg: #FBBF24;
      --scio-warn-bg: #3A2B12;
      --scio-bad-fg: #F87171;
      --scio-bad-bg: #3B1A1A;
    }
  }");
            if (includePrint)
            {
                sb.AppendLine(@"  @media print {
    :root {
      --scio-bg: #FFFFFF;
      --scio-surface: #FFFFFF;
      --scio-header-bg: #FFFFFF;
      --scio-fg: #262626;
      --scio-muted: #666666;
      --scio-line: #DCDCE0;
      --scio-table-line: #BFBFBF;
      --scio-row-alt: #FFF3E8;
    }
  }");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Complete self-contained CSS for SCIO HTML reports (Censo, Metrado, Auditoria, ReportTool, BatchAudit, VisualQA).
        /// </summary>
        public static string ReportCss()
        {
            var sb = new StringBuilder();
            sb.AppendLine(CssVariables(includePrint: true));
            sb.AppendLine(@"  * { box-sizing: border-box; margin: 0; padding: 0; }
  body {
    font-family: var(--scio-font);
    background: var(--scio-bg);
    color: var(--scio-fg);
    line-height: 1.5;
    padding-bottom: 48px;
    font-size: 13.5px;
  }
  .scio-header {
    background: var(--scio-header-bg);
    border-bottom: 3px solid var(--scio-accent);
    padding: 20px 32px;
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 16px;
    flex-wrap: wrap;
  }
  .scio-eyebrow {
    font-size: 11px;
    font-weight: 700;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    color: var(--scio-accent);
    margin-bottom: 3px;
  }
  .scio-header h1 {
    font-size: 21px;
    font-weight: 700;
    color: var(--scio-fg);
    margin: 0;
  }
  .scio-subtitle {
    font-size: 12.5px;
    color: var(--scio-muted);
    margin-top: 3px;
  }
  .scio-actions { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
  .btn {
    background: var(--scio-accent);
    color: var(--scio-on-accent);
    padding: 8px 14px;
    border-radius: 6px;
    text-decoration: none;
    font-family: inherit;
    font-size: 12.5px;
    font-weight: 600;
    border: 1px solid var(--scio-accent);
    cursor: pointer;
    display: inline-flex;
    align-items: center;
    gap: 6px;
  }
  .btn:hover { background: var(--scio-accent-hover); border-color: var(--scio-accent-hover); }
  .btn-secondary {
    background: var(--scio-surface-alt);
    color: var(--scio-fg);
    border-color: var(--scio-line);
  }
  .btn-secondary:hover { background: var(--scio-accent-soft); border-color: var(--scio-accent); color: var(--scio-accent-dark); }
  .container { max-width: 1400px; margin: 22px auto; padding: 0 24px; }
  h2 {
    font-size: 15px;
    font-weight: 700;
    color: var(--scio-accent-dark);
    margin: 24px 0 8px;
    padding-bottom: 4px;
    border-bottom: 1px solid var(--scio-line);
  }
  h3 { font-size: 13.5px; font-weight: 600; color: var(--scio-fg); margin-bottom: 6px; }
  .summary-card {
    background: var(--scio-surface);
    border: 1px solid var(--scio-line);
    border-radius: var(--scio-radius);
    padding: 18px 20px;
    margin-bottom: 18px;
  }
  .section-meta { font-size: 12.5px; color: var(--scio-muted); margin-bottom: 10px; }
  .section-meta strong { color: var(--scio-fg); }
  .meta-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(190px, 1fr));
    gap: 12px;
    margin-top: 12px;
    padding-top: 12px;
    border-top: 1px solid var(--scio-line);
    font-size: 12.5px;
  }
  .meta-item strong {
    color: var(--scio-muted);
    font-size: 10.5px;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    display: block;
    margin-bottom: 2px;
  }
  .kpi-grid, .cards {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(175px, 1fr));
    gap: 14px;
    margin-bottom: 20px;
  }
  .kpi-card, .card {
    background: var(--scio-surface);
    border: 1px solid var(--scio-line);
    border-top: 3px solid var(--scio-accent);
    border-radius: var(--scio-radius);
    padding: 14px 16px;
  }
  .kpi-title, .card .k {
    font-size: 11px;
    color: var(--scio-muted);
    text-transform: uppercase;
    letter-spacing: 0.04em;
    font-weight: 600;
  }
  .kpi-val, .card .v {
    font-size: 22px;
    font-weight: 700;
    color: var(--scio-fg);
    margin-top: 4px;
  }
  .kpi-val.accent, .card .v.accent { color: var(--scio-accent); }
  .warn-box {
    background: var(--scio-warn-bg);
    border: 1px solid var(--scio-accent-light);
    border-left: 4px solid var(--scio-accent);
    color: var(--scio-warn-fg);
    padding: 12px 16px;
    border-radius: 6px;
    margin-bottom: 18px;
    font-size: 13px;
    display: flex;
    gap: 10px;
    align-items: flex-start;
  }
  .warn-box.error {
    background: var(--scio-bad-bg);
    border-color: var(--scio-bad-fg);
    border-left-color: var(--scio-bad-fg);
    color: var(--scio-bad-fg);
  }
  .table-container {
    background: var(--scio-surface);
    border: 1px solid var(--scio-line);
    border-radius: var(--scio-radius);
    overflow: hidden;
    margin-bottom: 20px;
  }
  .table-toolbar {
    padding: 12px 18px;
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 12px;
    flex-wrap: wrap;
    border-bottom: 1px solid var(--scio-line);
    background: var(--scio-surface-alt);
  }
  .search-input {
    padding: 7px 12px;
    border: 1px solid var(--scio-line);
    border-radius: 6px;
    font-family: inherit;
    font-size: 13px;
    width: 320px;
    max-width: 100%;
    background: var(--scio-surface);
    color: var(--scio-fg);
    outline: none;
  }
  .search-input:focus { border-color: var(--scio-accent); }
  .table-scroll { overflow-x: auto; }
  table { width: 100%; border-collapse: collapse; text-align: left; font-size: 12.5px; }
  th {
    background: var(--scio-accent);
    color: var(--scio-on-accent);
    font-weight: 700;
    padding: 10px 14px;
    border-bottom: 1px solid var(--scio-table-line);
    white-space: nowrap;
    font-size: 11.5px;
    text-transform: uppercase;
    letter-spacing: 0.03em;
  }
  td { padding: 9px 14px; border-bottom: 1px solid var(--scio-table-line); vertical-align: top; }
  tbody tr:nth-child(even) { background: var(--scio-row-alt); }
  tbody tr:hover { background: var(--scio-row-hover); }
  .mono { font-family: var(--scio-mono); font-size: 12px; }
  .badge {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    padding: 2px 8px;
    border-radius: 999px;
    font-size: 11px;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.03em;
  }
  .badge-ok { background: var(--scio-ok-bg); color: var(--scio-ok-fg); }
  .badge-warn { background: var(--scio-warn-bg); color: var(--scio-warn-fg); }
  .badge-fail { background: var(--scio-bad-bg); color: var(--scio-bad-fg); }
  .badge-neutral { background: var(--scio-surface-alt); color: var(--scio-muted); }
  .gallery-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(340px, 1fr));
    gap: 16px;
  }
  .gallery-card {
    background: var(--scio-surface);
    border: 1px solid var(--scio-line);
    border-top: 3px solid var(--scio-accent);
    border-radius: var(--scio-radius);
    padding: 12px 14px;
  }
  .gallery-card img {
    width: 100%;
    height: auto;
    border: 1px solid var(--scio-line);
    border-radius: 4px;
    margin-top: 8px;
    display: block;
  }
  .scio-footer {
    max-width: 1400px;
    margin: 24px auto 0;
    padding: 12px 24px 0;
    border-top: 1px solid var(--scio-line);
    color: var(--scio-muted);
    font-size: 11.5px;
    display: flex;
    justify-content: space-between;
    flex-wrap: wrap;
    gap: 8px;
  }
  @media print {
    .scio-actions, .search-input, .table-toolbar { display: none; }
    body { padding: 0; background: #FFFFFF; }
    .container { max-width: 100%; margin: 12px 0; padding: 0; }
    .scio-header { padding: 12px 0; }
    th { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
    tbody tr:nth-child(even) { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
  }");
            return sb.ToString();
        }

        public static string RenderHeader(string eyebrow, string title, string subtitle, string actionsHtml = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("  <header class=\"scio-header\">");
            sb.AppendLine("    <div>");
            sb.AppendLine($"      <div class=\"scio-eyebrow\">{WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(eyebrow) ? "SCIO BIM" : eyebrow)}</div>");
            sb.AppendLine($"      <h1>{WebUtility.HtmlEncode(title ?? string.Empty)}</h1>");
            if (!string.IsNullOrWhiteSpace(subtitle))
                sb.AppendLine($"      <div class=\"scio-subtitle\">{WebUtility.HtmlEncode(subtitle)}</div>");
            sb.AppendLine("    </div>");
            if (!string.IsNullOrWhiteSpace(actionsHtml))
                sb.AppendLine($"    <div class=\"scio-actions\">{actionsHtml}</div>");
            sb.AppendLine("  </header>");
            return sb.ToString();
        }

        public static string RenderFooter(string leftText = "Sutra — Revit MCP · SCIO DC", string rightText = null)
        {
            return $"  <footer class=\"scio-footer\"><span>{WebUtility.HtmlEncode(leftText)}</span><span>{WebUtility.HtmlEncode(rightText ?? string.Empty)}</span></footer>";
        }
    }
}

