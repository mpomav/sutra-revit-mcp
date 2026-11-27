using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.AutoCAD.ApplicationServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Cad.Commands;
using Scio.Contracts;
using Scio.Revit.Core;

namespace Scio.Cad.Ui
{
    /// <summary>1. Estado de conexión (espejo de ConnectionStatusWindow en Sutra Revit).</summary>
    internal sealed class CadStatusWindow : Window
    {
        public CadStatusWindow()
        {
            var app = SutraCadApp.Instance;
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            bool c3d = Civil3DInspectCommand.IsCivil3DLoaded();
            string hostLabel = c3d ? "Civil 3D " + (app?.CadYear ?? "") : "AutoCAD " + (app?.CadYear ?? "");

            SutraCadTheme.ApplyWindowChrome(this, "Sutra — Estado de conexión", 560, 460);

            var root = new DockPanel();
            var header = SutraCadTheme.HeaderBar(
                "Sutra — Puente MCP activo",
                "SCIO DC · Multi-host (Revit + AutoCAD + Civil 3D)",
                "CONECTADO · " + hostLabel.ToUpperInvariant(),
                SutraCadTheme.SuccessSoft,
                SutraCadTheme.Success,
                "status32.png");
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(18, 10, 18, 14)
            };

            string diagText =
                "Host: " + hostLabel + "\n" +
                "Pipe: " + (app?.PipeName ?? "stopped") + "\n" +
                "Versión: " + (app?.AddinVersion ?? "1.0.0") + "\n" +
                "Contract Hash: " + Contract.Hash + "\n" +
                "DWG Activo: " + (doc?.Name ?? "(ninguno)") + "\n" +
                "Discovery: " + (app?.DiscoveryFile ?? "(ninguno)");

            var copyBtn = SutraCadTheme.SecondaryButton("Copiar diagnóstico", (s, e) =>
            {
                try { Clipboard.SetText(diagText); } catch { }
            });
            copyBtn.Margin = new Thickness(0, 0, 8, 0);

            var closeBtn = SutraCadTheme.PrimaryButton("Cerrar", (s, e) => Close());
            footer.Children.Add(copyBtn);
            footer.Children.Add(closeBtn);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            var body = new StackPanel { Margin = new Thickness(18, 14, 18, 8) };
            var infoStack = new StackPanel();
            infoStack.Children.Add(SutraCadTheme.KeyValueRow("Host detectado", hostLabel));
            infoStack.Children.Add(SutraCadTheme.KeyValueRow("Civil 3D API", c3d ? "Activa (AeccDbMgd)" : "No activa (modo AutoCAD)"));
            infoStack.Children.Add(SutraCadTheme.KeyValueRow("Named Pipe", app?.PipeName ?? "—", mono: true));
            infoStack.Children.Add(SutraCadTheme.KeyValueRow("Contract Hash", Contract.Hash, mono: true));
            infoStack.Children.Add(SutraCadTheme.KeyValueRow("DWG activo", doc != null ? Path.GetFileName(doc.Name) : "(ninguno)"));
            infoStack.Children.Add(SutraCadTheme.KeyValueRow("Archivo discovery", app?.DiscoveryFile ?? "—", mono: true));
            infoStack.Children.Add(SutraCadTheme.KeyValueRow("Carpeta salidas", Path.Combine(ScioPaths.DataRoot(), "salidas"), mono: true));
            body.Children.Add(SutraCadTheme.Card(infoStack));

            body.Children.Add(new TextBlock
            {
                Text = "Integración con Sutra: el mismo servidor MCP (scio-revit-mcp.exe) enruta automáticamente las herramientas de Revit a Revit y las herramientas scio_cad_* / scio_c3d_* a esta sesión de " + hostLabel + ".",
                Foreground = SutraCadTheme.Muted,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            });

            root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;
        }
    }

    /// <summary>2. Conectar IA (espejo de ConnectClientWindow en Sutra Revit).</summary>
    internal sealed class CadConnectWindow : Window
    {
        public CadConnectWindow()
        {
            SutraCadTheme.ApplyWindowChrome(this, "Sutra — Conectar cliente IA", 620, 470);

            string serverExe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Scio", "MCP", "server", "scio-revit-mcp.exe");
            bool serverInstalled = File.Exists(serverExe);

            string claudeCmd = "claude mcp add --scope user scio-revit -- \"" + serverExe + "\"";
            string cursorJson = "{\n  \"mcpServers\": {\n    \"scio-revit\": {\n      \"command\": \"" + serverExe.Replace("\\", "\\\\") + "\"\n    }\n  }\n}";

            var root = new DockPanel();
            var header = SutraCadTheme.HeaderBar(
                "Conectar IA a Sutra (Revit + AutoCAD + Civil 3D)",
                "Un solo servidor MCP, un solo Code Mode (−98% tokens), todos tus hosts CAD/BIM",
                serverInstalled ? "SERVIDOR INSTALADO" : "EJECUTAR install.ps1",
                serverInstalled ? SutraCadTheme.SuccessSoft : SutraCadTheme.AccentSoft,
                serverInstalled ? SutraCadTheme.Success : SutraCadTheme.AccentHover,
                "connect32.png");
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(18, 10, 18, 14)
            };
            footer.Children.Add(SutraCadTheme.PrimaryButton("Cerrar", (s, e) => Close()));
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            var body = new StackPanel { Margin = new Thickness(18, 14, 18, 8) };

            var card1 = new StackPanel();
            card1.Children.Add(new TextBlock { Text = "1. Claude Code / Codex", FontWeight = FontWeights.SemiBold, Foreground = SutraCadTheme.Ink, Margin = new Thickness(0, 0, 0, 6) });
            var cmdBox = new TextBox
            {
                Text = claudeCmd,
                IsReadOnly = true,
                Background = SutraCadTheme.Ground,
                Foreground = SutraCadTheme.Ink,
                BorderBrush = SutraCadTheme.Line,
                FontFamily = SutraCadTheme.Mono,
                FontSize = 11.5,
                Padding = new Thickness(8),
                TextWrapping = TextWrapping.Wrap
            };
            card1.Children.Add(cmdBox);
            var b1 = SutraCadTheme.SecondaryButton("Copiar comando Claude Code", (s, e) => { try { Clipboard.SetText(claudeCmd); } catch { } });
            b1.Margin = new Thickness(0, 8, 0, 0);
            b1.HorizontalAlignment = HorizontalAlignment.Left;
            card1.Children.Add(b1);
            body.Children.Add(SutraCadTheme.Card(card1));

            var card2 = new StackPanel();
            card2.Children.Add(new TextBlock { Text = "2. Cursor / Windsurf / Antigravity (JSON MCP)", FontWeight = FontWeights.SemiBold, Foreground = SutraCadTheme.Ink, Margin = new Thickness(0, 0, 0, 6) });
            var jsonBox = new TextBox
            {
                Text = cursorJson,
                IsReadOnly = true,
                Background = SutraCadTheme.Ground,
                Foreground = SutraCadTheme.Ink,
                BorderBrush = SutraCadTheme.Line,
                FontFamily = SutraCadTheme.Mono,
                FontSize = 11.5,
                Padding = new Thickness(8)
            };
            card2.Children.Add(jsonBox);
            var b2 = SutraCadTheme.SecondaryButton("Copiar JSON MCP", (s, e) => { try { Clipboard.SetText(cursorJson); } catch { } });
            b2.Margin = new Thickness(0, 8, 0, 0);
            b2.HorizontalAlignment = HorizontalAlignment.Left;
            card2.Children.Add(b2);
            body.Children.Add(SutraCadTheme.Card(card2));

            root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;
        }
    }

    /// <summary>
    /// 3. Limpiar plano CAD (Flatten + Pedit/Join + Ordenar por capas + Resolver bloques grandes + Purge).
    /// Sigue el flujo Sutra: Analizar (Dry-Run) -> Revisar reporte -> Aplicar (Transaction).
    /// </summary>
    internal sealed class CadCleanupWindow : Window
    {
        private readonly CheckBox _chkFlatten;
        private readonly CheckBox _chkPedit;
        private readonly TextBox _txtTol;
        private readonly CheckBox _chkSortLayers;
        private readonly CheckBox _chkByLayer;
        private readonly CheckBox _chkLargeBlocks;
        private readonly TextBox _txtMaxEnts;
        private readonly TextBox _txtMaxExtent;
        private readonly ComboBox _cmbBlockAction;
        private readonly CheckBox _chkPurge;
        private readonly CheckBox _chkDegen;
        private readonly TextBox _txtReport;
        private readonly Button _btnApply;

        public CadCleanupWindow()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            SutraCadTheme.ApplyWindowChrome(this, "Sutra — Limpieza y ordenamiento de plano CAD", 680, 640);

            var root = new DockPanel();
            var header = SutraCadTheme.HeaderBar(
                "Limpiar y ordenar plano DWG",
                "Flatten (Z=0) · Pedit/Join · Ordenar capas · Bloques grandes · Purge — " + (doc != null ? Path.GetFileName(doc.Name) : "sin plano"),
                "TRANSACCIONAL",
                SutraCadTheme.AccentSoft,
                SutraCadTheme.AccentHover,
                "audit32.png");
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(18, 10, 18, 14)
            };
            var btnAudit = SutraCadTheme.SecondaryButton("1. Analizar plano (Dry-Run)", OnAnalyze);
            btnAudit.Margin = new Thickness(0, 0, 8, 0);
            _btnApply = SutraCadTheme.PrimaryButton("2. Aplicar limpieza (Transaction)", OnApply);
            _btnApply.Margin = new Thickness(0, 0, 8, 0);
            var btnClose = SutraCadTheme.SecondaryButton("Cerrar", (s, e) => Close());
            footer.Children.Add(btnAudit);
            footer.Children.Add(_btnApply);
            footer.Children.Add(btnClose);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            var body = new StackPanel { Margin = new Thickness(18, 14, 18, 8) };

            var optStack = new StackPanel();
            optStack.Children.Add(new TextBlock { Text = "Opciones de limpieza", FontWeight = FontWeights.SemiBold, Foreground = SutraCadTheme.Ink, Margin = new Thickness(0, 0, 0, 6) });

            _chkFlatten = SutraCadTheme.StyledCheck("Flatten — Proyectar todas las entidades a Z = 0", true);
            optStack.Children.Add(_chkFlatten);

            var peditRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            _chkPedit = SutraCadTheme.StyledCheck("Pedit / Join — Unir líneas, arcos y polilíneas por capa   Tolerancia:", true);
            _txtTol = SutraCadTheme.StyledInput("0.001", 70);
            _txtTol.Margin = new Thickness(8, 0, 0, 0);
            peditRow.Children.Add(_chkPedit);
            peditRow.Children.Add(_txtTol);
            optStack.Children.Add(peditRow);

            _chkSortLayers = SutraCadTheme.StyledCheck("Ordenar por capas (SUTRA-GEOMETRIA, SUTRA-TEXTOS, SUTRA-COTAS, SUTRA-HATCH, SUTRA-BLOQUES)", true);
            _chkByLayer = SutraCadTheme.StyledCheck("Normalizar Color y Tipo de Línea a ByLayer", true);
            optStack.Children.Add(_chkSortLayers);
            optStack.Children.Add(_chkByLayer);

            _chkLargeBlocks = SutraCadTheme.StyledCheck("Buscar y solucionar bloques grandes / pesados", true);
            optStack.Children.Add(_chkLargeBlocks);

            var blkRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(22, 4, 0, 6) };
            blkRow.Children.Add(new TextBlock { Text = "Máx. entidades:", Foreground = SutraCadTheme.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            _txtMaxEnts = SutraCadTheme.StyledInput("500", 65);
            blkRow.Children.Add(_txtMaxEnts);
            blkRow.Children.Add(new TextBlock { Text = "Máx. tamaño:", Foreground = SutraCadTheme.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0) });
            _txtMaxExtent = SutraCadTheme.StyledInput("10000", 75);
            blkRow.Children.Add(_txtMaxExtent);
            blkRow.Children.Add(new TextBlock { Text = "Acción:", Foreground = SutraCadTheme.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0) });
            _cmbBlockAction = new ComboBox
            {
                Width = 145,
                ItemsSource = new[] { "Explotar (explode)", "Solo reportar" },
                SelectedIndex = 0
            };
            blkRow.Children.Add(_cmbBlockAction);
            optStack.Children.Add(blkRow);

            _chkPurge = SutraCadTheme.StyledCheck("Purgar definiciones de bloques y capas sin uso", true);
            _chkDegen = SutraCadTheme.StyledCheck("Eliminar líneas de longitud 0 y textos vacíos", true);
            optStack.Children.Add(_chkPurge);
            optStack.Children.Add(_chkDegen);

            body.Children.Add(SutraCadTheme.Card(optStack));

            var repStack = new StackPanel();
            repStack.Children.Add(new TextBlock { Text = "Reporte de auditoría / resultado verificado", FontWeight = FontWeights.SemiBold, Foreground = SutraCadTheme.Ink, Margin = new Thickness(0, 0, 0, 6) });
            _txtReport = new TextBox
            {
                Height = 200,
                IsReadOnly = true,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = SutraCadTheme.Ground,
                Foreground = SutraCadTheme.Ink,
                BorderBrush = SutraCadTheme.Line,
                FontFamily = SutraCadTheme.Mono,
                FontSize = 11.5,
                Padding = new Thickness(8),
                Text = "Haz clic en '1. Analizar plano (Dry-Run)' para inspeccionar sin modificar el DWG."
            };
            repStack.Children.Add(_txtReport);
            body.Children.Add(SutraCadTheme.Card(repStack));

            root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;
        }

        private CadCleanupCommand.CleanupOptions BuildOptions(bool dryRun)
        {
            double.TryParse(_txtTol.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double tol);
            int.TryParse(_txtMaxEnts.Text, out int maxEnts);
            double.TryParse(_txtMaxExtent.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double maxExt);

            return new CadCleanupCommand.CleanupOptions
            {
                Flatten = _chkFlatten.IsChecked == true,
                PeditJoin = _chkPedit.IsChecked == true,
                JoinTolerance = tol > 0 ? tol : 0.001,
                SortByLayer = _chkSortLayers.IsChecked == true,
                NormalizeByLayer = _chkByLayer.IsChecked == true,
                FixLargeBlocks = _chkLargeBlocks.IsChecked == true,
                MaxBlockEntities = maxEnts >= 10 ? maxEnts : 500,
                MaxBlockExtent = maxExt >= 1 ? maxExt : 10000.0,
                LargeBlockAction = _cmbBlockAction.SelectedIndex == 0 ? "explode" : "report_only",
                PurgeUnused = _chkPurge.IsChecked == true,
                RemoveDegenerate = _chkDegen.IsChecked == true,
                DryRun = dryRun
            };
        }

        private void OnAnalyze(object sender, RoutedEventArgs e)
        {
            try
            {
                JObject res = CadCleanupCommand.RunFromUi(BuildOptions(dryRun: true));
                _txtReport.Text = "[DRY-RUN — NADA MODIFICADO]\n" + res.ToString(Formatting.Indented);
            }
            catch (Exception ex)
            {
                _txtReport.Text = "ERROR: " + ex.Message;
            }
        }

        private void OnApply(object sender, RoutedEventArgs e)
        {
            try
            {
                JObject res = CadCleanupCommand.RunFromUi(BuildOptions(dryRun: false));
                _txtReport.Text = "[LIMPIEZA APLICADA Y VERIFICADA — Puedes revertir con UNDO en AutoCAD]\n" + res.ToString(Formatting.Indented);
            }
            catch (Exception ex)
            {
                _txtReport.Text = "ERROR: " + ex.Message;
            }
        }
    }

    /// <summary>4. Panel Civil 3D &amp; Exportación de datos (CSV -> SQLite + LandXML 1.2).</summary>
    internal sealed class Civil3DPanelWindow : Window
    {
        private readonly TextBox _txtLog;

        public Civil3DPanelWindow()
        {
            bool c3d = Civil3DInspectCommand.IsCivil3DLoaded();
            SutraCadTheme.ApplyWindowChrome(this, "Sutra — Civil 3D & Exportación de datos", 620, 520);

            var root = new DockPanel();
            var header = SutraCadTheme.HeaderBar(
                "Civil 3D — Inspección y Exportación",
                "Generación de tablas CSV (ingesta SQLite) y LandXML 1.2 sin cuadros modales",
                c3d ? "CIVIL 3D ACTIVO" : "MODO AUTOCAD",
                c3d ? SutraCadTheme.SuccessSoft : SutraCadTheme.AccentSoft,
                c3d ? SutraCadTheme.Success : SutraCadTheme.AccentHover,
                "metrado32.png");
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(18, 10, 18, 14)
            };
            var openOut = SutraCadTheme.SecondaryButton("Abrir carpeta salidas", (s, e) => OpenSalidas());
            openOut.Margin = new Thickness(0, 0, 8, 0);
            footer.Children.Add(openOut);
            footer.Children.Add(SutraCadTheme.PrimaryButton("Cerrar", (s, e) => Close()));
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            var body = new StackPanel { Margin = new Thickness(18, 14, 18, 8) };

            // Summary card
            var sumStack = new StackPanel();
            if (c3d)
            {
                JObject s = Civil3DInspectCommand.BuildSummary();
                sumStack.Children.Add(SutraCadTheme.KeyValueRow("Sistema de coordenadas", (string)s["coordinate_system"] ?? "(sin asignar)"));
                sumStack.Children.Add(SutraCadTheme.KeyValueRow("Superficies TIN", ((int?)s["surfaces_count"] ?? 0).ToString()));
                sumStack.Children.Add(SutraCadTheme.KeyValueRow("Alineamientos", ((int?)s["alignments_count"] ?? 0).ToString()));
                sumStack.Children.Add(SutraCadTheme.KeyValueRow("Puntos COGO", ((int?)s["cogo_points_count"] ?? 0).ToString()));
                sumStack.Children.Add(SutraCadTheme.KeyValueRow("Redes de tuberías", ((int?)s["pipe_networks_count"] ?? 0).ToString()));
                sumStack.Children.Add(SutraCadTheme.KeyValueRow("Corredores", ((int?)s["corridors_count"] ?? 0).ToString()));
            }
            else
            {
                sumStack.Children.Add(new TextBlock
                {
                    Text = "Civil 3D (AeccDbMgd) no está cargado en esta sesión. Abre el plano en Civil 3D para habilitar exportaciones de superficies, alineamientos, redes y LandXML. El Censo DWG funciona en ambos.",
                    Foreground = SutraCadTheme.Muted,
                    TextWrapping = TextWrapping.Wrap
                });
            }
            body.Children.Add(SutraCadTheme.Card(sumStack));

            // Export buttons
            var btnWrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            AddAction(btnWrap, "Censo DWG → CSV", () => CadCensoCommand.Execute(new JObject { ["space"] = "model" }), true);
            AddAction(btnWrap, "COGO → CSV", () => Civil3DExportCommand.Execute(new JObject { ["operation"] = "cogo_csv" }), c3d);
            AddAction(btnWrap, "Superficies → CSV", () => Civil3DExportCommand.Execute(new JObject { ["operation"] = "surfaces_csv" }), c3d);
            AddAction(btnWrap, "Alineamientos (20m) → CSV", () => Civil3DExportCommand.Execute(new JObject { ["operation"] = "alignments_csv", ["station_interval"] = 20 }), c3d);
            AddAction(btnWrap, "Redes Tuberías → CSV", () => Civil3DExportCommand.Execute(new JObject { ["operation"] = "pipe_network_csv" }), c3d);
            AddAction(btnWrap, "Exportar LandXML 1.2", () => Civil3DExportCommand.Execute(new JObject { ["operation"] = "landxml" }), c3d, primary: true);
            body.Children.Add(SutraCadTheme.Card(btnWrap));

            _txtLog = new TextBox
            {
                Height = 140,
                IsReadOnly = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = SutraCadTheme.Ground,
                Foreground = SutraCadTheme.Ink,
                BorderBrush = SutraCadTheme.Line,
                FontFamily = SutraCadTheme.Mono,
                FontSize = 11.5,
                Padding = new Thickness(8),
                Text = "Listo para exportar a " + Path.Combine(ScioPaths.DataRoot(), "salidas")
            };
            body.Children.Add(_txtLog);

            root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;
        }

        private void AddAction(WrapPanel panel, string label, Func<JObject> action, bool enabled, bool primary = false)
        {
            Button btn = primary
                ? SutraCadTheme.PrimaryButton(label, (s, e) => RunExport(label, action))
                : SutraCadTheme.SecondaryButton(label, (s, e) => RunExport(label, action));
            btn.IsEnabled = enabled;
            btn.Margin = new Thickness(0, 0, 8, 8);
            panel.Children.Add(btn);
        }

        private void RunExport(string label, Func<JObject> action)
        {
            try
            {
                JObject res = action();
                _txtLog.Text = "[" + label + " — OK]\n" + res.ToString(Formatting.Indented);
            }
            catch (Exception ex)
            {
                _txtLog.Text = "[" + label + " — ERROR]\n" + ex.Message;
            }
        }

        private static void OpenSalidas()
        {
            try
            {
                string dir = Path.Combine(ScioPaths.DataRoot(), "salidas");
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch { }
        }
    }
}
