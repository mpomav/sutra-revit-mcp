#nullable disable
// -----------------------------------------------------------------------------
// SCIO Revit MCP - Project Classification Lookup
//
// Resolves classification parameter names dynamically from project-context.json
// or caller arguments to maintain organisation-neutral design.
// Completely free of Revit API references (runs anywhere .NET runs).
// -----------------------------------------------------------------------------
using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Scio.Abstractions
{
    public static class ProjectClassificationLookup
    {
        public const string ContextFileName = "project-context.json";

        /// <summary>
        /// Attempts to find the classification parameter name from project-context.json
        /// walking up from the document's path or looking in ~/.scio/.
        /// </summary>
        public static string FindClassificationParameter(string docPathName, out string sourceDescription)
        {
            sourceDescription = null;
            if (!string.IsNullOrEmpty(docPathName))
            {
                DirectoryInfo dir = null;
                try
                {
                    if (File.Exists(docPathName)) dir = new FileInfo(docPathName).Directory;
                    else if (Directory.Exists(docPathName)) dir = new DirectoryInfo(docPathName);
                }
                catch { }

                for (int up = 0; dir != null && up <= 4; up++, dir = dir.Parent)
                {
                    string candidate = Path.Combine(dir.FullName, ContextFileName);
                    if (File.Exists(candidate))
                    {
                        string param = ReadParamFromContext(candidate);
                        if (!string.IsNullOrWhiteSpace(param))
                        {
                            sourceDescription = candidate;
                            return param;
                        }
                    }
                }
            }

            // Fallback: check %USERPROFILE%\.scio\project-context.json
            try
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string userScioContext = Path.Combine(userProfile, ".scio", ContextFileName);
                if (File.Exists(userScioContext))
                {
                    string param = ReadParamFromContext(userScioContext);
                    if (!string.IsNullOrWhiteSpace(param))
                    {
                        sourceDescription = userScioContext;
                        return param;
                    }
                }
            }
            catch { }

            return null;
        }

        public static string ReadParamFromContext(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string text = File.ReadAllText(path);
                var root = JObject.Parse(text);
                var classification = root["classification"] as JObject;
                if (classification == null) return null;

                string instanceParam = classification.Value<string>("instance_override_parameter");
                if (!string.IsNullOrWhiteSpace(instanceParam)) return instanceParam.Trim();

                string typeParam = classification.Value<string>("type_parameter");
                if (!string.IsNullOrWhiteSpace(typeParam)) return typeParam.Trim();

                string name = classification.Value<string>("name");
                if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
            }
            catch { }
            return null;
        }

        public sealed class NamingPatternsInfo
        {
            public string SheetPattern { get; set; }
            public string ViewPattern { get; set; }
            public string FamilyPattern { get; set; }
            public string SourcePath { get; set; }

            public bool HasAny =>
                !string.IsNullOrWhiteSpace(SheetPattern) ||
                !string.IsNullOrWhiteSpace(ViewPattern) ||
                !string.IsNullOrWhiteSpace(FamilyPattern);
        }

        public static NamingPatternsInfo FindNamingPatterns(string docPathName, out string sourceDescription)
        {
            sourceDescription = null;
            if (!string.IsNullOrEmpty(docPathName))
            {
                DirectoryInfo dir = null;
                try
                {
                    if (File.Exists(docPathName)) dir = new FileInfo(docPathName).Directory;
                    else if (Directory.Exists(docPathName)) dir = new DirectoryInfo(docPathName);
                }
                catch { }

                for (int up = 0; dir != null && up <= 4; up++, dir = dir.Parent)
                {
                    string candidate = Path.Combine(dir.FullName, ContextFileName);
                    var info = ReadNamingFromContext(candidate);
                    if (info != null && info.HasAny)
                    {
                        sourceDescription = candidate;
                        return info;
                    }
                }
            }

            try
            {
                string userScioContext = Path.Combine(ScioPaths.DataRoot(), ContextFileName);
                var info = ReadNamingFromContext(userScioContext);
                if (info != null && info.HasAny)
                {
                    sourceDescription = userScioContext;
                    return info;
                }
            }
            catch { }

            return null;
        }

        public static NamingPatternsInfo ReadNamingFromContext(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var root = JObject.Parse(File.ReadAllText(path));
                var naming = root["naming"] as JObject;
                if (naming == null) return null;

                var revitObj = naming["revit"] as JObject ?? naming;
                return new NamingPatternsInfo
                {
                    SheetPattern = NonEmpty(revitObj.Value<string>("sheet_pattern")) ?? NonEmpty(naming.Value<string>("sheet_pattern")),
                    ViewPattern = NonEmpty(revitObj.Value<string>("view_pattern")) ?? NonEmpty(naming.Value<string>("view_pattern")),
                    FamilyPattern = NonEmpty(revitObj.Value<string>("family_pattern")) ?? NonEmpty(naming.Value<string>("family_pattern")),
                    SourcePath = path
                };
            }
            catch { }
            return null;
        }

        private static string NonEmpty(string s)
            => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}

