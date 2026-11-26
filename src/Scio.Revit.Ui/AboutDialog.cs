#nullable disable
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Scio.Revit.Ui
{
    /// <summary>
    /// Branded "Acerca de Sutra — SCIO Revit MCP" modal dialog following ScioTheme (Light/Dark).
    /// </summary>
    internal static class AboutDialog
    {
        private const string WebsiteUrl = "https://sciodc.com";

        public static void Show(IntPtr owner, ScioBridgeStatusSnapshot snap)
        {
            snap = snap ?? new ScioBridgeStatusSnapshot();
            ScioTheme.Refresh();

            var win = new Window();
            ScioTheme.ApplyWindowChrome(win, owner, "Acerca de Sutra — SCIO Revit MCP", 540, null, resizable: false);
            win.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) win.Close(); };

            var root = new StackPanel { Background = ScioTheme.Ground };

            // Branded top header with SCIO orange badge + version pill
            var headerBar = new Border
            {
                Background = ScioTheme.HeaderBg,
                BorderBrush = ScioTheme.Line,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(24, 20, 24, 18)
            };

            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var emblem = new Image
            {
                Source = RibbonIconRenderer.Render(RibbonIconKind.About, 48),
                Width = 48,
                Height = 48,
                Margin = new Thickness(0, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(emblem, BitmapScalingMode.HighQuality);
            Grid.SetColumn(emblem, 0);
            headerGrid.Children.Add(emblem);

            var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(new TextBlock
            {
                Text = "SCIO DC · GRUPO POVA",
                Foreground = ScioTheme.Accent,
                FontFamily = ScioTheme.Font,
                FontSize = 10.5,
                FontWeight = FontWeights.Bold
            });

            var nameRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            var productTitle = ScioTheme.Text("Sutra — SCIO Revit MCP", 18, ScioTheme.Ink, FontWeights.Bold);
            productTitle.VerticalAlignment = VerticalAlignment.Center;
            nameRow.Children.Add(productTitle);

            var versionBadge = ScioTheme.Badge("v" + snap.Version, ScioTheme.AccentSoft, ScioTheme.AccentText);
            versionBadge.Margin = new Thickness(10, 0, 0, 0);
            versionBadge.VerticalAlignment = VerticalAlignment.Center;
            nameRow.Children.Add(versionBadge);
            titleStack.Children.Add(nameRow);

            var tagline = ScioTheme.Text("Modelamos como quien ya construyó.", 12, ScioTheme.Muted);
            tagline.FontStyle = FontStyles.Italic;
            tagline.Margin = new Thickness(0, 4, 0, 0);
            titleStack.Children.Add(tagline);

            Grid.SetColumn(titleStack, 1);
            headerGrid.Children.Add(titleStack);
            headerBar.Child = headerGrid;
            root.Children.Add(headerBar);

            // Body content
            var content = new StackPanel { Margin = new Thickness(24, 16, 24, 10) };

            var desc = ScioTheme.Text(
                "Puente MCP (Model Context Protocol) que conecta clientes de IA con Autodesk Revit de forma " +
                "verificada, tipada y segura directamente desde tu estación de trabajo.",
                12.5,
                ScioTheme.Ink);
            desc.Margin = new Thickness(0, 0, 0, 14);
            content.Children.Add(desc);

            // Metadata card
            var infoGrid = new Grid();
            infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });
            infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            AddInfoRow(infoGrid, 0, "Desarrollado por", "SCIO DC (Grupo POVA) — Lima, Perú");
            AddInfoRow(infoGrid, 1, "Licencia", "Apache-2.0 (Open Source)");
            AddInfoRow(infoGrid, 2, "Entorno Revit", snap.RevitYear > 0 ? $"Autodesk Revit {snap.RevitYear}" : "Autodesk Revit");
            AddInfoRow(infoGrid, 3, "Estado del puente", snap.IsPaused ? "PAUSADO" : $"ACTIVO · {snap.RegisteredTools} herramientas");
            AddInfoRow(infoGrid, 4, "Perfil de permisos", snap.PermissionProfile.ToString());
            AddInfoRow(infoGrid, 5, "Contract Hash", string.IsNullOrWhiteSpace(snap.ContractHash) ? "—" : snap.ContractHash, mono: true);

            content.Children.Add(ScioTheme.Card(infoGrid));

            // Loaded modules pills
            if (snap.LoadedModules != null && snap.LoadedModules.Count > 0)
            {
                var modHeader = ScioTheme.Text($"Módulos cargados ({snap.LoadedModules.Count})", 11, ScioTheme.Muted, FontWeights.SemiBold);
                modHeader.Margin = new Thickness(0, 12, 0, 6);
                content.Children.Add(modHeader);

                var chips = new WrapPanel { Orientation = Orientation.Horizontal };
                foreach (string mod in snap.LoadedModules)
                {
                    var chip = ScioTheme.Badge(mod, ScioTheme.SurfaceAlt, ScioTheme.Ink);
                    chip.Margin = new Thickness(0, 0, 6, 6);
                    chips.Children.Add(chip);
                }
                content.Children.Add(chips);
            }

            root.Children.Add(content);

            // Footer
            var footer = new DockPanel { Margin = new Thickness(24, 8, 24, 18), LastChildFill = false };

            var webBtn = ScioTheme.Button("sciodc.com", ScioTheme.Glyph.Globe, ScioTheme.ButtonKind.Secondary, () =>
            {
                try { Process.Start(new ProcessStartInfo(WebsiteUrl) { UseShellExecute = true }); }
                catch { }
            });
            DockPanel.SetDock(webBtn, Dock.Left);
            footer.Children.Add(webBtn);

            Button copyBtn = null;
            copyBtn = ScioTheme.Button("Copiar info", ScioTheme.Glyph.Copy, ScioTheme.ButtonKind.Secondary, () =>
            {
                try
                {
                    string summary =
                        $"Sutra — SCIO Revit MCP v{snap.Version}\n" +
                        $"Revit: {snap.RevitYear}\n" +
                        $"Estado: {(snap.IsPaused ? "PAUSADO" : "ACTIVO")}\n" +
                        $"Perfil: {snap.PermissionProfile}\n" +
                        $"Herramientas: {snap.RegisteredTools}\n" +
                        $"Contract Hash: {snap.ContractHash}\n" +
                        $"Pipe: {snap.PipeName}";
                    Clipboard.SetText(summary);
                }
                catch { }
            });
            DockPanel.SetDock(copyBtn, Dock.Left);
            footer.Children.Add(copyBtn);

            var closeBtn = ScioTheme.Button("Cerrar", null, ScioTheme.ButtonKind.Primary, () => win.Close());
            closeBtn.Margin = new Thickness(0);
            DockPanel.SetDock(closeBtn, Dock.Right);
            footer.Children.Add(closeBtn);

            root.Children.Add(footer);

            win.Content = root;
            win.ShowDialog();
        }

        private static void AddInfoRow(Grid grid, int row, string label, string value, bool mono = false)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var lbl = new TextBlock
            {
                Text = label,
                Foreground = ScioTheme.Muted,
                FontFamily = ScioTheme.Font,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, row == 0 ? 0 : 6, 12, 0)
            };
            Grid.SetRow(lbl, row);
            Grid.SetColumn(lbl, 0);
            grid.Children.Add(lbl);

            var val = new TextBlock
            {
                Text = value ?? "—",
                Foreground = ScioTheme.Ink,
                FontFamily = mono ? ScioTheme.Mono : ScioTheme.Font,
                FontSize = mono ? 11 : 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, row == 0 ? 0 : 6, 0, 0)
            };
            Grid.SetRow(val, row);
            Grid.SetColumn(val, 1);
            grid.Children.Add(val);
        }
    }
}
