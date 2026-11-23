#nullable disable
// -----------------------------------------------------------------------------
// SCIO Revit MCP - Unified Branded Modal Dialogs (replacing TaskDialog & MessageBox).
//
// Pure C# WPF built over ScioTheme so security consents, mode selectors, quick
// ribbon summaries, and alerts match the SCIO visual identity across both
// Light and Dark Revit themes (net48, net8, net10) without XAML.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Scio.Revit.Ui
{
    internal sealed class ScioDialogChoice
    {
        public string Key { get; }
        public string Title { get; }
        public string Subtitle { get; }
        public string Glyph { get; }

        public ScioDialogChoice(string key, string title, string subtitle, string glyph = null)
        {
            Key = key;
            Title = title;
            Subtitle = subtitle;
            Glyph = glyph;
        }
    }

    internal sealed class ScioDialogKpi
    {
        public string Label { get; }
        public string Value { get; }
        public string Subtext { get; }
        public bool Highlight { get; }

        public ScioDialogKpi(string label, string value, string subtext = null, bool highlight = false)
        {
            Label = label;
            Value = value;
            Subtext = subtext;
            Highlight = highlight;
        }
    }

    internal sealed class ScioDialogAction
    {
        public string Label { get; }
        public string Glyph { get; }
        public Action OnClick { get; }
        public bool Primary { get; }
        public bool CloseOnClick { get; }

        public ScioDialogAction(string label, string glyph, Action onClick, bool primary = false, bool closeOnClick = false)
        {
            Label = label;
            Glyph = glyph;
            OnClick = onClick;
            Primary = primary;
            CloseOnClick = closeOnClick;
        }
    }

    internal static class ScioDialog
    {
        internal static bool ShowConfirm(
            IntPtr owner,
            string title,
            string eyebrow,
            string headline,
            string body,
            string primaryLabel,
            string cancelLabel,
            string expandedDetail = null,
            string verificationCheckboxText = null,
            bool danger = false,
            string glyph = null)
        {
            ScioTheme.Refresh();
            bool confirmed = false;

            var win = new Window();
            ScioTheme.ApplyWindowChrome(win, owner, title, 540, null, resizable: false);
            win.PreviewKeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) { confirmed = false; win.Close(); } };

            var root = new StackPanel { Background = ScioTheme.Ground };
            root.Children.Add(ScioTheme.Header(eyebrow, title, headline));

            var content = new StackPanel { Margin = new Thickness(24, 16, 24, 10) };

            if (danger)
            {
                content.Children.Add(ScioTheme.Banner(
                    null,
                    body,
                    glyph ?? ScioTheme.Glyph.Warning,
                    ScioTheme.WarningSoft,
                    ScioTheme.WarningBorder,
                    ScioTheme.Warning));
            }
            else if (!string.IsNullOrWhiteSpace(body))
            {
                var bodyCard = ScioTheme.Card(ScioTheme.Text(body, 12.5, ScioTheme.Ink));
                content.Children.Add(bodyCard);
            }

            if (!string.IsNullOrWhiteSpace(expandedDetail))
            {
                var detailBox = new System.Windows.Controls.TextBox
                {
                    Text = expandedDetail.Trim(),
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = ScioTheme.Mono,
                    FontSize = 11,
                    Background = ScioTheme.SurfaceAlt,
                    Foreground = ScioTheme.Ink,
                    BorderBrush = ScioTheme.Line,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(10, 8, 10, 8),
                    MaxHeight = 140,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                };
                var expander = new Expander
                {
                    Header = ScioTheme.Text("Detalle tÃ©cnico", 11.5, ScioTheme.Muted, FontWeights.SemiBold),
                    Content = detailBox,
                    Margin = new Thickness(0, 6, 0, 8),
                    Foreground = ScioTheme.Ink
                };
                content.Children.Add(expander);
            }

            CheckBox verifyCheck = null;
            if (!string.IsNullOrWhiteSpace(verificationCheckboxText))
            {
                var checkBorder = new Border
                {
                    Background = ScioTheme.Surface,
                    BorderBrush = danger ? ScioTheme.WarningBorder : ScioTheme.Line,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(12, 10, 12, 10),
                    Margin = new Thickness(0, 8, 0, 4)
                };
                verifyCheck = ScioTheme.CheckBox(verificationCheckboxText, false);
                checkBorder.Child = verifyCheck;
                content.Children.Add(checkBorder);
            }

            root.Children.Add(content);

            var footer = new DockPanel { Margin = new Thickness(24, 6, 24, 18), LastChildFill = false };
            var cancelBtn = ScioTheme.Button(cancelLabel ?? "Cancelar", null, ScioTheme.ButtonKind.Secondary, () =>
            {
                confirmed = false;
                win.Close();
            });
            cancelBtn.Margin = new Thickness(0);
            DockPanel.SetDock(cancelBtn, Dock.Left);
            footer.Children.Add(cancelBtn);

            var primaryBtn = ScioTheme.Button(
                primaryLabel ?? "Confirmar",
                glyph ?? ScioTheme.Glyph.Apply,
                danger ? ScioTheme.ButtonKind.Primary : ScioTheme.ButtonKind.Primary,
                () =>
                {
                    if (verifyCheck != null && verifyCheck.IsChecked != true) return;
                    confirmed = true;
                    win.Close();
                });
            primaryBtn.Margin = new Thickness(0);
            if (verifyCheck != null)
            {
                primaryBtn.IsEnabled = false;
                verifyCheck.Checked += (s, e) => primaryBtn.IsEnabled = true;
                verifyCheck.Unchecked += (s, e) => primaryBtn.IsEnabled = false;
            }
            DockPanel.SetDock(primaryBtn, Dock.Right);
            footer.Children.Add(primaryBtn);

            root.Children.Add(footer);
            win.Content = root;
            win.ShowDialog();
            return confirmed;
        }

        internal static string ShowChoices(
            IntPtr owner,
            string title,
            string eyebrow,
            string headline,
            string body,
            IReadOnlyList<ScioDialogChoice> choices,
            string activeKey,
            string cancelLabel,
            string activeBadgeText = "Actual")
        {
            ScioTheme.Refresh();
            string chosen = null;

            var win = new Window();
            ScioTheme.ApplyWindowChrome(win, owner, title, 580, null, resizable: false);
            win.PreviewKeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) { chosen = null; win.Close(); } };

            var root = new StackPanel { Background = ScioTheme.Ground };
            root.Children.Add(ScioTheme.Header(eyebrow, title, headline));

            var content = new StackPanel { Margin = new Thickness(24, 16, 24, 6) };
            if (!string.IsNullOrWhiteSpace(body))
            {
                var intro = ScioTheme.Text(body, 12, ScioTheme.Muted);
                intro.Margin = new Thickness(0, 0, 0, 12);
                content.Children.Add(intro);
            }

            foreach (ScioDialogChoice c in choices ?? Array.Empty<ScioDialogChoice>())
            {
                bool isCurrent = string.Equals(c.Key, activeKey, StringComparison.OrdinalIgnoreCase);

                var rowGrid = new Grid();
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var badge = ScioTheme.IconBadge(
                    c.Glyph ?? ScioTheme.Glyph.Shield,
                    isCurrent ? ScioTheme.Accent : ScioTheme.AccentSoft,
                    isCurrent ? ScioTheme.OnAccent : ScioTheme.AccentText,
                    38);
                badge.Margin = new Thickness(0, 1, 14, 0);
                Grid.SetColumn(badge, 0);
                rowGrid.Children.Add(badge);

                var text = new StackPanel();
                var topLine = new WrapPanel { Orientation = Orientation.Horizontal };
                var titleTb = ScioTheme.Text(c.Title, 13.5, ScioTheme.Ink, FontWeights.SemiBold);
                titleTb.VerticalAlignment = VerticalAlignment.Center;
                topLine.Children.Add(titleTb);
                if (isCurrent)
                {
                    var pill = ScioTheme.Badge(activeBadgeText, ScioTheme.AccentSoft, ScioTheme.AccentText);
                    pill.Margin = new Thickness(8, 0, 0, 0);
                    topLine.Children.Add(pill);
                }
                text.Children.Add(topLine);
                if (!string.IsNullOrWhiteSpace(c.Subtitle))
                {
                    var sub = ScioTheme.Text(c.Subtitle, 11.5, ScioTheme.Muted);
                    sub.Margin = new Thickness(0, 3, 0, 0);
                    text.Children.Add(sub);
                }
                Grid.SetColumn(text, 1);
                rowGrid.Children.Add(text);

                var btn = new Button
                {
                    Content = rowGrid,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 0, 0, 10),
                    Padding = new Thickness(14, 12, 14, 12),
                    Background = ScioTheme.Surface,
                    BorderBrush = isCurrent ? ScioTheme.Accent : ScioTheme.Line,
                    Foreground = ScioTheme.Ink,
                    Template = ScioTheme.CardTemplate()
                };
                string key = c.Key;
                btn.Click += (s, e) => { chosen = key; win.Close(); };
                content.Children.Add(btn);
            }

            root.Children.Add(content);

            var footer = new DockPanel { Margin = new Thickness(24, 4, 24, 18), LastChildFill = false };
            var cancelBtn = ScioTheme.Button(cancelLabel ?? "Cancelar", null, ScioTheme.ButtonKind.Secondary, () =>
            {
                chosen = null;
                win.Close();
            });
            cancelBtn.Margin = new Thickness(0);
            DockPanel.SetDock(cancelBtn, Dock.Right);
            footer.Children.Add(cancelBtn);
            root.Children.Add(footer);

            win.Content = root;
            win.ShowDialog();
            return chosen;
        }

        internal static void ShowResult(
            IntPtr owner,
            string title,
            string eyebrow,
            string headline,
            string summary = null,
            IReadOnlyList<ScioDialogKpi> kpis = null,
            string monoBody = null,
            string warningText = null,
            string footerNote = null,
            IReadOnlyList<ScioDialogAction> actions = null,
            string closeLabel = "Cerrar",
            bool isError = false)
        {
            ScioTheme.Refresh();
            var win = new Window();
            ScioTheme.ApplyWindowChrome(win, owner, title, 620, null, resizable: false);
            win.PreviewKeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) win.Close(); };

            var root = new StackPanel { Background = ScioTheme.Ground };
            root.Children.Add(ScioTheme.Header(eyebrow, title, headline));

            var content = new StackPanel { Margin = new Thickness(24, 16, 24, 8) };

            if (!string.IsNullOrWhiteSpace(summary))
            {
                content.Children.Add(ScioTheme.Banner(
                    null,
                    summary,
                    isError ? ScioTheme.Glyph.Error : ScioTheme.Glyph.CheckCircle,
                    isError ? ScioTheme.DangerSoft : ScioTheme.SuccessSoft,
                    isError ? ScioTheme.DangerBorder : ScioTheme.SuccessBorder,
                    isError ? ScioTheme.Danger : ScioTheme.Success));
            }

            if (kpis != null && kpis.Count > 0)
            {
                var kpiGrid = new Grid { Margin = new Thickness(-4, 10, -4, 4) };
                int cols = Math.Min(3, kpis.Count);
                for (int i = 0; i < cols; i++)
                    kpiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                int rowsCount = (int)Math.Ceiling(kpis.Count / (double)cols);
                for (int r = 0; r < rowsCount; r++)
                    kpiGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                for (int idx = 0; idx < kpis.Count; idx++)
                {
                    var k = kpis[idx];
                    var tile = ScioTheme.KpiTile(
                        k.Label,
                        k.Value,
                        k.Subtext,
                        k.Highlight ? ScioTheme.Accent : ScioTheme.Ink);
                    Grid.SetRow(tile, idx / cols);
                    Grid.SetColumn(tile, idx % cols);
                    kpiGrid.Children.Add(tile);
                }
                content.Children.Add(kpiGrid);
            }

            if (!string.IsNullOrWhiteSpace(warningText))
            {
                var warn = ScioTheme.Banner(
                    null,
                    warningText,
                    ScioTheme.Glyph.Warning,
                    ScioTheme.WarningSoft,
                    ScioTheme.WarningBorder,
                    ScioTheme.Warning);
                warn.Margin = new Thickness(0, 8, 0, 0);
                content.Children.Add(warn);
            }

            if (!string.IsNullOrWhiteSpace(monoBody))
            {
                var box = new System.Windows.Controls.TextBox
                {
                    Text = monoBody.Trim(),
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.NoWrap,
                    FontFamily = ScioTheme.Mono,
                    FontSize = 11.5,
                    Background = ScioTheme.Surface,
                    Foreground = ScioTheme.Ink,
                    BorderBrush = ScioTheme.Line,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 10, 0, 0),
                    MaxHeight = 230,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                };
                content.Children.Add(box);
            }

            if (!string.IsNullOrWhiteSpace(footerNote))
            {
                var note = new TextBlock
                {
                    Text = footerNote,
                    FontFamily = ScioTheme.Mono,
                    FontSize = 11,
                    Foreground = ScioTheme.Muted,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 10, 0, 0)
                };
                content.Children.Add(note);
            }

            root.Children.Add(content);

            var footer = new DockPanel { Margin = new Thickness(24, 8, 24, 18), LastChildFill = true };
            var closeBtn = ScioTheme.Button(closeLabel ?? "Cerrar", null, ScioTheme.ButtonKind.Secondary, () => win.Close());
            closeBtn.Margin = new Thickness(0);
            DockPanel.SetDock(closeBtn, Dock.Right);
            footer.Children.Add(closeBtn);

            if (actions != null && actions.Count > 0)
            {
                var actPanel = new WrapPanel { Orientation = Orientation.Horizontal };
                foreach (var act in actions)
                {
                    var captured = act;
                    var btn = ScioTheme.Button(
                        captured.Label,
                        captured.Glyph,
                        captured.Primary ? ScioTheme.ButtonKind.Primary : ScioTheme.ButtonKind.Secondary,
                        () =>
                        {
                            captured.OnClick?.Invoke();
                            if (captured.CloseOnClick) win.Close();
                        });
                    actPanel.Children.Add(btn);
                }
                footer.Children.Add(actPanel);
            }

            root.Children.Add(footer);
            win.Content = root;
            win.ShowDialog();
        }

        internal static void ShowMessage(Window ownerWindow, string title, string headline, string body, bool isError = false)
        {
            IntPtr handle = IntPtr.Zero;
            if (ownerWindow != null)
            {
                try { handle = new WindowInteropHelper(ownerWindow).Handle; } catch { }
            }
            ShowMessage(handle, title, headline, body, isError);
        }

        internal static void ShowMessage(IntPtr owner, string title, string headline, string body, bool isError = false)
        {
            ScioTheme.Refresh();
            var win = new Window();
            ScioTheme.ApplyWindowChrome(win, owner, title, 480, null, resizable: false);
            win.PreviewKeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) win.Close(); };

            var root = new StackPanel { Background = ScioTheme.Ground };
            root.Children.Add(ScioTheme.Header("SCIO BIM", title, null));

            var content = new StackPanel { Margin = new Thickness(24, 16, 24, 8) };
            content.Children.Add(ScioTheme.Banner(
                headline,
                body,
                isError ? ScioTheme.Glyph.Error : ScioTheme.Glyph.Info,
                isError ? ScioTheme.DangerSoft : ScioTheme.Surface,
                isError ? ScioTheme.DangerBorder : ScioTheme.Line,
                isError ? ScioTheme.Danger : ScioTheme.Accent));
            root.Children.Add(content);

            var footer = new DockPanel { Margin = new Thickness(24, 6, 24, 18), LastChildFill = false };
            var ok = ScioTheme.Button("OK", null, ScioTheme.ButtonKind.Primary, () => win.Close());
            ok.Margin = new Thickness(0);
            DockPanel.SetDock(ok, Dock.Right);
            footer.Children.Add(ok);
            root.Children.Add(footer);

            win.Content = root;
            win.ShowDialog();
        }
    }
}

