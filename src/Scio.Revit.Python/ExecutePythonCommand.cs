using System;
using System.IO;
using System.Text;
using IronPython.Hosting;
using Microsoft.Scripting.Hosting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Revit.Python
{
    public sealed class ExecutePythonInput
    {
        [ScioParam("Inline Python script to execute inside Revit.")]
        public string? Code { get; set; }

        [ScioParam("Optional absolute path to a .py file to execute instead of inline code.")]
        public string? CodePath { get; set; }

        [ScioParam("Optional arguments dictionary exposed to the script as __args__.")]
        public JObject? Args { get; set; }

        [ScioParam("Optional expected active document title or path.")]
        public string? TargetDocument { get; set; }
    }

    /// <summary>
    /// Clean-room IronPython execution command. Always requires PermissionProfile.UnsafeCode
    /// and reports results as self_reported_verified (host_verified = false).
    /// </summary>
    [ScioTool("scio_execute_python", "unsafe_code", "Execute an authorized IronPython script inside the active Autodesk Revit session.", ReadOnly = false)]
    public sealed class ExecutePythonCommand : IRevitCommand
    {
        private ScriptEngine? _engine;

        public ToolDescriptor Descriptor { get; } =
            JsonSchemaBuilder.DescribeTool(typeof(ExecutePythonCommand), typeof(ExecutePythonInput));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            if (context.PermissionProfile != PermissionProfile.UnsafeCode)
            {
                return ToolResponse.Refusal(
                    request.RequestId,
                    "python_not_authorized",
                    "IronPython execution is disabled. Enable UnsafeCode permission in SCIO Revit MCP settings first.");
            }

            var input = request.Arguments.ToObject<ExecutePythonInput>() ?? new ExecutePythonInput();
            string? code = input.Code;

            if (!string.IsNullOrWhiteSpace(input.CodePath))
            {
                string? pathProblem = ScioRuntimePaths.ValidateLocalOutputPath(input.CodePath, "code_path");
                if (pathProblem != null)
                    return ToolResponse.Refusal(request.RequestId, "invalid_code_path", pathProblem);
                if (!input.CodePath!.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
                    return ToolResponse.Refusal(request.RequestId, "invalid_code_path", "code_path must point to a local .py script file.");
                if (!File.Exists(input.CodePath))
                    return ToolResponse.Refusal(request.RequestId, "file_not_found", $"Script file not found: {input.CodePath}");
                code = File.ReadAllText(input.CodePath, Encoding.UTF8);
            }

            if (string.IsNullOrWhiteSpace(code))
                return ToolResponse.Refusal(request.RequestId, "missing_code", "Provide either 'code' or 'code_path'.");

            var doc = context.RequireActiveDocument(input.TargetDocument ?? request.TargetDocument);

            try
            {
                _engine ??= CreateEngine();
                var scope = _engine.CreateScope();
                scope.SetVariable("uiapp", context.UiApplication);
                scope.SetVariable("uidoc", context.ActiveUiDocument);
                scope.SetVariable("doc", doc);
                scope.SetVariable("__args__", input.Args?.ToString(Formatting.None) ?? "{}");
                scope.SetVariable("__output__", null);

                using (var stdout = new MemoryStream())
                using (var stderr = new MemoryStream())
                {
                    try
                    {
                        _engine.Runtime.IO.SetOutput(stdout, Encoding.UTF8);
                        _engine.Runtime.IO.SetErrorOutput(stderr, Encoding.UTF8);

                        var source = _engine.CreateScriptSourceFromString(code!);
                        source.Execute(scope);

                        string outText = Encoding.UTF8.GetString(stdout.ToArray());
                        string errText = Encoding.UTF8.GetString(stderr.ToArray());

                        object? rawOutput = scope.ContainsVariable("__output__") ? scope.GetVariable("__output__") : null;
                        JToken? structuredOutput = rawOutput != null ? JToken.FromObject(rawOutput) : null;

                        var data = new JObject
                        {
                            ["stdout"] = outText,
                            ["stderr"] = errText,
                            ["output"] = structuredOutput
                        };

                        return ToolResponse.Success(
                            request.RequestId,
                            data,
                            "Python script completed (self-reported).",
                            VerificationStatus.SelfReportedVerified,
                            hostVerified: false);
                    }
                    finally
                    {
                        try
                        {
                            _engine.Runtime.IO.SetOutput(Stream.Null, Encoding.UTF8);
                            _engine.Runtime.IO.SetErrorOutput(Stream.Null, Encoding.UTF8);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                return ToolResponse.Failure(request.RequestId, "python_execution_error", ex.Message);
            }
        }

        private static ScriptEngine CreateEngine()
        {
#if NET8_0_OR_GREATER
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#endif
            var engine = IronPython.Hosting.Python.CreateEngine();
            engine.Runtime.LoadAssembly(typeof(Autodesk.Revit.DB.Document).Assembly);
            engine.Runtime.LoadAssembly(typeof(Autodesk.Revit.UI.UIApplication).Assembly);
            return engine;
        }
    }
}
