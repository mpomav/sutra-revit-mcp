using System;
using System.Security.Cryptography;
using System.Text;

namespace Scio.Abstractions
{
    /// <summary>
    /// Pure helper for preparing dynamic C# snippets and computing deterministic SHA-256 fingerprints.
    /// </summary>
    public static class CSharpScriptHelper
    {
        public const string DefaultTypeName = "ScioDynamicScript";
        public const string DefaultMethodName = "Run";

        /// <summary>
        /// Wraps bare C# statements in a standard ScioDynamicScript.Run(...) class when the caller
        /// does not already declare a class named ScioDynamicScript.
        /// </summary>
        public static string WrapIfNeeded(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                throw new ArgumentException("Code cannot be empty.", nameof(code));

            if (code.IndexOf($"class {DefaultTypeName}", StringComparison.Ordinal) >= 0)
                return code;

            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.IO;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using System.Text;");
            sb.AppendLine("using Autodesk.Revit.DB;");
            sb.AppendLine("using Autodesk.Revit.DB.Architecture;");
            sb.AppendLine("using Autodesk.Revit.DB.Structure;");
            sb.AppendLine("using Autodesk.Revit.UI;");
            sb.AppendLine("using Newtonsoft.Json;");
            sb.AppendLine("using Newtonsoft.Json.Linq;");
            sb.AppendLine("using Scio.Abstractions;");
            sb.AppendLine("using Scio.Revit.Sdk;");
            sb.AppendLine();
            sb.AppendLine($"public static class {DefaultTypeName}");
            sb.AppendLine("{");
            sb.AppendLine($"    public static object? {DefaultMethodName}(UIApplication uiapp, UIDocument? uidoc, Document doc, JObject args)");
            sb.AppendLine("    {");
            sb.AppendLine(code);
            if (code.IndexOf("return ", StringComparison.Ordinal) < 0 && code.IndexOf("return;", StringComparison.Ordinal) < 0)
                sb.AppendLine("        return null;");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        public static string ComputeSha256(string text)
        {
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }
}
