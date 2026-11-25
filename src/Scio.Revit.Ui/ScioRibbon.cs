using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Scio.Abstractions;

namespace Scio.Revit.Ui
{
    public sealed class ScioBridgeStatusSnapshot
    {
        public string Version { get; set; } = "1.0.0";
        public int RevitYear { get; set; }
        public string PipeName { get; set; } = string.Empty;
        public string ContractHash { get; set; } = string.Empty;
        public int RegisteredTools { get; set; }
        public PermissionProfile PermissionProfile { get; set; } = PermissionProfile.SafeWrite;
        public bool IsPaused { get; set; }
        public List<string> LoadedModules { get; set; } = new List<string>();
    }

    /// <summary>
    /// Host-injected hooks allowing Ribbon UI commands to inspect and control the live bridge.
    /// </summary>
    public static class ScioBridgeUiHooks
    {
        public static Func<ScioBridgeStatusSnapshot>? GetStatus { get; set; }
        public static Func<bool>? TogglePause { get; set; }
        public static Func<PermissionProfile>? TogglePythonAccess { get; set; }
    }

    public static class ScioRibbon
    {
        public const string TabName = "Sutra";

        public static void Build(UIControlledApplication app)
        {
            if (app == null) return;

            try { app.CreateRibbonTab(TabName); }
            catch { /* tab already exists */ }

            RibbonPanel panel = app.CreateRibbonPanel(TabName, "SCIO Revit MCP");
            string asmPath = Assembly.GetExecutingAssembly().Location;

            var statusData = new PushButtonData(
                "ScioConnectionStatus",
                "Estado de\nconexión",
                asmPath,
                typeof(ConnectionStatusCommand).FullName)
            {
                ToolTip = "Ver estado del puente SCIO Revit MCP v4, módulos cargados y hash de contrato."
            };
            TrySetIcon(statusData, "scio_status_32.png", "scio_status_16.png");
            panel.AddItem(statusData);

            var advData = new PulldownButtonData("ScioAdvancedOptions", "Opciones\navanzadas")
            {
                ToolTip = "Controles locales del propietario: pausar MCP y autorización de Python."
            };
            TrySetIcon(advData, "scio_settings_32.png", "scio_settings_16.png");
            var pulldown = panel.AddItem(advData) as PulldownButton;
            if (pulldown != null)
            {
                pulldown.AddPushButton(new PushButtonData(
                    "ScioTogglePause",
                    "Pausar / Reanudar MCP",
                    asmPath,
                    typeof(TogglePauseCommand).FullName)
                {
                    ToolTip = "Pausa o reanuda la ejecución remota de herramientas MCP en esta sesión de Revit."
                });

                pulldown.AddPushButton(new PushButtonData(
                    "ScioTogglePython",
                    "Python ON / OFF",
                    asmPath,
                    typeof(TogglePythonCommand).FullName)
                {
                    ToolTip = "Autoriza o revoca scio_execute_python (SafeWrite <-> UnsafeCode) para esta sesión."
                });
            }
        }

        private static void TrySetIcon(ButtonData data, string largeName, string smallName)
        {
            try
            {
                string asmName = Assembly.GetExecutingAssembly().GetName().Name;
                var large = LoadPackImage(asmName, largeName);
                var small = LoadPackImage(asmName, smallName);
                if (large != null) data.LargeImage = large;
                if (small != null) data.Image = small;
            }
            catch { /* non-fatal */ }
        }

        private static BitmapImage? LoadPackImage(string asmName, string fileName)
        {
            try
            {
                var uri = new Uri($"pack://application:,,,/{asmName};component/Resources/{fileName}", UriKind.Absolute);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = uri;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                return bmp;
            }
            catch
            {
                return null;
            }
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class ConnectionStatusCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var snap = ScioBridgeUiHooks.GetStatus?.Invoke() ?? new ScioBridgeStatusSnapshot();
            string modules = snap.LoadedModules.Count == 0 ? "(ninguno)" : string.Join(", ", snap.LoadedModules);
            string body =
                $"Producto: SCIO Revit MCP v{snap.Version}\n" +
                $"Revit: {snap.RevitYear}\n" +
                $"Estado: {(snap.IsPaused ? "PAUSADO" : "ACTIVO")}\n" +
                $"Perfil de permisos: {snap.PermissionProfile}\n" +
                $"Herramientas registradas: {snap.RegisteredTools}\n" +
                $"Módulos: {modules}\n" +
                $"Contract Hash: {snap.ContractHash}\n" +
                $"Named Pipe: {snap.PipeName}";

            TaskDialog.Show("SCIO Revit MCP — Estado de conexión", body);
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class TogglePauseCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            bool paused = ScioBridgeUiHooks.TogglePause?.Invoke() ?? false;
            TaskDialog.Show(
                "SCIO Revit MCP",
                paused
                    ? "Puente MCP PAUSADO. Las llamadas remotas serán rechazadas hasta que lo reanudes."
                    : "Puente MCP REANUDADO. Las herramientas vuelven a aceptar solicitudes.");
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class TogglePythonCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var profile = ScioBridgeUiHooks.TogglePythonAccess?.Invoke() ?? PermissionProfile.SafeWrite;
            TaskDialog.Show(
                "SCIO Revit MCP — Python",
                profile == PermissionProfile.UnsafeCode
                    ? "Python AUTORIZADO (UnsafeCode). scio_execute_python está habilitado en esta sesión."
                    : "Python DESACTIVADO (SafeWrite). Solo las herramientas tipadas están habilitadas.");
            return Result.Succeeded;
        }
    }
}
