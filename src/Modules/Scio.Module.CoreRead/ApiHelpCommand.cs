using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Module.CoreRead
{
    [ScioTool("scio_api_help", "core", "Reflect the live loaded RevitAPI.dll for the running Revit year (describe type or check_signature) plus optional offline CHM-converted Markdown docs.", ReadOnly = true)]
    public sealed class ApiHelpCommand : IRevitCommand
    {
        [ScioParam("operation", "Operation: describe or check_signature.", Required = true, EnumValues = new[] { "describe", "check_signature" })]
        public string Operation { get; set; } = "describe";

        [ScioParam("type_name", "Revit API type name (e.g. 'Rebar', 'Wall', 'SlabShapeEditor').", Required = true)]
        public string TypeName { get; set; } = string.Empty;

        [ScioParam("member_name", "Optional method or property name for check_signature (e.g. 'CreateFromCurves', 'AddPoint').")]
        public string? MemberName { get; set; }

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ApiHelpCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            string op = (request.Arguments.Value<string>("operation") ?? "describe").Trim().ToLowerInvariant();
            string typeName = (request.Arguments.Value<string>("type_name") ?? string.Empty).Trim();
            string? memberName = request.Arguments.Value<string>("member_name")?.Trim();
            if (string.IsNullOrWhiteSpace(typeName))
                return ToolResponse.Refusal(request.RequestId, "missing_type", "'type_name' is required.");

            Assembly revitAsm = typeof(Document).Assembly;
            Type? target = revitAsm.GetTypes().FirstOrDefault(t =>
                string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.FullName, typeName, StringComparison.OrdinalIgnoreCase));

            if (target == null)
            {
                var suggestions = revitAsm.GetTypes()
                    .Where(t => t.IsPublic && t.Name.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(t => t.FullName)
                    .Take(10)
                    .ToList();
                return ToolResponse.Refusal(request.RequestId, "type_not_found", $"Type '{typeName}' not found in RevitAPI {context.RevitYear}.", details: new JObject { ["suggestions"] = new JArray(suggestions) });
            }

            if (op == "check_signature")
            {
                if (string.IsNullOrWhiteSpace(memberName))
                    return ToolResponse.Refusal(request.RequestId, "missing_member", "'member_name' is required for check_signature.");

                var methods = target.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(m => string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase))
                    .Select(FormatMethod)
                    .ToList();
                var props = target.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                    .Where(p => string.Equals(p.Name, memberName, StringComparison.OrdinalIgnoreCase))
                    .Select(p => $"{p.PropertyType.Name} {p.Name} {{ {(p.CanRead ? "get; " : "")}{(p.CanWrite ? "set; " : "")}}}")
                    .ToList();

                return ToolResponse.Success(request.RequestId, new JObject
                {
                    ["revit_year"] = context.RevitYear,
                    ["type"] = target.FullName,
                    ["member"] = memberName,
                    ["exists"] = methods.Count > 0 || props.Count > 0,
                    ["methods"] = new JArray(methods),
                    ["properties"] = new JArray(props)
                }, $"Checked '{target.Name}.{memberName}' on Revit {context.RevitYear}: {methods.Count} method(s), {props.Count} property(ies).");
            }

            var allProps = target.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .OrderBy(p => p.Name).Take(40).Select(p => $"{p.PropertyType.Name} {p.Name}").ToList();
            var allMethods = target.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName).OrderBy(m => m.Name).Take(50).Select(FormatMethod).ToList();

            string? offlineDocSnippet = TryReadOfflineMarkdownDoc(target.Name);
            var data = new JObject
            {
                ["revit_year"] = context.RevitYear,
                ["type"] = target.FullName,
                ["base_type"] = target.BaseType?.FullName ?? string.Empty,
                ["is_enum"] = target.IsEnum,
                ["properties"] = new JArray(allProps),
                ["methods"] = new JArray(allMethods)
            };
            if (!string.IsNullOrEmpty(offlineDocSnippet)) data["offline_doc_snippet"] = offlineDocSnippet;

            return ToolResponse.Success(request.RequestId, data, $"Described '{target.FullName}' on Revit {context.RevitYear}.");
        }

        private static string FormatMethod(MethodInfo m)
        {
            string pars = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
            bool obsolete = m.GetCustomAttribute<ObsoleteAttribute>() != null;
            return $"{(m.IsStatic ? "static " : "")}{m.ReturnType.Name} {m.Name}({pars}){(obsolete ? " [OBSOLETE]" : "")}";
        }

        private static string? TryReadOfflineMarkdownDoc(string typeName)
        {
            try
            {
                string docsDir = Path.Combine(ScioRuntimePaths.DataRoot, "revit-api-docs");
                if (!Directory.Exists(docsDir)) return null;
                string? file = Directory.GetFiles(docsDir, $"*{typeName}*.md", SearchOption.AllDirectories).FirstOrDefault();
                if (file == null) return null;
                string txt = File.ReadAllText(file);
                return txt.Length > 1200 ? txt.Substring(0, 1200) + "..." : txt;
            }
            catch { return null; }
        }
    }
}
