using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Scio.Abstractions;

namespace Scio.Revit.Ui
{
    public sealed class ScioBridgeStatusSnapshot
    {
        public string Version { get; set; } = "1.3.0";
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
                ToolTip = "Ver estado del puente SCIO Revit MCP, módulos cargados y hash de contrato.",
                LongDescription = "Muestra el estado en vivo del puente MCP, la cantidad de herramientas registradas, el perfil de permisos activo y permite copiar el diagnóstico."
            };
            SetVectorIcon(statusData, RibbonIconKind.Status);
            panel.AddItem(statusData);

            var advData = new PulldownButtonData("ScioAdvancedOptions", "Opciones\navanzadas")
            {
                ToolTip = "Controles locales del propietario: pausar MCP y autorización de Python.",
                LongDescription = "Permite pausar o reanudar la ejecución remota de herramientas MCP y autorizar o revocar scio_execute_python en esta sesión."
            };
            SetVectorIcon(advData, RibbonIconKind.Settings);
            var pulldown = panel.AddItem(advData) as PulldownButton;
            if (pulldown != null)
            {
                var pauseBtn = new PushButtonData(
                    "ScioTogglePause",
                    "Pausar / Reanudar MCP",
                    asmPath,
                    typeof(TogglePauseCommand).FullName)
                {
                    ToolTip = "Pausa o reanuda la ejecución remota de herramientas MCP en esta sesión de Revit."
                };
                SetVectorIcon(pauseBtn, RibbonIconKind.Pause);
                pulldown.AddPushButton(pauseBtn);

                var pyBtn = new PushButtonData(
                    "ScioTogglePython",
                    "Python ON / OFF",
                    asmPath,
                    typeof(TogglePythonCommand).FullName)
                {
                    ToolTip = "Autoriza o revoca scio_execute_python (SafeWrite <-> UnsafeCode) para esta sesión."
                };
                SetVectorIcon(pyBtn, RibbonIconKind.Python);
                pulldown.AddPushButton(pyBtn);

                var telBtn = new PushButtonData(
                    "ScioToggleTelemetry",
                    "Telemetría anónima (ON / OFF)",
                    asmPath,
                    typeof(ToggleTelemetryCommand).FullName)
                {
                    ToolTip = "Activa o desactiva el envío automático de telemetría anónima de uso (sin datos de modelos)."
                };
                SetVectorIcon(telBtn, RibbonIconKind.Settings);
                pulldown.AddPushButton(telBtn);
            }

            panel.AddSeparator();

            var aboutData = new PushButtonData(
                "ScioAboutSutra",
                "Acerca de\nSutra",
                asmPath,
                typeof(AboutCommand).FullName)
            {
                ToolTip = "Información de Sutra — SCIO Revit MCP, versión, licencia y módulos activos.",
                LongDescription = "Abre la ventana Acerca de con la versión instalada, información de SCIO DC, estado del puente y enlace a sciodc.com."
            };
            SetVectorIcon(aboutData, RibbonIconKind.About);
            panel.AddItem(aboutData);
        }

        private static void SetVectorIcon(ButtonData data, RibbonIconKind kind)
        {
            try
            {
                data.LargeImage = RibbonIconRenderer.Render(kind, 32);
                data.Image = RibbonIconRenderer.Render(kind, 16);
            }
            catch { /* non-fatal: never block Revit startup */ }
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class ConnectionStatusCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var snap = ScioBridgeUiHooks.GetStatus?.Invoke() ?? new ScioBridgeStatusSnapshot();
            string modules = snap.LoadedModules.Count == 0 ? "(ninguno)" : string.Join(", ", snap.LoadedModules);
            string techDetail =
                $"Producto      : Sutra — SCIO Revit MCP v{snap.Version}\n" +
                $"Revit         : {snap.RevitYear}\n" +
                $"Estado        : {(snap.IsPaused ? "PAUSADO" : "ACTIVO")}\n" +
                $"Permisos      : {snap.PermissionProfile}\n" +
                $"Herramientas  : {snap.RegisteredTools}\n" +
                $"Módulos       : {modules}\n" +
                $"Contract Hash : {snap.ContractHash}\n" +
                $"Named Pipe    : {snap.PipeName}";

            IntPtr owner = commandData?.Application?.MainWindowHandle ?? IntPtr.Zero;
            ScioDialog.ShowResult(
                owner: owner,
                title: "Estado de conexión",
                eyebrow: "SUTRA · SCIO REVIT MCP",
                headline: snap.IsPaused
                    ? "El puente MCP está en pausa en esta sesión de Revit."
                    : "El puente MCP está escuchando y listo para recibir comandos.",
                summary: snap.IsPaused
                    ? "PAUSADO — las llamadas remotas están temporalmente bloqueadas."
                    : $"ACTIVO — conectado en Revit {snap.RevitYear} con {snap.RegisteredTools} herramientas registradas.",
                kpis: new[]
                {
                    new ScioDialogKpi("Revit", snap.RevitYear > 0 ? snap.RevitYear.ToString() : "—", "Sesión local"),
                    new ScioDialogKpi("Herramientas", snap.RegisteredTools.ToString(), $"{snap.LoadedModules.Count} módulos", highlight: true),
                    new ScioDialogKpi("Permisos", snap.PermissionProfile.ToString(), snap.IsPaused ? "Pausado" : "Activo")
                },
                monoBody: techDetail,
                actions: new[]
                {
                    new ScioDialogAction("Copiar diagnóstico", ScioTheme.Glyph.Copy, () =>
                    {
                        try { Clipboard.SetText(techDetail); } catch { }
                    })
                },
                closeLabel: "Cerrar",
                isError: snap.IsPaused);

            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class TogglePauseCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            bool paused = ScioBridgeUiHooks.TogglePause?.Invoke() ?? false;
            IntPtr owner = commandData?.Application?.MainWindowHandle ?? IntPtr.Zero;
            ScioDialog.ShowMessage(
                owner,
                "Control del puente MCP",
                paused ? "Puente MCP PAUSADO" : "Puente MCP REANUDADO",
                paused
                    ? "Las llamadas remotas serán rechazadas hasta que vuelvas a reanudar el puente desde Opciones avanzadas."
                    : "El puente vuelve a aceptar solicitudes de clientes MCP en esta sesión de Revit.",
                isError: paused);
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class TogglePythonCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var profile = ScioBridgeUiHooks.TogglePythonAccess?.Invoke() ?? PermissionProfile.SafeWrite;
            bool enabled = profile == PermissionProfile.UnsafeCode;
            IntPtr owner = commandData?.Application?.MainWindowHandle ?? IntPtr.Zero;
            ScioDialog.ShowMessage(
                owner,
                "Autorización de Python",
                enabled ? "Python AUTORIZADO (UnsafeCode)" : "Python DESACTIVADO (SafeWrite)",
                enabled
                    ? "scio_execute_python está habilitado para esta sesión de Revit. Puedes revocarlo en cualquier momento desde Opciones avanzadas."
                    : "Solo las herramientas tipadas están habilitadas. La ejecución arbitraria de Python queda bloqueada.",
                isError: false);
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class ToggleTelemetryCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            bool next = !TelemetryPreferences.IsOptedIn();
            TelemetryPreferences.SaveChoice(next, markPromptShown: true);
            IntPtr owner = commandData?.Application?.MainWindowHandle ?? IntPtr.Zero;
            ScioDialog.ShowMessage(
                owner,
                "Telemetría anónima de uso",
                next ? "Telemetría anónima ACTIVADA" : "Telemetría anónima DESACTIVADA",
                next
                    ? "Gracias por ayudar a mejorar Sutra. Se enviarán automáticamente métricas agregadas y anónimas de uso (nunca nombres de proyecto, rutas ni contenido del modelo)."
                    : "La telemetría quedó desactivada. No se registrará ni enviará ningún dato de uso.",
                isError: false);
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class AboutCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var snap = ScioBridgeUiHooks.GetStatus?.Invoke() ?? new ScioBridgeStatusSnapshot();
            IntPtr owner = commandData?.Application?.MainWindowHandle ?? IntPtr.Zero;
            AboutDialog.Show(owner, snap);
            return Result.Succeeded;
        }
    }
}
