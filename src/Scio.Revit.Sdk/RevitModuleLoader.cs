using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Scio.Revit.Sdk
{
    /// <summary>
    /// Modular registration entry point implemented by every Scio.Module.* assembly.
    /// </summary>
    public interface IRevitModule
    {
        string ModuleName { get; }
        IEnumerable<IRevitCommand> CreateCommands();
    }

    public sealed class ModuleLoadReport
    {
        public List<string> LoadedModules { get; } = new List<string>();
        public List<string> Errors { get; } = new List<string>();
        public List<IRevitCommand> Commands { get; } = new List<IRevitCommand>();
    }

    /// <summary>
    /// Discovers and instantiates IRevitModule implementations from the add-in directory
    /// and already-loaded SCIO assemblies.
    /// </summary>
    public static class RevitModuleLoader
    {
        public static ModuleLoadReport DiscoverAndLoad(string? baseDirectory = null)
        {
            var report = new ModuleLoadReport();
            var assemblies = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string? name = asm.GetName().Name;
                if (name != null && IsCandidateAssemblyName(name))
                    assemblies[name] = asm;
            }

            string dir = baseDirectory ?? Path.GetDirectoryName(typeof(RevitModuleLoader).Assembly.Location) ?? string.Empty;
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                foreach (string dll in Directory.GetFiles(dir, "Scio.*.dll"))
                {
                    string fileName = Path.GetFileNameWithoutExtension(dll);
                    if (!IsCandidateAssemblyName(fileName) || assemblies.ContainsKey(fileName))
                        continue;

                    try
                    {
                        var loaded = Assembly.LoadFrom(dll);
                        assemblies[fileName] = loaded;
                    }
                    catch (Exception ex)
                    {
                        report.Errors.Add($"{fileName}: {ex.Message}");
                    }
                }
            }

            foreach (var kvp in assemblies.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                LoadFromAssembly(kvp.Value, report);
            }

            return report;
        }

        public static void LoadFromAssembly(Assembly assembly, ModuleLoadReport report)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException rtle)
            {
                types = rtle.Types.Where(t => t != null).ToArray()!;
                foreach (var le in rtle.LoaderExceptions.Where(e => e != null))
                    report.Errors.Add($"{assembly.GetName().Name}: {le!.Message}");
            }
            catch (Exception ex)
            {
                report.Errors.Add($"{assembly.GetName().Name}: {ex.Message}");
                return;
            }

            var moduleTypes = types
                .Where(t => t != null && !t.IsAbstract && !t.IsInterface && typeof(IRevitModule).IsAssignableFrom(t))
                .ToList();

            foreach (var moduleType in moduleTypes)
            {
                try
                {
                    var module = (IRevitModule)Activator.CreateInstance(moduleType)!;
                    report.LoadedModules.Add(module.ModuleName);
                    foreach (var cmd in module.CreateCommands())
                    {
                        if (cmd != null)
                            report.Commands.Add(cmd);
                    }
                }
                catch (Exception ex)
                {
                    report.Errors.Add($"{moduleType.FullName}: {ex.Message}");
                }
            }
        }

        private static bool IsCandidateAssemblyName(string name)
        {
            return name.StartsWith("Scio.Module.", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Scio.Revit.Python", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Scio.Revit.CSharp", StringComparison.OrdinalIgnoreCase);
        }
    }
}
