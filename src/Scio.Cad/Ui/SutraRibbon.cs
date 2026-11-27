using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Cad.Commands;
using Scio.Cad.Core;
using Scio.Revit.Core;

namespace Scio.Cad.Ui
{
    public sealed class SutraRibbonCommands
    {
        [CommandMethod("SUTRASTATUS")]
        public void ShowStatus()
        {
            try { Application.ShowModalWindow(new CadStatusWindow()); }
            catch (System.Exception ex) { CadLog.Error("SUTRASTATUS failed", ex); }
        }

        [CommandMethod("SUTRACONNECT")]
        public void ShowConnect()
        {
            try { Application.ShowModalWindow(new CadConnectWindow()); }
            catch (System.Exception ex) { CadLog.Error("SUTRACONNECT failed", ex); }
        }

        [CommandMethod("SUTRACLEANUP")]
        public void ShowCleanup()
        {
            try { Application.ShowModalWindow(new CadCleanupWindow()); }
            catch (System.Exception ex) { CadLog.Error("SUTRACLEANUP failed", ex); }
        }

        [CommandMethod("SUTRACRITERIA")]
        public void ShowCriteria()
        {
            try { Application.ShowModalWindow(new CadCriteriaWindow()); }
            catch (System.Exception ex) { CadLog.Error("SUTRACRITERIA failed", ex); }
        }

        [CommandMethod("SUTRAC3D")]
        public void ShowCivil3DPanel()
        {
            try { Application.ShowModalWindow(new Civil3DPanelWindow()); }
            catch (System.Exception ex) { CadLog.Error("SUTRAC3D failed", ex); }
        }

        [CommandMethod("SUTRACENSO")]
        public void RunCenso()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc?.Editor;
            try
            {
                JObject res = CadCensoCommand.Execute(new JObject { ["space"] = "model" });
                string msg = "\n[Sutra Censo DWG] Total entidades: " + res["total_entities"] +
                             " | Grupos: " + res["total_groups"] +
                             "\nCSV: " + res["csv_path"] + "\n";
                if (ed != null) ed.WriteMessage(msg);
                else Application.ShowAlertDialog(msg);
            }
            catch (System.Exception ex)
            {
                if (ed != null) ed.WriteMessage("\n[Sutra Censo ERROR] " + ex.Message + "\n");
            }
        }

        [CommandMethod("SUTRAOUT")]
        public void OpenOutputs()
        {
            try
            {
                string dir = Path.Combine(ScioPaths.DataRoot(), "salidas");
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch (System.Exception ex)
            {
                CadLog.Warn("Failed to open salidas folder: " + ex.Message);
            }
        }
    }

    internal static class SutraRibbon
    {
        private const string TabId = "SUTRA_CAD_TAB";

        public static void Attach()
        {
            try
            {
                if (ComponentManager.Ribbon != null) BuildTab(ComponentManager.Ribbon);
                else ComponentManager.ItemInitialized += OnComponentInitialized;
            }
            catch (System.Exception ex)
            {
                CadLog.Warn("Ribbon attach skipped: " + ex.Message);
            }
        }

        private static void OnComponentInitialized(object sender, RibbonItemEventArgs e)
        {
            if (ComponentManager.Ribbon == null) return;
            ComponentManager.ItemInitialized -= OnComponentInitialized;
            try { BuildTab(ComponentManager.Ribbon); }
            catch (System.Exception ex) { CadLog.Warn("Ribbon deferred build failed: " + ex.Message); }
        }

        private static void BuildTab(RibbonControl ribbon)
        {
            if (ribbon.FindTab(TabId) != null) return;

            var tab = new RibbonTab
            {
                Title = "Sutra",
                Id = TabId
            };

            // Panel 1: Sutra MCP (Conexión e integración IA)
            var mcpSource = new RibbonPanelSource { Title = "Sutra MCP" };
            mcpSource.Items.Add(MakeLargeButton(
                "Estado de\nconexión",
                "status16.png", "status32.png",
                "Muestra el estado del puente Sutra (AutoCAD/Civil 3D), pipe activo, Contract Hash y DWG actual.",
                "SUTRASTATUS "));
            mcpSource.Items.Add(MakeLargeButton(
                "Conectar\nIA",
                "connect16.png", "connect32.png",
                "Configura Claude Code, Claude Desktop, Codex o Cursor con el servidor unificado de Sutra.",
                "SUTRACONNECT "));
            mcpSource.Items.Add(MakeLargeButton(
                "Criterios\nde diseño",
                "audit16.png", "audit32.png",
                "Carga y activa paquetes JSON de normas y criterios de diseño (sutra.design_criteria.v1) desde Gemini Notebook o libros técnicos.",
                "SUTRACRITERIA "));
            tab.Panels.Add(new RibbonPanel { Source = mcpSource });

            // Panel 2: Limpieza y Auditoría CAD
            var cadSource = new RibbonPanelSource { Title = "Limpieza y Censo CAD" };
            cadSource.Items.Add(MakeLargeButton(
                "Limpiar\nplano",
                "audit16.png", "audit32.png",
                "Limpia y ordena el DWG: Flatten (Z=0), Pedit/Join por capa, ordenar capas, resolver bloques grandes y Purge (con Dry-Run previo).",
                "SUTRACLEANUP "));
            cadSource.Items.Add(MakeLargeButton(
                "Censo DWG\n→ SQLite",
                "censo16.png", "censo32.png",
                "Escanea todas las entidades del plano y genera un CSV completo en salidas para consulta SQL.",
                "SUTRACENSO "));
            tab.Panels.Add(new RibbonPanel { Source = cadSource });

            // Panel 3: Civil 3D & Exportación
            var c3dSource = new RibbonPanelSource { Title = "Civil 3D y Datos" };
            c3dSource.Items.Add(MakeLargeButton(
                "Panel\nCivil 3D",
                "metrado16.png", "metrado32.png",
                "Inspecciona superficies TIN, alineamientos, puntos COGO y redes de tuberías; exporta CSV y LandXML 1.2.",
                "SUTRAC3D "));
            c3dSource.Items.Add(MakeLargeButton(
                "Carpeta\nsalidas",
                "salidas16.png", "salidas32.png",
                "Abre %USERPROFILE%\\.scio\\salidas donde se guardan CSVs, LandXML y reportes.",
                "SUTRAOUT "));
            tab.Panels.Add(new RibbonPanel { Source = c3dSource });

            ribbon.Tabs.Add(tab);
        }

        private static RibbonButton MakeLargeButton(string text, string icon16, string icon32, string tooltip, string command)
        {
            return new RibbonButton
            {
                Text = text,
                ShowText = true,
                ShowImage = true,
                Size = RibbonItemSize.Large,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                Image = SutraCadTheme.LoadIcon(icon16),
                LargeImage = SutraCadTheme.LoadIcon(icon32),
                ToolTip = new RibbonToolTip
                {
                    Title = text.Replace("\n", " "),
                    Content = tooltip
                },
                CommandHandler = new AcadCommandHandler(command)
            };
        }

        private sealed class AcadCommandHandler : ICommand
        {
            private readonly string _cmd;
            public AcadCommandHandler(string cmd) { _cmd = cmd; }
            public event EventHandler CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object parameter) => true;
            public void Execute(object parameter)
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                doc?.SendStringToExecute(_cmd, true, false, false);
            }
        }
    }
}
