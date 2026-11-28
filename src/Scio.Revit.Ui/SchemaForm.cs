#nullable disable
// -----------------------------------------------------------------------------
// SCIO Revit MCP - a form drawn from a tool's input schema.
//
// The contract already says, for every tool, which arguments exist, which are
// required, their types, enums and defaults. Drawing the form from that schema
// means a button in the pane takes exactly what an MCP client would send - no
// second, hand-written description of each tool to drift out of step.
//
// What it does NOT draw: the rehearsal plumbing (dry_run, confirmation_token,
// idempotency_key), which the pane drives itself through Rehearse â†’ Apply, and
// paging cursors. Nested objects get a JSON box: a profile or a requirement set
// is a document, and a generated sub-form for it would be worse than the JSON.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Scio.Revit.Ui
{
    internal sealed class SchemaForm
    {
        private static readonly HashSet<string> Plumbing = new HashSet<string>(StringComparer.Ordinal)
        { "dry_run", "confirmation_token", "idempotency_key", "_meta", "cursor", "transaction_name" };

        private const int VisibleOptional = 4;

        private readonly JObject _schema;
        private readonly bool _es;
        private readonly Func<List<long>> _selection;
        private readonly List<Field> _fields = new List<Field>();
        private readonly WpfTextBox _raw;
        private readonly CheckBox _rawToggle;
        private readonly StackPanel _fieldsPanel = new StackPanel();

        public FrameworkElement View { get; }

        private sealed class Field
        {
            public string Name;
            public string Kind;    // bool, enum, string, number, integer, ids, strings, json
            public bool Required;
            public FrameworkElement Control;
            public JToken Default;
        }

        public SchemaForm(JObject inputSchema, JObject preset, bool spanish, Func<List<long>> currentSelection)
        {
            _schema = inputSchema ?? new JObject();
            _es = spanish;
            _selection = currentSelection;

            var props = _schema["properties"] as JObject ?? new JObject();
            var required = new HashSet<string>((_schema["required"] as JArray)?.Select(t => t.ToString()) ?? Enumerable.Empty<string>(),
                                               StringComparer.Ordinal);
            preset = preset ?? new JObject();

            var ordered = props.Properties()
                .Where(p => !Plumbing.Contains(p.Name))
                .OrderByDescending(p => required.Contains(p.Name))
                .ThenByDescending(p => preset[p.Name] != null)
                .ToList();

            var optionalHost = new StackPanel();
            int optionalShown = 0;
            foreach (JProperty p in ordered)
            {
                var f = Build(p.Name, p.Value as JObject ?? new JObject(), required.Contains(p.Name), preset[p.Name]);
                _fields.Add(f);
                FrameworkElement row = Row(f, (p.Value as JObject)?.Value<string>("description"));
                if (f.Required || preset[p.Name] != null || optionalShown < VisibleOptional)
                {
                    if (!f.Required && preset[p.Name] == null) optionalShown++;
                    _fieldsPanel.Children.Add(row);
                }
                else optionalHost.Children.Add(row);
            }
            if (optionalHost.Children.Count > 0)
            {
                _fieldsPanel.Children.Add(new Expander
                {
                    Header = ScioTheme.Text((_es ? "Más opciones (" : "More options (") + optionalHost.Children.Count + ")", 11, ScioTheme.Muted),
                    Content = optionalHost,
                    Margin = new Thickness(0, 2, 0, 4),
                    Foreground = ScioTheme.Muted
                });
            }
            if (_fields.Count == 0)
                _fieldsPanel.Children.Add(ScioTheme.Text(_es ? "Esta herramienta no necesita argumentos." : "This tool takes no arguments.", 11, ScioTheme.Muted));

            _raw = ScioTheme.Input();
            _raw.AcceptsReturn = true;
            _raw.TextWrapping = TextWrapping.Wrap;
            _raw.MinHeight = 90;
            _raw.MaxHeight = 260;
            _raw.FontFamily = ScioTheme.Mono;
            _raw.FontSize = 11;
            _raw.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _raw.Visibility = Visibility.Collapsed;

            _rawToggle = ScioTheme.CheckBox(_es ? "Editar como JSON" : "Edit as JSON", false);
            _rawToggle.Margin = new Thickness(0, 2, 0, 4);
            _rawToggle.Checked += (s, e) =>
            {
                var (args, _) = CollectFields();
                _raw.Text = (args ?? new JObject()).ToString();
                _raw.Visibility = Visibility.Visible;
                _fieldsPanel.Visibility = Visibility.Collapsed;
            };
            _rawToggle.Unchecked += (s, e) =>
            {
                _raw.Visibility = Visibility.Collapsed;
                _fieldsPanel.Visibility = Visibility.Visible;
            };

            var root = new StackPanel();
            root.Children.Add(_fieldsPanel);
            root.Children.Add(_raw);
            if (_fields.Count > 0) root.Children.Add(_rawToggle);
            View = root;
        }

        /// <summary>The arguments as the person left them, or an error naming the field.</summary>
        public (JObject args, string error) Collect()
        {
            if (_rawToggle.IsChecked == true)
            {
                try { return (JObject.Parse(string.IsNullOrWhiteSpace(_raw.Text) ? "{}" : _raw.Text), null); }
                catch (Exception ex) { return (null, (_es ? "JSON inválido: " : "Invalid JSON: ") + ex.Message); }
            }
            return CollectFields();
        }

        private (JObject args, string error) CollectFields()
        {
            var o = new JObject();
            foreach (Field f in _fields)
            {
                JToken value;
                string err = Read(f, out value);
                if (err != null) return (null, Label(f.Name) + ": " + err);
                if (value == null)
                {
                    if (f.Required) return (null, Label(f.Name) + (_es ? ": es obligatorio." : ": is required."));
                    continue;
                }
                o[f.Name] = value;
            }
            return (o, null);
        }

        // ------------------------------------------------------------------

        private Field Build(string name, JObject prop, bool required, JToken preset)
        {
            var f = new Field { Name = name, Required = required, Default = prop["default"] };
            string type = prop.Value<string>("type");
            JArray en = prop["enum"] as JArray;
            bool idsLike = name == "element_ids" || name.EndsWith("_ids", StringComparison.Ordinal);

            if (type == "boolean")
            {
                f.Kind = "bool";
                bool start = preset?.Type == JTokenType.Boolean ? preset.Value<bool>() : (f.Default?.Type == JTokenType.Boolean && f.Default.Value<bool>());
                f.Control = new CheckBox { IsChecked = start, Foreground = ScioTheme.Ink, VerticalAlignment = VerticalAlignment.Center };
            }
            else if (en != null && en.Count > 0)
            {
                f.Kind = "enum";
                var itemStyle = new Style(typeof(ComboBoxItem));
                itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, ScioTheme.Surface));
                itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, ScioTheme.Ink));
                var cb = new ComboBox
                {
                    MinWidth = 140,
                    FontFamily = ScioTheme.Font,
                    FontSize = 11.5,
                    ItemContainerStyle = itemStyle
                };
                cb.Items.Add(_es ? "(predeterminado" + (f.Default != null ? ": " + f.Default : "") + ")"
                                 : "(default" + (f.Default != null ? ": " + f.Default : "") + ")");
                foreach (JToken v in en) cb.Items.Add(v.ToString());
                string want = preset?.ToString();
                cb.SelectedIndex = 0;
                if (want != null) for (int i = 1; i < cb.Items.Count; i++) if ((string)cb.Items[i] == want) cb.SelectedIndex = i;
                f.Control = cb;
            }
            else if (idsLike || (type == "array" && prop["items"]?.Value<string>("type") == "integer") || (prop["oneOf"] != null && idsLike))
            {
                f.Kind = "ids";
                var tb = ScioTheme.Input();
                tb.Text = preset is JArray pa ? string.Join(", ", pa.Select(t => t.ToString())) : (preset?.Type == JTokenType.String ? preset.ToString() : "");
                var host = new DockPanel();
                var useSel = ScioTheme.Button(_es ? "Usar selección" : "Use selection", ScioTheme.Glyph.Select, ScioTheme.ButtonKind.Chip, () =>
                {
                    List<long> sel = _selection?.Invoke() ?? new List<long>();
                    tb.Text = string.Join(", ", sel);
                });
                useSel.Margin = new Thickness(6, 0, 0, 0);
                DockPanel.SetDock(useSel, Dock.Right);
                host.Children.Add(useSel);
                host.Children.Add(tb);
                host.Tag = tb;
                f.Control = host;
            }
            else if (type == "array" && prop["items"]?.Value<string>("type") == "string")
            {
                f.Kind = "strings";
                var tb = ScioTheme.Input(_es ? "Separados por coma" : "Comma-separated");
                tb.Text = preset is JArray pa ? string.Join(", ", pa.Select(t => t.ToString())) : "";
                f.Control = tb;
            }
            else if (type == "integer" || type == "number")
            {
                f.Kind = type;
                var tb = ScioTheme.Input(f.Default != null ? (_es ? "Predeterminado: " : "Default: ") + f.Default : null);
                tb.Text = preset?.ToString() ?? "";
                tb.MaxWidth = 160;
                tb.HorizontalAlignment = HorizontalAlignment.Left;
                f.Control = tb;
            }
            else if (type == "string")
            {
                f.Kind = "string";
                var tb = ScioTheme.Input(f.Default != null ? (_es ? "Predeterminado: " : "Default: ") + f.Default : null);
                tb.Text = preset?.ToString() ?? "";
                if (name == "question" || name == "objective" || name.Contains("description") || name == "proposed_solution")
                {
                    tb.AcceptsReturn = true; tb.TextWrapping = TextWrapping.Wrap; tb.MinHeight = 48;
                }
                f.Control = tb;
            }
            else
            {
                f.Kind = "json";
                var tb = ScioTheme.Input(_es ? "JSON" : "JSON");
                tb.AcceptsReturn = true; tb.TextWrapping = TextWrapping.Wrap; tb.MinHeight = 40;
                tb.FontFamily = ScioTheme.Mono; tb.FontSize = 11;
                tb.Text = preset?.ToString(Newtonsoft.Json.Formatting.None) ?? "";
                f.Control = tb;
            }
            return f;
        }

        private FrameworkElement Row(Field f, string description)
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(ScioTheme.Text(Label(f.Name), 11, ScioTheme.Ink, FontWeights.SemiBold, wrap: false));
            if (f.Required) head.Children.Add(ScioTheme.Text(" *", 11, ScioTheme.Accent, FontWeights.SemiBold, wrap: false));
            if (f.Kind == "bool")
            {
                f.Control.Margin = new Thickness(0, 0, 6, 0);
                head.Children.Insert(0, f.Control);
                row.Children.Add(head);
            }
            else
            {
                head.Margin = new Thickness(0, 0, 0, 2);
                row.Children.Add(head);
                row.Children.Add(f.Control);
            }
            if (!string.IsNullOrWhiteSpace(description))
                row.ToolTip = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
            return row;
        }

        private string Read(Field f, out JToken value)
        {
            value = null;
            switch (f.Kind)
            {
                case "bool":
                {
                    bool v = ((CheckBox)f.Control).IsChecked == true;
                    bool def = f.Default?.Type == JTokenType.Boolean && f.Default.Value<bool>();
                    if (v != def || f.Required) value = v;
                    return null;
                }
                case "enum":
                {
                    var cb = (ComboBox)f.Control;
                    if (cb.SelectedIndex > 0) value = (string)cb.SelectedItem;
                    return null;
                }
                case "ids":
                {
                    var tb = (WpfTextBox)((FrameworkElement)f.Control).Tag;
                    string t = tb.Text?.Trim() ?? "";
                    if (t.Length == 0) return null;
                    if (t.StartsWith("@", StringComparison.Ordinal)) { value = t; return null; }
                    var arr = new JArray();
                    foreach (string part in t.Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
                            return (_es ? "'" + part + "' no es un id entero." : "'" + part + "' is not an integer id.");
                        arr.Add(id);
                    }
                    value = arr;
                    return null;
                }
                case "strings":
                {
                    string t = ((WpfTextBox)f.Control).Text?.Trim() ?? "";
                    if (t.Length == 0) return null;
                    value = new JArray(t.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0));
                    return null;
                }
                case "integer":
                {
                    string t = ((WpfTextBox)f.Control).Text?.Trim() ?? "";
                    if (t.Length == 0) return null;
                    if (!long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n))
                        return _es ? "debe ser un entero." : "must be an integer.";
                    value = n; return null;
                }
                case "number":
                {
                    string t = ((WpfTextBox)f.Control).Text?.Trim().Replace(',', '.') ?? "";
                    if (t.Length == 0) return null;
                    if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                        return _es ? "debe ser un número." : "must be a number.";
                    value = d; return null;
                }
                case "string":
                {
                    string t = ((WpfTextBox)f.Control).Text ?? "";
                    if (t.Trim().Length == 0) return null;
                    value = t; return null;
                }
                default:
                {
                    string t = ((WpfTextBox)f.Control).Text?.Trim() ?? "";
                    if (t.Length == 0) return null;
                    try { value = JToken.Parse(t); return null; }
                    catch (Exception ex) { return (_es ? "JSON inválido: " : "invalid JSON: ") + ex.Message; }
                }
            }
        }

        private static string Label(string name)
        {
            string s = name.Replace('_', ' ');
            return s.Length == 0 ? name : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }
    }
}

