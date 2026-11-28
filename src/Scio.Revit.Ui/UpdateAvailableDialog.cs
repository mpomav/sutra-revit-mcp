#nullable disable
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Scio.Abstractions;

namespace Scio.Revit.Ui
{
    /// <summary>
    /// Branded update notification modal for Sutra — Revit MCP following ScioTheme (Light/Dark).
    /// Alerts the user when a newer GitHub Release is available and opens the installer download.
    /// </summary>
    public static class UpdateAvailableDialog
    {
        /// <summary>
        /// Runs a non-blocking 24h-cached background check for newer Sutra releases and,
        /// when showModal is true and the release was not dismissed, displays the update modal
        /// on the captured WPF UI dispatcher.
        /// </summary>
        public static void CheckInBackground(IntPtr owner, string currentVersion, bool showModal = true)
        {
            var uiDispatcher = Dispatcher.CurrentDispatcher;
            _ = Task.Run(async () =>
            {
                try
                {
                    var update = await SutraUpdateChecker.CheckForUpdateAsync(currentVersion, forceRefresh: false).ConfigureAwait(false);
                    if (showModal &&
                        update != null &&
                        update.UpdateAvailable &&
                        !SutraUpdateChecker.IsVersionDismissed(update.LatestVersion))
                    {
                        uiDispatcher.BeginInvoke(new Action(() =>
                        {
                            try { Show(owner, update); }
                            catch { /* never fail Revit UI */ }
                        }));
                    }
                }
                catch { /* never fail Revit startup */ }
            });
        }

        public static void Show(IntPtr owner, SutraUpdateInfo info)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.LatestVersion))
                return;

            ScioTheme.Refresh();

            var win = new Window();
            ScioTheme.ApplyWindowChrome(win, owner, "Nueva versión de Sutra disponible", 540, null, resizable: false);
            win.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) win.Close(); };

            var root = new StackPanel { Background = ScioTheme.Ground };

            // Branded header
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
                Text = "SUTRA — REVIT MCP · ACTUALIZACIÓN",
                Foreground = ScioTheme.Accent,
                FontFamily = ScioTheme.Font,
                FontSize = 10.5,
                FontWeight = FontWeights.Bold
            });

            var nameRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            var title = ScioTheme.Text("Nueva versión disponible", 18, ScioTheme.Ink, FontWeights.Bold);
            title.VerticalAlignment = VerticalAlignment.Center;
            nameRow.Children.Add(title);

            var versionBadge = ScioTheme.Badge("v" + info.LatestVersion, ScioTheme.AccentSoft, ScioTheme.AccentText);
            versionBadge.Margin = new Thickness(10, 0, 0, 0);
            versionBadge.VerticalAlignment = VerticalAlignment.Center;
            nameRow.Children.Add(versionBadge);
            titleStack.Children.Add(nameRow);

            var subtitle = ScioTheme.Text($"Versión instalada: v{info.CurrentVersion} → Nueva: v{info.LatestVersion}", 12, ScioTheme.Muted);
            subtitle.Margin = new Thickness(0, 4, 0, 0);
            titleStack.Children.Add(subtitle);

            Grid.SetColumn(titleStack, 1);
            headerGrid.Children.Add(titleStack);
            headerBar.Child = headerGrid;
            root.Children.Add(headerBar);

            // Body
            var content = new StackPanel { Margin = new Thickness(24, 16, 24, 10) };

            var intro = ScioTheme.Text(
                "Hay una nueva versión de Sutra — Revit MCP lista para descargar. " +
                "El instalador actualiza tu versión actual automáticamente conservando tus clientes MCP y configuraciones.",
                12.5,
                ScioTheme.Ink);
            intro.Margin = new Thickness(0, 0, 0, 12);
            content.Children.Add(intro);

            if (!string.IsNullOrWhiteSpace(info.ReleaseNotes))
            {
                var notesStack = new StackPanel();
                notesStack.Children.Add(ScioTheme.Text("Novedades de v" + info.LatestVersion, 12, ScioTheme.Ink, FontWeights.SemiBold));

                var notesText = ScioTheme.Text(info.ReleaseNotes, 11.5, ScioTheme.Muted);
                notesText.Margin = new Thickness(0, 6, 0, 0);

                var scroll = new ScrollViewer
                {
                    Content = notesText,
                    MaxHeight = 150,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                };
                notesStack.Children.Add(scroll);

                var notesCard = ScioTheme.Card(notesStack);
                notesCard.Margin = new Thickness(0, 0, 0, 10);
                content.Children.Add(notesCard);
            }

            var hint = ScioTheme.Text(
                "Recuerda cerrar Autodesk Revit (y cerrar Claude Desktop desde la bandeja del sistema) antes de ejecutar el instalador.",
                11,
                ScioTheme.Muted);
            content.Children.Add(hint);

            root.Children.Add(content);

            // Footer
            var footer = new DockPanel { Margin = new Thickness(24, 8, 24, 18), LastChildFill = false };

            var skipBtn = ScioTheme.Button("Omitir v" + info.LatestVersion, null, ScioTheme.ButtonKind.Secondary, () =>
            {
                SutraUpdateChecker.DismissVersion(info.LatestVersion);
                win.Close();
            });
            DockPanel.SetDock(skipBtn, Dock.Left);
            footer.Children.Add(skipBtn);

            var laterBtn = ScioTheme.Button("Más tarde", null, ScioTheme.ButtonKind.Secondary, () => win.Close());
            DockPanel.SetDock(laterBtn, Dock.Left);
            footer.Children.Add(laterBtn);

            var downloadBtn = ScioTheme.Button("Descargar instalador", ScioTheme.Glyph.Globe, ScioTheme.ButtonKind.Primary, () =>
            {
                try
                {
                    string safeUrl = SutraUpdateChecker.IsTrustedReleaseUrl(info.DownloadUrl)
                        ? info.DownloadUrl
                        : SutraUpdateChecker.DefaultReleasesPageUrl;
                    Process.Start(new ProcessStartInfo(safeUrl) { UseShellExecute = true });
                }
                catch { }
                win.Close();
            });
            downloadBtn.Margin = new Thickness(0);
            DockPanel.SetDock(downloadBtn, Dock.Right);
            footer.Children.Add(downloadBtn);

            root.Children.Add(footer);

            win.Content = root;
            win.ShowDialog();
        }
    }
}
