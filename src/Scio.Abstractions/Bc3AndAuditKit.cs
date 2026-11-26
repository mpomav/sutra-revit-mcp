using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Scio.Abstractions
{
    public sealed class BudgetLineItem
    {
        public string Code { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Unit { get; set; } = "u";
        public double Quantity { get; set; }
        public double UnitPrice { get; set; }
        public double Total => Math.Round(Quantity * UnitPrice, 2);
    }

    /// <summary>
    /// Pure helpers for Ola 4: FIEBDC-3 (.bc3) export, budget delta comparison, and 4D task matching.
    /// </summary>
    public static class Bc3AndAuditKit
    {
        public static string BuildBc3Document(string projectCode, string projectTitle, string currency, IReadOnlyList<BudgetLineItem> items)
        {
            string pCode = string.IsNullOrWhiteSpace(projectCode) ? "SUTRA" : SanitizeBc3(projectCode);
            string pTitle = string.IsNullOrWhiteSpace(projectTitle) ? "Sutra BIM Takeoff" : SanitizeBc3(projectTitle);
            string curr = string.IsNullOrWhiteSpace(currency) ? "PEN" : SanitizeBc3(currency);
            string dateStr = DateTime.UtcNow.ToString("ddMMyyyy", CultureInfo.InvariantCulture);

            double grandTotal = items.Sum(i => i.Total);
            var sb = new StringBuilder();
            sb.AppendLine($"~V|SCIO DC|FIEBDC-3/2020|Sutra Revit MCP||ANSI|");
            sb.AppendLine($"~K|\\2\\2\\3\\2\\2\\2\\2\\{curr}\\|0|");
            sb.AppendLine($"~C|{pCode}##||{pTitle}|{grandTotal.ToString("0.00", CultureInfo.InvariantCulture)}|{dateStr}|0|");

            var chapterChildren = new StringBuilder();
            foreach (var item in items)
            {
                string code = SanitizeBc3(string.IsNullOrWhiteSpace(item.Code) ? "ITEM" : item.Code);
                string unit = SanitizeBc3(string.IsNullOrWhiteSpace(item.Unit) ? "u" : item.Unit);
                string desc = SanitizeBc3(item.Description);
                sb.AppendLine($"~C|{code}|{unit}|{desc}|{item.UnitPrice.ToString("0.00", CultureInfo.InvariantCulture)}|{dateStr}|0|");
                chapterChildren.Append($"{code}\\1\\{item.Quantity.ToString("0.000", CultureInfo.InvariantCulture)}\\");
            }

            sb.AppendLine($"~D|{pCode}##|{chapterChildren}|");
            return sb.ToString();
        }

        public static JObject CompareBudgets(IReadOnlyList<BudgetLineItem> baseline, IReadOnlyList<BudgetLineItem> current)
        {
            var baseMap = (baseline ?? Array.Empty<BudgetLineItem>())
                .GroupBy(i => i.Code, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var currMap = (current ?? Array.Empty<BudgetLineItem>())
                .GroupBy(i => i.Code, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var allCodes = baseMap.Keys.Union(currMap.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

            var diffs = new JArray();
            double baseTotal = 0.0, currTotal = 0.0;
            int added = 0, removed = 0, changed = 0;

            foreach (string code in allCodes)
            {
                baseMap.TryGetValue(code, out var b);
                currMap.TryGetValue(code, out var c);
                double bt = b?.Total ?? 0.0;
                double ct = c?.Total ?? 0.0;
                baseTotal += bt;
                currTotal += ct;

                string status;
                if (b == null) { status = "added"; added++; }
                else if (c == null) { status = "removed"; removed++; }
                else if (Math.Abs(bt - ct) > 0.01 || Math.Abs(b.Quantity - c.Quantity) > 1e-4) { status = "changed"; changed++; }
                else { status = "unchanged"; }

                if (status != "unchanged")
                {
                    diffs.Add(new JObject
                    {
                        ["code"] = code,
                        ["status"] = status,
                        ["description"] = c?.Description ?? b?.Description ?? string.Empty,
                        ["baseline_qty"] = b?.Quantity ?? 0.0,
                        ["current_qty"] = c?.Quantity ?? 0.0,
                        ["delta_qty"] = Math.Round((c?.Quantity ?? 0.0) - (b?.Quantity ?? 0.0), 3),
                        ["baseline_total"] = bt,
                        ["current_total"] = ct,
                        ["delta_total"] = Math.Round(ct - bt, 2)
                    });
                }
            }

            double delta = Math.Round(currTotal - baseTotal, 2);
            double pct = Math.Abs(baseTotal) > 1e-4 ? Math.Round(100.0 * delta / baseTotal, 2) : 0.0;
            return new JObject
            {
                ["baseline_total"] = Math.Round(baseTotal, 2),
                ["current_total"] = Math.Round(currTotal, 2),
                ["delta_total"] = delta,
                ["variance_pct"] = pct,
                ["added"] = added,
                ["removed"] = removed,
                ["changed"] = changed,
                ["differences"] = diffs
            };
        }

        public static string Evaluate4DStatus(DateTime asOf, DateTime start, DateTime end)
        {
            if (asOf < start) return "not_started";
            if (asOf >= end) return "completed";
            return "in_progress";
        }

        private static string SanitizeBc3(string s)
        {
            return (s ?? string.Empty).Replace("|", " ").Replace("~", " ").Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }
}
