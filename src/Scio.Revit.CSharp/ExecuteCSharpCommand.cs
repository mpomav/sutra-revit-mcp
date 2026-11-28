using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;
#if NET8_0_OR_GREATER
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
#endif

namespace Scio.Revit.CSharp
{
    public sealed class ExecuteCSharpInput
    {
        [ScioParam("Inline C# statement body or full 'public static class ScioDynamicScript' with 'public static object? Run(UIApplication uiapp, UIDocument? uidoc, Document doc, JObject args)'.")]
        public string? Code { get; set; }

        [ScioParam("Optional absolute path to a .cs file to compile and execute instead of inline code.")]
        public string? CodePath { get; set; }

        [ScioParam("Optional arguments JSON object passed to Run(..., args).")]
        public JObject? Args { get; set; }

        [ScioParam("Optional expected active document title or path.")]
        public string? TargetDocument { get; set; }
    }

    /// <summary>
    /// Clean-room in-memory Roslyn C# execution command.
    /// Always requires PermissionProfile.UnsafeCode, runs inside a collectible AssemblyLoadContext
    /// on Revit 2025+ (.NET 8/10), refuses honestly on Revit 2023-2024 (.NET 4.8), and always
    /// reports VerificationStatus.SelfReportedVerified (host_verified = false).
    /// </summary>
    [ScioTool(
        "scio_execute_csharp",
        "unsafe_code",
        "Compile in-memory with Roslyn and execute an authorized C# script inside Revit 2025+ (.NET 8/10) using a collectible AssemblyLoadContext. Disabled by default (requires UnsafeCode).",
        ReadOnly = false)]
    public sealed class ExecuteCSharpCommand : IRevitCommand
    {
        public ToolDescriptor Descriptor { get; } =
            JsonSchemaBuilder.DescribeTool(typeof(ExecuteCSharpCommand), typeof(ExecuteCSharpInput));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            if (context.PermissionProfile != PermissionProfile.UnsafeCode)
            {
                return ToolResponse.Refusal(
                    request.RequestId,
                    "csharp_not_authorized",
                    "Dynamic C# execution is disabled. Enable UnsafeCode permission in Sutra settings first.");
            }

#if !NET8_0_OR_GREATER
            return ToolResponse.Refusal(
                request.RequestId,
                "unsupported_runtime_net48",
                "scio_execute_csharp requires Revit 2025+ (.NET 8/10) for collectible AssemblyLoadContext unloading. On Revit 2023-2024 (.NET Framework 4.8), use typed Sutra tools or scio_execute_python.");
#else
            var input = request.Arguments.ToObject<ExecuteCSharpInput>() ?? new ExecuteCSharpInput();
            string? rawCode = input.Code;

            if (!string.IsNullOrWhiteSpace(input.CodePath))
            {
                string? pathProblem = ScioRuntimePaths.ValidateLocalOutputPath(input.CodePath, "code_path");
                if (pathProblem != null)
                    return ToolResponse.Refusal(request.RequestId, "invalid_code_path", pathProblem);
                if (!input.CodePath!.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    return ToolResponse.Refusal(request.RequestId, "invalid_code_path", "code_path must point to a local .cs script file.");
                if (!File.Exists(input.CodePath))
                    return ToolResponse.Refusal(request.RequestId, "file_not_found", $"C# script file not found: {input.CodePath}");
                rawCode = File.ReadAllText(input.CodePath, Encoding.UTF8);
            }

            if (string.IsNullOrWhiteSpace(rawCode))
                return ToolResponse.Refusal(request.RequestId, "missing_code", "Provide either 'code' or 'code_path'.");

            var doc = context.RequireActiveDocument(input.TargetDocument ?? request.TargetDocument);
            string wrappedSource = CSharpScriptHelper.WrapIfNeeded(rawCode!);
            string codeSha = CSharpScriptHelper.ComputeSha256(wrappedSource);

            var syntaxTree = CSharpSyntaxTree.ParseText(wrappedSource);
            var references = CollectMetadataReferences();

            var compilation = CSharpCompilation.Create(
                assemblyName: $"ScioDyn_{Guid.NewGuid():N}",
                syntaxTrees: new[] { syntaxTree },
                references: references,
                options: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release,
                    nullableContextOptions: NullableContextOptions.Enable));

            using var peStream = new MemoryStream();
            var emitResult = compilation.Emit(peStream);

            if (!emitResult.Success)
            {
                var diagArr = new JArray();
                foreach (var diag in emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(25))
                {
                    var span = diag.Location.GetLineSpan();
                    diagArr.Add(new JObject
                    {
                        ["id"] = diag.Id,
                        ["line"] = span.StartLinePosition.Line + 1,
                        ["column"] = span.StartLinePosition.Character + 1,
                        ["message"] = diag.GetMessage()
                    });
                }

                return new ToolResponse
                {
                    RequestId = request.RequestId,
                    Ok = false,
                    Verification = "failed",
                    HostVerified = false,
                    Summary = $"C# compilation failed with {diagArr.Count} error(s).",
                    Data = new JObject
                    {
                        ["code_sha256"] = codeSha,
                        ["diagnostics"] = diagArr
                    },
                    Error = new ToolError
                    {
                        Code = "csharp_compile_error",
                        Message = diagArr.FirstOrDefault()?.Value<string>("message") ?? "Compilation failed."
                    }
                };
            }

            peStream.Position = 0;
            var alc = new CollectibleScriptLoadContext();
            var prevOut = Console.Out;
            var prevErr = Console.Error;
            using var outWriter = new StringWriter();
            using var errWriter = new StringWriter();

            try
            {
                Console.SetOut(outWriter);
                Console.SetError(errWriter);

                var asm = alc.LoadFromStream(peStream);
                var scriptType = asm.GetType(CSharpScriptHelper.DefaultTypeName)
                    ?? throw new InvalidOperationException($"Compiled script does not define '{CSharpScriptHelper.DefaultTypeName}'.");
                var runMethod = scriptType.GetMethod(
                    CSharpScriptHelper.DefaultMethodName,
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: new[] { typeof(UIApplication), typeof(UIDocument), typeof(Document), typeof(JObject) },
                    modifiers: null)
                    ?? throw new InvalidOperationException($"Type '{CSharpScriptHelper.DefaultTypeName}' must define 'public static object? Run(UIApplication uiapp, UIDocument? uidoc, Document doc, JObject args)'.");

                object? rawOutput = runMethod.Invoke(null, new object?[]
                {
                    context.UiApplication,
                    context.ActiveUiDocument,
                    doc,
                    input.Args ?? new JObject()
                });

                JToken? structuredOutput = rawOutput switch
                {
                    null => null,
                    JToken jt => jt,
                    _ => JToken.FromObject(rawOutput)
                };

                var data = new JObject
                {
                    ["code_sha256"] = codeSha,
                    ["stdout"] = outWriter.ToString(),
                    ["stderr"] = errWriter.ToString(),
                    ["output"] = structuredOutput
                };

                return ToolResponse.Success(
                    request.RequestId,
                    data,
                    "C# script completed in collectible AssemblyLoadContext (self-reported).",
                    VerificationStatus.SelfReportedVerified,
                    hostVerified: false);
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                return ToolResponse.Failure(request.RequestId, "csharp_runtime_error", inner.Message);
            }
            catch (Exception ex)
            {
                return ToolResponse.Failure(request.RequestId, "csharp_execution_error", ex.Message);
            }
            finally
            {
                Console.SetOut(prevOut);
                Console.SetError(prevErr);
                alc.Unload();
            }
#endif
        }

#if NET8_0_OR_GREATER
        private static List<MetadataReference> CollectMetadataReferences()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var refs = new List<MetadataReference>();

            void TryAdd(Assembly asm)
            {
                try
                {
                    if (asm.IsDynamic) return;
                    string loc = asm.Location;
                    if (!string.IsNullOrWhiteSpace(loc) && File.Exists(loc) && seen.Add(loc))
                        refs.Add(MetadataReference.CreateFromFile(loc));
                }
                catch { }
            }

            // Ensure core assemblies are always included
            TryAdd(typeof(object).Assembly);
            TryAdd(typeof(Enumerable).Assembly);
            TryAdd(typeof(Document).Assembly);
            TryAdd(typeof(UIApplication).Assembly);
            TryAdd(typeof(JObject).Assembly);
            TryAdd(typeof(ToolResponse).Assembly);
            TryAdd(typeof(RevitContext).Assembly);

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                TryAdd(asm);

            return refs;
        }

        private sealed class CollectibleScriptLoadContext : AssemblyLoadContext
        {
            public CollectibleScriptLoadContext() : base(isCollectible: true) { }
            protected override Assembly? Load(AssemblyName assemblyName) => null;
        }
#endif
    }
}
