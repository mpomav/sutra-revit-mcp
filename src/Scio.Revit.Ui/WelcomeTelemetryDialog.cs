#nullable disable
using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;

namespace Scio.Revit.Ui
{
    /// <summary>
    /// Reads and writes opt-in anonymous telemetry preferences in %USERPROFILE%\.scio\settings.json
    /// without clobbering any other user settings.
    /// </summary>
    public static class TelemetryPreferences
    {
        private static readonly object FileLock = new object();

        public static string SettingsPath => Path.Combine(ScioRuntimePaths.DataRoot, "settings.json");

        public static bool IsOptedIn()
        {
            try
            {
                string path = SettingsPath;
                if (!File.Exists(path)) return false;
                var obj = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                return obj["telemetry_opt_in"]?.Type == JTokenType.Boolean && (bool)obj["telemetry_opt_in"];
            }
            catch
            {
                return false;
            }
        }

        public static bool HasAnsweredWelcomePrompt()
        {
            try
            {
                string path = SettingsPath;
                if (!File.Exists(path)) return false;
                var obj = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                if (obj["telemetry_prompt_shown"]?.Type == JTokenType.Boolean && (bool)obj["telemetry_prompt_shown"])
                    return true;
                if (obj["telemetry_opt_in"]?.Type == JTokenType.Boolean)
                    return true;
                return false;
            }
            catch
            {
                return false;
            }
        }

        public static bool SaveChoice(bool optIn, bool markPromptShown = true)
        {
            lock (FileLock)
            {
                try
                {
                    string path = SettingsPath;
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    JObject obj = new JObject();
                    if (File.Exists(path))
                    {
                        try { obj = JObject.Parse(File.ReadAllText(path, Encoding.UTF8)); }
                        catch { obj = new JObject(); }
                    }

                    obj["telemetry_opt_in"] = optIn;
                    if (markPromptShown) obj["telemetry_prompt_shown"] = true;
                    obj["telemetry_opt_in_changed_at_utc"] = DateTimeOffset.UtcNow.ToString("O");

                    string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
                    File.WriteAllText(tmp, obj.ToString(Formatting.Indented), new UTF8Encoding(false));
                    if (File.Exists(path))
                    {
                        try { File.Replace(tmp, path, null); }
                        catch
                        {
                            File.Copy(tmp, path, overwrite: true);
                            try { File.Delete(tmp); } catch { }
                        }
                    }
                    else
                    {
                        File.Move(tmp, path);
                    }
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }
    }

    /// <summary>
    /// Branded first-run Welcome window inviting the machine owner to opt into
    /// strictly anonymous usage telemetry (OFF by default unless explicitly accepted).
    /// </summary>
    public static class WelcomeTelemetryDialog
    {
        /// <summary>
        /// Shows the first-run welcome modal once per machine if the owner has not yet
        /// chosen a telemetry preference. Returns true if accepted, false if declined.
        /// </summary>
        public static bool ShowIfFirstRun(IntPtr owner, string version)
        {
            if (TelemetryPreferences.HasAnsweredWelcomePrompt())
                return TelemetryPreferences.IsOptedIn();

            return Show(owner, version);
        }

        public static bool Show(IntPtr owner, string version)
        {
            ScioTheme.Refresh();
            bool accepted = false;
            bool decided = false;

            var win = new Window();
            ScioTheme.ApplyWindowChrome(win, owner, "Bienvenido a Sutra — SCIO Revit MCP", 560, null, resizable: false);
            win.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    accepted = false;
                    decided = true;
                    win.Close();
                }
            };

            var root = new StackPanel { Background = ScioTheme.Ground };

            // Branded header matching AboutDialog
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
                Text = "SCIO DC · PRIMER USO",
                Foreground = ScioTheme.Accent,
                FontFamily = ScioTheme.Font,
                FontSize = 10.5,
                FontWeight = FontWeights.Bold
            });

            var nameRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            var productTitle = ScioTheme.Text("Bienvenido a Sutra", 18, ScioTheme.Ink, FontWeights.Bold);
            productTitle.VerticalAlignment = VerticalAlignment.Center;
            nameRow.Children.Add(productTitle);

            var versionBadge = ScioTheme.Badge("v" + (string.IsNullOrWhiteSpace(version) ? "1.1.0" : version), ScioTheme.AccentSoft, ScioTheme.AccentText);
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

            // Body
            var content = new StackPanel { Margin = new Thickness(24, 16, 24, 10) };

            var intro = ScioTheme.Text(
                "Sutra ya está conectado a esta sesión de Autodesk Revit. Antes de empezar, ¿nos ayudas a mejorar el producto compartiendo telemetría anónima de uso?",
                12.5,
                ScioTheme.Ink);
            intro.Margin = new Thickness(0, 0, 0, 12);
            content.Children.Add(intro);

            // What IS collected card
            var yesStack = new StackPanel();
            yesStack.Children.Add(ScioTheme.Text("Qué SÍ se envía (si aceptas)", 12, ScioTheme.Ink, FontWeights.SemiBold));
            var yesBody = ScioTheme.Text(
                "• Nombre de la herramienta usada, conteo de llamadas, éxito/error y duración promedio.\n" +
                "• Versión de Sutra, modo de esquema (codemode/facade/full) y código de país (2 letras).\n" +
                "• Se envía automáticamente en segundo plano de forma agregada y periódica.",
                11.5,
                ScioTheme.Muted);
            yesBody.Margin = new Thickness(0, 4, 0, 0);
            yesStack.Children.Add(yesBody);
            var yesCard = ScioTheme.Card(yesStack);
            yesCard.Margin = new Thickness(0, 0, 0, 10);
            content.Children.Add(yesCard);

            // Privacy guarantee card
            var noStack = new StackPanel();
            noStack.Children.Add(ScioTheme.Text("Qué NUNCA se recopila", 12, ScioTheme.Ink, FontWeights.SemiBold));
            var noBody = ScioTheme.Text(
                "• Nunca se envían direcciones IP, nombres de proyecto, rutas de archivo, IDs de elementos, parámetros ni contenido de tus modelos Revit.",
                11.5,
                ScioTheme.Muted);
            noBody.Margin = new Thickness(0, 4, 0, 0);
            noStack.Children.Add(noBody);
            var noCard = ScioTheme.Card(noStack);
            noCard.Margin = new Thickness(0, 0, 0, 10);
            content.Children.Add(noCard);

            var footnote = ScioTheme.Text(
                "Es 100% opcional y puedes activarla o desactivarla en cualquier momento desde Sutra → Opciones avanzadas → Telemetría anónima.",
                11,
                ScioTheme.Muted);
            content.Children.Add(footnote);

            root.Children.Add(content);

            // Footer
            var footer = new DockPanel { Margin = new Thickness(24, 8, 24, 18), LastChildFill = false };

            var declineBtn = ScioTheme.Button("No, gracias", null, ScioTheme.ButtonKind.Secondary, () =>
            {
                accepted = false;
                decided = true;
                win.Close();
            });
            declineBtn.Margin = new Thickness(0);
            DockPanel.SetDock(declineBtn, Dock.Left);
            footer.Children.Add(declineBtn);

            var acceptBtn = ScioTheme.Button("Aceptar telemetría anónima", ScioTheme.Glyph.Apply, ScioTheme.ButtonKind.Primary, () =>
            {
                accepted = true;
                decided = true;
                win.Close();
            });
            acceptBtn.Margin = new Thickness(0);
            DockPanel.SetDock(acceptBtn, Dock.Right);
            footer.Children.Add(acceptBtn);

            root.Children.Add(footer);

            win.Content = root;
            win.ShowDialog();

            // Persist choice (closing with X defaults safely to false so we don't nag on every startup)
            TelemetryPreferences.SaveChoice(decided && accepted, markPromptShown: true);
            return decided && accepted;
        }
    }
}
