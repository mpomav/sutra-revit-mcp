using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using Scio.Cad.Core;

namespace Scio.Cad.Ui
{
    /// <summary>
    /// WPF modal window launched by the SUTRACRITERIA Ribbon button to import, validate,
    /// activate/deactivate, and manage design-criteria JSON packs (sutra.design_criteria.v1)
    /// exported from Gemini Notebook / File Search.
    /// </summary>
    internal sealed class CadCriteriaWindow : Window
    {
        private readonly StackPanel _packsContainer;
        private readonly TextBlock _statusText;

        public CadCriteriaWindow()
        {
            SutraCadTheme.ApplyWindowChrome(this, "Sutra — Criterios de diseño y normas", 640, 520);

            var root = new DockPanel();
            var header = SutraCadTheme.HeaderBar(
                "Criterios de diseño y normas",
                "Paquetes JSON (sutra.design_criteria.v1) desde Gemini Notebook / libros técnicos",
                "LISTO PARA MCP",
                SutraCadTheme.SuccessSoft,
                SutraCadTheme.Success,
                "audit32.png");
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(18, 10, 18, 14)
            };

            var openFolderBtn = SutraCadTheme.SecondaryButton("Abrir carpeta", (s, e) =>
            {
                try
                {
                    string dir = CadCriteriaStore.CriteriaRoot();
                    Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    SetStatus("No se pudo abrir la carpeta: " + ex.Message, isError: true);
                }
            });
            openFolderBtn.Margin = new Thickness(0, 0, 8, 0);

            var copyPromptBtn = SutraCadTheme.SecondaryButton("Copiar prompt Gemini Notebook", (s, e) =>
            {
                try
                {
                    Clipboard.SetText(CadCriteriaStore.GetGeminiNotebookExportPrompt());
                    SetStatus("Prompt copiado al portapapeles. Pégalo en tu Gemini Notebook para exportar un paquete JSON.", isError: false);
                }
                catch (Exception ex)
                {
                    SetStatus("No se pudo copiar el prompt: " + ex.Message, isError: true);
                }
            });
            copyPromptBtn.Margin = new Thickness(0, 0, 8, 0);

            var loadBtn = SutraCadTheme.PrimaryButton("Cargar JSON...", OnImportClicked);
            loadBtn.Margin = new Thickness(0, 0, 8, 0);

            var closeBtn = SutraCadTheme.SecondaryButton("Cerrar", (s, e) => Close());

            footer.Children.Add(openFolderBtn);
            footer.Children.Add(copyPromptBtn);
            footer.Children.Add(loadBtn);
            footer.Children.Add(closeBtn);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            var body = new StackPanel { Margin = new Thickness(18, 14, 18, 8) };

            var pathCard = new StackPanel();
            pathCard.Children.Add(SutraCadTheme.KeyValueRow("Carpeta compartida", CadCriteriaStore.CriteriaRoot(), mono: true));
            pathCard.Children.Add(SutraCadTheme.KeyValueRow("Esquema esperado", CadCriteriaStore.SchemaVersion, mono: true));
            body.Children.Add(SutraCadTheme.Card(pathCard));

            body.Children.Add(new TextBlock
            {
                Text = "Paquetes cargados (marca los activos para que el agente MCP los consulte automáticamente):",
                FontWeight = FontWeights.SemiBold,
                Foreground = SutraCadTheme.Ink,
                Margin = new Thickness(0, 4, 0, 8)
            });

            _packsContainer = new StackPanel();
            body.Children.Add(_packsContainer);

            _statusText = new TextBlock
            {
                Text = "",
                Foreground = SutraCadTheme.Muted,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0)
            };
            body.Children.Add(_statusText);

            root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;

            RefreshPacksList();
        }

        private void OnImportClicked(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Seleccionar archivo de criterios de diseño (sutra.design_criteria.v1)",
                Filter = "Criterios Sutra (*.json)|*.json|Todos los archivos (*.*)|*.*",
                Multiselect = true
            };

            if (dlg.ShowDialog(this) != true) return;

            int imported = 0;
            foreach (string file in dlg.FileNames)
            {
                try
                {
                    JObject res = CadCriteriaStore.ImportPackFile(file, activate: true);
                    imported++;
                    SetStatus(
                        "Cargado y activado '" + res["name"] + "' (" + res["rules_count"] + " reglas, " + res["assemblies_count"] + " detalles).",
                        isError: false);
                }
                catch (Exception ex)
                {
                    SetStatus("Error al cargar '" + Path.GetFileName(file) + "': " + ex.Message, isError: true);
                }
            }

            if (imported > 0) RefreshPacksList();
        }

        private void RefreshPacksList()
        {
            _packsContainer.Children.Clear();
            JArray packs = CadCriteriaStore.ListPacks();
            if (packs.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text = "Aún no hay paquetes cargados. Usa 'Copiar prompt Gemini Notebook' para generar tu primer archivo .criteria.json y luego pulsa 'Cargar JSON...'.",
                    Foreground = SutraCadTheme.Muted,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                };
                _packsContainer.Children.Add(SutraCadTheme.Card(empty));
                return;
            }

            foreach (JObject p in packs)
            {
                string id = (string)p["pack_id"] ?? "";
                string name = (string)p["name"] ?? id;
                string disc = ((string)p["discipline"] ?? "").ToUpperInvariant();
                int rules = (int?)p["rules_count"] ?? 0;
                int asms = (int?)p["assemblies_count"] ?? 0;
                bool active = (bool?)p["active"] ?? false;

                var row = new DockPanel();
                var cb = new CheckBox
                {
                    IsChecked = active,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                };
                cb.Checked += (s, e) =>
                {
                    CadCriteriaStore.SetPackActive(id, true);
                    SetStatus("Paquete '" + id + "' activado.", isError: false);
                };
                cb.Unchecked += (s, e) =>
                {
                    CadCriteriaStore.SetPackActive(id, false);
                    SetStatus("Paquete '" + id + "' desactivado.", isError: false);
                };
                DockPanel.SetDock(cb, Dock.Left);
                row.Children.Add(cb);

                var info = new StackPanel();
                info.Children.Add(new TextBlock
                {
                    Text = name + "  [" + disc + "]",
                    FontWeight = FontWeights.SemiBold,
                    Foreground = SutraCadTheme.Ink,
                    FontSize = 12.5
                });
                info.Children.Add(new TextBlock
                {
                    Text = "ID: " + id + " · " + rules + " reglas · " + asms + " detalles/ensamblajes",
                    Foreground = SutraCadTheme.Muted,
                    FontSize = 11.5
                });
                row.Children.Add(info);

                _packsContainer.Children.Add(SutraCadTheme.Card(row));
            }
        }

        private void SetStatus(string msg, bool isError)
        {
            _statusText.Text = msg;
            _statusText.Foreground = isError ? SutraCadTheme.AccentHover : SutraCadTheme.Success;
        }
    }
}
