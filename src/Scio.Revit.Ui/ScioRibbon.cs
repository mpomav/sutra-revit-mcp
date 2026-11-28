using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        public static Func<PermissionProfile, PermissionProfile>? SetPermissionProfile { get; set; }
    }

    public static class ScioRibbon
    {
        public const string TabName = "Sutra";
        private static PushButton? _statusButton;

        public static void Build(UIControlledApplication app)
        {
            if (app == null) return;

            try { app.CreateRibbonTab(TabName); }
            catch { /* tab already exists */ }

            RibbonPanel panel = app.CreateRibbonPanel(TabName, "Sutra MCP");
            string asmPath = Assembly.GetExecutingAssembly().Location;

            var statusData = new PushButtonData(
                "ScioConnectionStatus",
                "Estado de\nconexión",
                asmPath,
                typeof(ConnectionStatusCommand).FullName)
            {
                ToolTip = "Ver estado del puente Sutra MCP, módulos cargados y hash de contrato.",
                LongDescription = "Muestra el estado en vivo del puente MCP, la cantidad de herramientas registradas, el perfil de permisos activo y permite copiar el diagnóstico."
            };
            SetVectorIcon(statusData, RibbonIconKind.Status);
            _statusButton = panel.AddItem(statusData) as PushButton;

            var advData = new PulldownButtonData("ScioAdvancedOptions", "Opciones\navanzadas")
            {
                ToolTip = "Controles locales del propietario: perfil de permisos, pausar MCP, Python/C# y telemetría.",
                LongDescription = "Permite cambiar el perfil de permisos (ReadOnly / SafeWrite / UnsafeCode), pausar o reanudar MCP, autorizar ejecución dinámica y abrir la carpeta de salidas."
            };
            SetVectorIcon(advData, RibbonIconKind.Settings);
            var pulldown = panel.AddItem(advData) as PulldownButton;
            if (pulldown != null)
            {
                var profileBtn = new PushButtonData(
                    "ScioSelectProfile",
                    "Perfil de permisos...",
                    asmPath,
                    typeof(SelectProfileCommand).FullName)
                {
                    ToolTip = "Elige el perfil de permisos para esta sesión: ReadOnly, SafeWrite o UnsafeCode."
                };
                SetVectorIcon(profileBtn, RibbonIconKind.Profile);
                pulldown.AddPushButton(profileBtn);

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
                    "Python / C# dinámico (ON / OFF)",
                    asmPath,
                    typeof(TogglePythonCommand).FullName)
                {
                    ToolTip = "Autoriza o revoca scio_execute_python y scio_execute_csharp (SafeWrite <-> UnsafeCode) con confirmación."
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

                var outBtn = new PushButtonData(
                    "ScioOpenOutputs",
                    "Carpeta de salidas",
                    asmPath,
                    typeof(OpenOutputsCommand).FullName)
                {
                    ToolTip = "Abre %USERPROFILE%\\.scio\\v4\\salidas donde se guardan CSVs y reportes HTML."
                };
                SetVectorIcon(outBtn, RibbonIconKind.Folder);
                pulldown.AddPushButton(outBtn);
            }

            panel.AddSeparator();

            var aboutData = new PushButtonData(
                "ScioAboutSutra",
                "Acerca de\nSutra",
                asmPath,
                typeof(AboutCommand).FullName)
            {
                ToolTip = "Información de Sutra — Revit MCP, versión, licencia y módulos activos.",
                LongDescription = "Abre la ventana Acerca de con la versión instalada, información de SCIO DC, estado del puente y enlace a sciodc.com."
            };
            SetVectorIcon(aboutData, RibbonIconKind.About);
            panel.AddItem(aboutData);
        }

        internal static void RefreshStatusIndicator()
        {
            if (_statusButton == null) return;
            try
            {
                var snap = ScioBridgeUiHooks.GetStatus?.Invoke();
                if (snap == null) return;
                RibbonIconKind kind = snap.IsPaused
                    ? RibbonIconKind.StatusPaused
                    : (snap.PermissionProfile == PermissionProfile.UnsafeCode ? RibbonIconKind.StatusUnsafe : RibbonIconKind.Status);
                _statusButton.LargeImage = RibbonIconRenderer.Render(kind, 32);
                _statusButton.Image = RibbonIconRenderer.Render(kind, 16);
                _statusButton.ItemText = snap.IsPaused
                    ? "MCP\nPAUSADO"
                    : (snap.PermissionProfile == PermissionProfile.UnsafeCode ? "MCP\nUnsafeCode" : "Estado de\nconexión");
            }
            catch { }
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
            ScioRibbon.RefreshStatusIndicator();
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
    public sealed class SelectProfileCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var snap = ScioBridgeUiHooks.GetStatus?.Invoke() ?? new ScioBridgeStatusSnapshot();
            IntPtr owner = commandData?.Application?.MainWindowHandle ?? IntPtr.Zero;

            var choices = new[]
            {
                new ScioDialogChoice(
                    "ReadOnly",
                    "Solo lectura (ReadOnly)",
                    "Solo permite inspección, consultas, auditorías y extracción de datos. Bloquea toda escritura en el modelo.",
                    ScioTheme.Glyph.Lock),
                new ScioDialogChoice(
                    "SafeWrite",
                    "Escritura segura (SafeWrite — recomendado)",
                    "Permite herramientas tipadas de Sutra con dry_run y verificación post-commit. Bloquea ejecución de Python/C# arbitrario.",
                    ScioTheme.Glyph.Shield),
                new ScioDialogChoice(
                    "UnsafeCode",
                    "Ejecución dinámica (UnsafeCode — avanzado)",
                    "Habilita además scio_execute_python y scio_execute_csharp en esta sesión de Revit. Requiere confirmación explícita.",
                    ScioTheme.Glyph.Code)
            };

            string? picked = ScioDialog.ShowChoices(
                owner,
                "Perfil de permisos",
                "SUTRA · SEGURIDAD LOCAL",
                "Elige el nivel de permisos para esta sesión de Revit",
                "El cambio aplica de inmediato en la sesión actual sin reiniciar Revit.",
                choices,
                snap.PermissionProfile.ToString(),
                "Cancelar");

            if (string.IsNullOrWhiteSpace(picked)) return Result.Succeeded;

            PermissionProfile target = picked switch
            {
                "ReadOnly" => PermissionProfile.ReadOnly,
                "UnsafeCode" => PermissionProfile.UnsafeCode,
                _ => PermissionProfile.SafeWrite
            };

            if (target == PermissionProfile.UnsafeCode && snap.PermissionProfile != PermissionProfile.UnsafeCode)
            {
                if (!ConfirmUnsafeCode(owner)) return Result.Succeeded;
            }

            if (ScioBridgeUiHooks.SetPermissionProfile != null)
            {
                ScioBridgeUiHooks.SetPermissionProfile(target);
            }
            else if (target == PermissionProfile.UnsafeCode != (snap.PermissionProfile == PermissionProfile.UnsafeCode))
            {
                ScioBridgeUiHooks.TogglePythonAccess?.Invoke();
            }

            ScioRibbon.RefreshStatusIndicator();
            return Result.Succeeded;
        }

        internal static bool ConfirmUnsafeCode(IntPtr owner)
        {
            return ScioDialog.ShowConfirm(
                owner: owner,
                title: "Autorizar ejecución dinámica",
                eyebrow: "SUTRA · SEGURIDAD LOCAL",
                headline: "Estás a punto de habilitar UnsafeCode en esta sesión de Revit",
                body: "El perfil UnsafeCode autoriza tanto scio_execute_python (IronPython) como scio_execute_csharp (Roslyn C#) con acceso completo al API de Autodesk Revit y al usuario local de Windows. Actívalo solo si confías en el cliente IA conectado.",
                primaryLabel: "Habilitar UnsafeCode",
                cancelLabel: "Mantener SafeWrite",
                verificationCheckboxText: "Entiendo que se habilitarán Python y C# dinámicos en esta sesión de Revit.",
                danger: true,
                glyph: ScioTheme.Glyph.Warning);
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class TogglePythonCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var snap = ScioBridgeUiHooks.GetStatus?.Invoke() ?? new ScioBridgeStatusSnapshot();
            IntPtr owner = commandData?.Application?.MainWindowHandle ?? IntPtr.Zero;

            if (snap.PermissionProfile != PermissionProfile.UnsafeCode)
            {
                if (!SelectProfileCommand.ConfirmUnsafeCode(owner))
                    return Result.Succeeded;
            }

            var profile = ScioBridgeUiHooks.TogglePythonAccess?.Invoke() ?? PermissionProfile.SafeWrite;
            ScioRibbon.RefreshStatusIndicator();
            bool enabled = profile == PermissionProfile.UnsafeCode;
            ScioDialog.ShowMessage(
                owner,
                "Autorización de ejecución dinámica",
                enabled ? "Python y C# AUTORIZADOS (UnsafeCode)" : "Ejecución dinámica DESACTIVADA (SafeWrite)",
                enabled
                    ? "scio_execute_python y scio_execute_csharp están habilitados para esta sesión de Revit. Puedes revocarlos en cualquier momento desde Opciones avanzadas."
                    : "Solo las herramientas tipadas están habilitadas. La ejecución arbitraria de Python y C# queda bloqueada.",
                isError: false);
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class OpenOutputsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                string dir = Path.Combine(ScioRuntimePaths.DataRoot, "salidas");
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch { }
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
