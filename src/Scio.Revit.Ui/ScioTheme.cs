#nullable disable
// -----------------------------------------------------------------------------
// SCIO Revit MCP - one visual language for every SCIO surface inside Revit.
//
// Unified Design Tokens:
//   - Official SCIO Orange (#E06400) scale + semantic-only status colors
//   - Embedded DM Sans OFL typography (with automatic fallback to Segoe UI)
//   - Native Light & Dark themes following Revit (or user override Settings.UITheme)
//   - Vector glyphs (Segoe Fluent Icons / Segoe MDL2 Assets), zero emojis
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using BridgeSettings = Scio.Revit.Ui.UiHostSettings;

namespace Scio.Revit.Ui
{
    internal static class ScioTheme
    {
        /// <summary>The official SCIO brand orange (#E06400).</summary>
        internal static readonly Color AccentColor = Color.FromRgb(0xE0, 0x64, 0x00);

        internal static bool Dark { get; private set; }

        internal static Brush Accent, AccentHover, AccentDark, AccentMid, AccentLight, AccentSoft, AccentText, OnAccent;
        internal static Brush Ground, Surface, SurfaceAlt, HeaderBg, Line, TableLine, Ink, Muted;
        internal static Brush Success, Warning, Danger, SuccessSoft, WarningSoft, DangerSoft;
        internal static Brush SuccessBorder, WarningBorder, DangerBorder;

        internal static readonly FontFamily Font = ResolveBrandFont();
        internal static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas, Courier New");
        internal static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        internal static event Action ThemeChanged;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        static ScioTheme() { Apply(DetectDark()); }

        /// <summary>Re-read Settings.UITheme / Revit's theme and rebuild all brushes.</summary>
        internal static void Refresh()
        {
            bool prevDark = Dark;
            bool nextDark = DetectDark();
            Apply(nextDark);
            if (prevDark != nextDark)
            {
                try { ThemeChanged?.Invoke(); } catch { }
            }
        }

        internal static void NotifyThemePreferenceChanged()
        {
            Apply(DetectDark());
            try { ThemeChanged?.Invoke(); } catch { }
        }

        private static FontFamily ResolveBrandFont()
        {
            try
            {
                string asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                string fontsDir = Path.Combine(asmDir, "Resources", "Fonts");
                if (Directory.Exists(fontsDir))
                {
                    string folderUri = new Uri(fontsDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar).AbsoluteUri;
                    FontFamily found = Fonts.GetFontFamilies(folderUri).FirstOrDefault();
                    if (found != null)
                        return new FontFamily(found.Source + ", DM Sans, Segoe UI");
                }
            }
            catch { }
            return new FontFamily("DM Sans, Segoe UI");
        }

        private static bool DetectDark()
        {
            try
            {
                string pref = BridgeSettings.UITheme;
                if (pref == "dark") return true;
                if (pref == "light") return false;
            }
            catch { }
#if REVIT2023
            return false;
#else
            try { return Autodesk.Revit.UI.UIThemeManager.CurrentTheme == Autodesk.Revit.UI.UITheme.Dark; }
            catch { return false; }
#endif
        }

        private static void Apply(bool dark)
        {
            Dark = dark;
            Accent = B(AccentColor);
            AccentHover = B(dark ? "#F28C38" : "#C75700");
            AccentDark = B(dark ? "#FBD3B0" : "#A84A00");
            AccentMid = B("#F28C38");
            AccentLight = B(dark ? "#6E3A14" : "#FBD3B0");
            AccentSoft = B(dark ? "#332012" : "#FFF3E8");
            AccentText = B(dark ? "#FBD3B0" : "#A84A00");
            OnAccent = Brushes.White;

            Ground = B(dark ? "#1A1B1E" : "#F7F7F8");
            Surface = B(dark ? "#24262B" : "#FFFFFF");
            SurfaceAlt = B(dark ? "#2E3037" : "#F1F1F3");
            HeaderBg = B(dark ? "#24262B" : "#FFFFFF");
            Line = B(dark ? "#3E4046" : "#DCDCE0");
            TableLine = B(dark ? "#3E4046" : "#BFBFBF");
            Ink = B(dark ? "#ECECEE" : "#262626");
            Muted = B(dark ? "#A1A1AA" : "#666666");

            Success = B(dark ? "#4ADE80" : "#2E7D32");
            Warning = B(dark ? "#FBBF24" : "#E06400");
            Danger = B(dark ? "#F87171" : "#C00000");
            SuccessSoft = B(dark ? "#163222" : "#EDF7EE");
            WarningSoft = B(dark ? "#3A2B12" : "#FFF3E8");
            DangerSoft = B(dark ? "#3B1A1A" : "#FDECEC");
            SuccessBorder = B(dark ? "#276749" : "#A7D7AB");
            WarningBorder = B(dark ? "#7A4A16" : "#FBD3B0");
            DangerBorder = B(dark ? "#7F1D1D" : "#F5B8B8");
        }

        internal static SolidColorBrush B(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        private static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        /// <summary>Applies consistent SCIO window chrome, ownership, DM Sans font, and Windows dark titlebar.</summary>
        internal static void ApplyWindowChrome(
            Window window,
            IntPtr owner,
            string title,
            double width,
            double? height = null,
            bool resizable = false)
        {
            if (window == null) return;
            window.Title = title ?? "SCIO";
            window.Width = width;
            if (height.HasValue)
            {
                window.Height = height.Value;
            }
            else
            {
                window.SizeToContent = SizeToContent.Height;
                try { window.MaxHeight = Math.Max(460, SystemParameters.WorkArea.Height * 0.88); } catch { }
            }
            window.ResizeMode = resizable ? ResizeMode.CanResize : ResizeMode.NoResize;
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = owner == IntPtr.Zero ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
            window.Background = Ground;
            window.Foreground = Ink;
            window.FontFamily = Font;
            if (owner != IntPtr.Zero) new WindowInteropHelper(window).Owner = owner;

            window.SourceInitialized += (s, e) => TrySetDarkTitleBar(window, Dark);
        }

        internal static void TrySetDarkTitleBar(Window window, bool dark)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                int val = dark ? 1 : 0;
                // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (Win11 / Win10 2004+), fallback 19 (older Win10).
                if (DwmSetWindowAttribute(hwnd, 20, ref val, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref val, sizeof(int));
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // Glyphs (Segoe MDL2 / Fluent code points)
        // ------------------------------------------------------------------
        internal static class Glyph
        {
            internal const string Search = "\uE721";
            internal const string Play = "\uE768";
            internal const string Rehearse = "\uE7B3";
            internal const string Apply = "\uE73E";
            internal const string Undo = "\uE7A7";
            internal const string Select = "\uE8B3";
            internal const string Isolate = "\uE890";
            internal const string Open = "\uE8E5";
            internal const string Folder = "\uE8B7";
            internal const string Pin = "\uE718";
            internal const string Unpin = "\uE77A";
            internal const string Send = "\uE724";
            internal const string Copy = "\uE8C8";
            internal const string Clear = "\uE894";
            internal const string Close = "\uE711";
            internal const string Refresh = "\uE72C";
            internal const string Warning = "\uE7BA";
            internal const string Error = "\uE783";
            internal const string Info = "\uE946";
            internal const string Link = "\uE71B";
            internal const string List = "\uE8FD";
            internal const string Ruler = "\uECC6";
            internal const string Shield = "\uEA18";
            internal const string Tag = "\uE8EC";
            internal const string Mail = "\uE715";
            internal const string Sync = "\uE895";
            internal const string Code = "\uE943";
            internal const string Settings = "\uE713";
            internal const string Document = "\uE8A5";
            internal const string Layers = "\uE81E";
            internal const string Sparkle = "\uE945";
            internal const string Plug = "\uE839";
            internal const string Chevron = "\uE76C";
            internal const string Edit = "\uE70F";
            internal const string Globe = "\uE774";
            internal const string Package = "\uE7B8";
            internal const string CheckCircle = "\uE73E";
            internal const string StatusError = "\uEA39";
            internal const string Pause = "\uE769";
            internal const string History = "\uE81C";
            internal const string Lock = "\uE72E";
            internal const string Palette = "\uE790";
            internal const string Terminal = "\uE756";
            internal const string Print = "\uE749";
        }

        // ------------------------------------------------------------------
        // Building blocks
        // ------------------------------------------------------------------

        internal static Border Header(string eyebrow, string title, string subtitle = null)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = (eyebrow ?? "SCIO BIM").ToUpperInvariant(),
                Foreground = Accent,
                FontFamily = Font,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 2)
            });
            stack.Children.Add(new TextBlock
            {
                Text = title ?? string.Empty,
                Foreground = Ink,
                FontFamily = Font,
                FontSize = 19,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap
            });
            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = subtitle,
                    Foreground = Muted,
                    FontFamily = Font,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }
            return new Border
            {
                Background = HeaderBg,
                BorderBrush = Accent,
                BorderThickness = new Thickness(0, 0, 0, 3),
                Padding = new Thickness(24, 18, 24, 16),
                Child = stack
            };
        }

        internal static Border IconBadge(string glyph, Brush bg = null, Brush fg = null, double size = 38)
        {
            var grid = new Grid { Width = size, Height = size };
            grid.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(9),
                Background = bg ?? AccentSoft,
                BorderBrush = bg == Accent ? Accent : AccentLight,
                BorderThickness = new Thickness(1)
            });
            grid.Children.Add(new TextBlock
            {
                Text = glyph ?? Glyph.Settings,
                FontFamily = Icons,
                FontSize = Math.Max(14, size * 0.44),
                Foreground = fg ?? (bg == Accent ? OnAccent : AccentText),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            return new Border
            {
                Width = size,
                Height = size,
                VerticalAlignment = VerticalAlignment.Top,
                Child = grid
            };
        }

        internal static Border Badge(string text, Brush bg, Brush fg, string glyph = null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (!string.IsNullOrEmpty(glyph))
            {
                var ic = Icon(glyph, 11, fg);
                ic.Margin = new Thickness(0, 0, 5, 0);
                row.Children.Add(ic);
            }
            row.Children.Add(new TextBlock
            {
                Text = text ?? string.Empty,
                Foreground = fg,
                FontFamily = Font,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            return new Border
            {
                Background = bg,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3, 8, 3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 0),
                Child = row
            };
        }

        internal static Border Banner(string title, string detail, string glyph, Brush bg, Brush border, Brush titleFg)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var ic = Icon(glyph, 20, titleFg);
            ic.Margin = new Thickness(0, 1, 12, 0);
            ic.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(ic, 0);
            grid.Children.Add(ic);

            var stack = new StackPanel();
            if (!string.IsNullOrWhiteSpace(title))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = title,
                    FontFamily = Font,
                    FontSize = 13.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = titleFg,
                    TextWrapping = TextWrapping.Wrap
                });
            }
            if (!string.IsNullOrWhiteSpace(detail))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = detail,
                    FontFamily = Font,
                    FontSize = 11.5,
                    Foreground = Ink,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, string.IsNullOrWhiteSpace(title) ? 0 : 2, 0, 0)
                });
            }
            Grid.SetColumn(stack, 1);
            grid.Children.Add(stack);

            return new Border
            {
                Background = bg,
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 11, 14, 11),
                Child = grid
            };
        }

        internal static Border KpiTile(string label, string value, string subtext = null, Brush valueBrush = null)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = (label ?? string.Empty).ToUpperInvariant(),
                FontFamily = Font,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = Muted
            });
            sp.Children.Add(new TextBlock
            {
                Text = value ?? string.Empty,
                FontFamily = Font,
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = valueBrush ?? Ink,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 2)
            });
            if (!string.IsNullOrWhiteSpace(subtext))
            {
                sp.Children.Add(new TextBlock
                {
                    Text = subtext,
                    FontFamily = Font,
                    FontSize = 11,
                    Foreground = Muted,
                    TextWrapping = TextWrapping.Wrap
                });
            }
            var body = new Border
            {
                Padding = new Thickness(12, 9, 12, 9),
                Child = sp
            };
            var stripe = new Border
            {
                Height = 3,
                Background = Accent
            };
            var cardStack = new StackPanel();
            cardStack.Children.Add(stripe);
            cardStack.Children.Add(body);
            return new Border
            {
                Background = Surface,
                BorderBrush = Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                ClipToBounds = true,
                Margin = new Thickness(4),
                Child = cardStack
            };
        }

        internal static Button SegmentedTab(string label, string glyph, bool active, Action onClick)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (!string.IsNullOrEmpty(glyph))
            {
                var ic = Icon(glyph, 13, active ? Accent : Muted);
                ic.Margin = new Thickness(0, 0, 6, 0);
                content.Children.Add(ic);
            }
            content.Children.Add(new TextBlock
            {
                Text = label,
                FontFamily = Font,
                FontSize = 12.5,
                FontWeight = active ? FontWeights.Bold : FontWeights.Normal,
                Foreground = active ? Ink : Muted,
                VerticalAlignment = VerticalAlignment.Center
            });

            var chrome = new Border
            {
                Padding = new Thickness(12, 8, 12, 8),
                BorderBrush = active ? Accent : Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, 2.5),
                Background = Brushes.Transparent,
                Child = content
            };

            var template = new ControlTemplate(typeof(Button));
            template.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));

            var btn = new Button
            {
                Content = chrome,
                Template = template,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 6, 0),
                Background = Brushes.Transparent,
                Tag = Tuple.Create(label, glyph)
            };
            if (onClick != null) btn.Click += (s, e) => onClick();
            return btn;
        }

        internal static void UpdateSegmentedTab(Button btn, bool active)
        {
            if (btn?.Content is Border chrome && chrome.Child is StackPanel panel)
            {
                chrome.BorderBrush = active ? Accent : Brushes.Transparent;
                foreach (var child in panel.Children)
                {
                    if (child is TextBlock tb)
                    {
                        bool isIcon = tb.FontFamily == Icons;
                        tb.Foreground = isIcon ? (active ? Accent : Muted) : (active ? Ink : Muted);
                        if (!isIcon) tb.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
                    }
                }
            }
        }

        internal static System.Windows.Controls.CheckBox CheckBox(string label, bool isChecked, Action<bool> onChanged = null)
        {
            var cb = new System.Windows.Controls.CheckBox
            {
                IsChecked = isChecked,
                Foreground = Ink,
                FontFamily = Font,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Content = new TextBlock
                {
                    Text = label ?? string.Empty,
                    Foreground = Ink,
                    FontFamily = Font,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                }
            };
            if (onChanged != null)
            {
                cb.Checked += (s, e) => onChanged(true);
                cb.Unchecked += (s, e) => onChanged(false);
            }
            return cb;
        }

        internal static ControlTemplate CardTemplate(Brush hoverBg = null, Brush hoverBorder = null)
        {
            var border = new FrameworkElementFactory(typeof(Border), "card");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1.2));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);

            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, hoverBg ?? AccentSoft, "card"));
            hover.Setters.Add(new Setter(Border.BorderBrushProperty, hoverBorder ?? Accent, "card"));
            template.Triggers.Add(hover);
            return template;
        }

        internal static TextBlock Icon(string glyph, double size = 13, Brush brush = null)
        {
            return new TextBlock
            {
                Text = glyph,
                FontFamily = Icons,
                FontSize = size,
                Foreground = brush ?? Ink,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        internal static TextBlock Text(string text, double size = 12, Brush brush = null, FontWeight? weight = null, bool wrap = true)
        {
            return new TextBlock
            {
                Text = text ?? "",
                FontFamily = Font,
                FontSize = size,
                Foreground = brush ?? Ink,
                FontWeight = weight ?? FontWeights.Normal,
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis
            };
        }

        internal enum ButtonKind { Primary, Secondary, Ghost, Chip, Danger }

        /// <summary>
        /// A flat button drawn entirely by SCIO, so it looks the same on every Windows
        /// theme and inside Revit's dark host - WPF's default chrome does neither.
        /// </summary>
        internal static Button Button(string label, string glyph, ButtonKind kind, Action onClick, string tooltip = null)
        {
            Brush bg, fg, border, hoverBg;
            switch (kind)
            {
                case ButtonKind.Primary: bg = Accent; fg = OnAccent; border = Accent; hoverBg = AccentHover; break;
                case ButtonKind.Danger: bg = DangerSoft; fg = Danger; border = Danger; hoverBg = DangerBorder; break;
                case ButtonKind.Chip: bg = SurfaceAlt; fg = Ink; border = Line; hoverBg = AccentSoft; break;
                case ButtonKind.Ghost: bg = Brushes.Transparent; fg = Muted; border = Brushes.Transparent; hoverBg = SurfaceAlt; break;
                default: bg = Surface; fg = Ink; border = Line; hoverBg = AccentSoft; break;
            }

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (!string.IsNullOrEmpty(glyph))
            {
                var ic = Icon(glyph, kind == ButtonKind.Chip ? 11 : 12, fg);
                ic.Margin = new Thickness(0, 0, string.IsNullOrEmpty(label) ? 0 : 6, 0);
                content.Children.Add(ic);
            }
            if (!string.IsNullOrEmpty(label))
                content.Children.Add(new TextBlock
                {
                    Text = label,
                    FontFamily = Font,
                    Foreground = fg,
                    FontSize = kind == ButtonKind.Chip ? 11 : 12,
                    FontWeight = kind == ButtonKind.Primary ? FontWeights.SemiBold : FontWeights.Medium,
                    VerticalAlignment = VerticalAlignment.Center
                });

            var chrome = new Border
            {
                Background = bg,
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(kind == ButtonKind.Chip ? 12 : 6),
                Padding = kind == ButtonKind.Chip ? new Thickness(10, 4, 10, 4) : new Thickness(12, 6, 12, 6),
                Child = content
            };

            var template = new ControlTemplate(typeof(Button));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            template.VisualTree = presenter;

            var btn = new Button
            {
                Content = chrome,
                Template = template,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 6, 6),
                ToolTip = tooltip,
                Focusable = true,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            btn.MouseEnter += (s, e) => { if (btn.IsEnabled) chrome.Background = hoverBg; };
            btn.MouseLeave += (s, e) => { chrome.Background = bg; chrome.BorderBrush = btn.IsKeyboardFocused ? Accent : border; };
            btn.PreviewMouseLeftButtonDown += (s, e) => { if (btn.IsEnabled) chrome.Opacity = 0.85; };
            btn.PreviewMouseLeftButtonUp += (s, e) => { if (btn.IsEnabled) chrome.Opacity = 1.0; };
            btn.GotKeyboardFocus += (s, e) => { chrome.BorderBrush = Accent; chrome.BorderThickness = new Thickness(1.5); };
            btn.LostKeyboardFocus += (s, e) => { chrome.BorderBrush = border; chrome.BorderThickness = new Thickness(1); };
            btn.IsEnabledChanged += (s, e) => chrome.Opacity = btn.IsEnabled ? 1.0 : 0.45;
            if (onClick != null) btn.Click += (s, e) => onClick();
            return btn;
        }

        /// <summary>Updates the visible text label of a ScioTheme.Button without destroying its styled Border chrome.</summary>
        internal static void SetButtonLabel(Button btn, string label)
        {
            if (btn?.Content is Border chrome && chrome.Child is StackPanel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is TextBlock tb && tb.FontFamily != Icons)
                    {
                        tb.Text = label ?? string.Empty;
                        return;
                    }
                }
            }
            if (btn != null) btn.Content = label ?? string.Empty;
        }

        /// <summary>A rounded card. The accent stripe marks who is speaking (SCIO vs the person).</summary>
        internal static Border Card(UIElement child, Brush stripe = null, Brush background = null)
        {
            return new Border
            {
                Background = background ?? Surface,
                BorderBrush = stripe ?? Line,
                BorderThickness = stripe != null ? new Thickness(3, 1, 1, 1) : new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 8),
                Child = child
            };
        }

        /// <summary>Small upper-case section label.</summary>
        internal static TextBlock Section(string text)
        {
            return new TextBlock
            {
                Text = (text ?? "").ToUpperInvariant(),
                FontFamily = Font,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = Muted,
                Margin = new Thickness(0, 6, 0, 4)
            };
        }

        internal static System.Windows.Controls.TextBox Input(string placeholderTooltip = null)
        {
            return new System.Windows.Controls.TextBox
            {
                Background = Surface,
                Foreground = Ink,
                CaretBrush = Ink,
                BorderBrush = Line,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 4, 6, 4),
                FontFamily = Font,
                FontSize = 12,
                ToolTip = placeholderTooltip
            };
        }
    }
}

