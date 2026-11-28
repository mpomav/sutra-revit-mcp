using System;
using System.Reflection;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Scio.Cad.Commands;
using Scio.Cad.Core;
using Scio.Cad.Transport;
using Scio.Cad.Ui;

[assembly: ExtensionApplication(typeof(Scio.Cad.SutraCadApp))]
[assembly: CommandClass(typeof(Scio.Cad.Ui.SutraRibbonCommands))]

namespace Scio.Cad
{
    public sealed class SutraCadApp : IExtensionApplication
    {
        public static SutraCadApp Instance { get; private set; }

        private CadPipeServer _pipeServer;

        public string CadYear { get; private set; }
        public string HostKind { get; private set; }
        public string AddinVersion { get; private set; }
        public string PipeName => _pipeServer?.PipeName;
        public string DiscoveryFile => _pipeServer?.DiscoveryFile;

        public void Initialize()
        {
            Instance = this;
            CadYear = DetectCadYear();
            AddinVersion = ReadVersion();
            HostKind = Civil3DInspectCommand.IsCivil3DLoaded() ? "civil3d" : "autocad";

            CadLog.Init(CadYear);
            CadLog.Info("Initializing Sutra CAD add-in (" + HostKind + " " + CadYear + ", v" + AddinVersion + ")");

            var uiDispatcher = Dispatcher.CurrentDispatcher;
            var dispatcher = new CadDispatcher(uiDispatcher);

            dispatcher.Register("scio_health", args => CadHealthCommand.Execute(args, this));
            dispatcher.Register("cad_context", CadContextCommand.Execute);
            dispatcher.Register("cad_censo", CadCensoCommand.Execute);
            dispatcher.Register("cad_texts", CadTextsCommand.Execute);
            dispatcher.Register("cad_entities", CadEntitiesCommand.Execute);
            dispatcher.Register("c3d_inspect", Civil3DInspectCommand.Execute);
            dispatcher.Register("cad_write", CadWriteCommand.Execute);
            dispatcher.Register("c3d_write", Civil3DWriteCommand.Execute);
            dispatcher.Register("c3d_export", Civil3DExportCommand.Execute);
            dispatcher.Register("cad_cleanup", CadCleanupCommand.Execute);
            dispatcher.Register("cad_point_cloud", CadPointCloudCommand.Execute);
            dispatcher.Register("cad_draft", CadDraftCommand.Execute);
            dispatcher.Register("cad_arch_plan", CadArchPlanCommand.Execute);
            dispatcher.Register("cad_detail", CadDetailCommand.Execute);
            dispatcher.Register("cad_mep", CadMepCommand.Execute);
            dispatcher.Register("cad_criteria", CadCriteriaCommand.Execute);
            dispatcher.Register("cad_struct", CadStructCommand.Execute);
            dispatcher.Register("cad_civil_draft", CadCivilDraftCommand.Execute);

            _pipeServer = new CadPipeServer(dispatcher, CadYear, HostKind, AddinVersion);
            _pipeServer.Start();

            SutraRibbon.Attach();
        }

        public void Terminate()
        {
            try { _pipeServer?.Stop(); } catch { }
            CadLog.Info("Sutra CAD add-in terminated.");
        }

        private static string DetectCadYear()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                foreach (var attr in asm.GetCustomAttributes<AssemblyMetadataAttribute>())
                {
                    if (string.Equals(attr.Key, "ScioCadYear", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(attr.Value))
                        return attr.Value.Trim();
                }
            }
            catch { }

            try
            {
                // AutoCAD 2025 -> Major 25, 2024 -> 24.3, 2023 -> 24.2, 2026 -> 25.1
                Version v = Application.Version;
                if (v.Major == 25 && v.Minor >= 1) return "2026";
                if (v.Major == 25) return "2025";
                if (v.Major == 24 && v.Minor >= 3) return "2024";
                if (v.Major == 24) return "2023";
            }
            catch { }

            return "2025";
        }

        private static string ReadVersion()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var attr = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    asm, typeof(AssemblyInformationalVersionAttribute));
                string v = attr != null ? attr.InformationalVersion : asm.GetName().Version?.ToString();
                if (string.IsNullOrEmpty(v)) return "1.0.0";
                int plus = v.IndexOf('+');
                return plus > 0 ? v.Substring(0, plus) : v;
            }
            catch { return "1.0.0"; }
        }
    }
}
