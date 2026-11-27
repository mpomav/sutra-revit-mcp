using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Scio.Cad.Ui
{
    /// <summary>
    /// Unified Sutra design tokens &amp; WPF helpers for AutoCAD / Civil 3D,
    /// mirroring ScioTheme in Scio.Revit (SCIO Orange #E06400, dark slate surfaces, DM Sans / Segoe UI).
    /// </summary>
    internal static class SutraCadTheme
    {
        internal static readonly Brush Accent = B("#E06400");
        internal static readonly Brush AccentHover = B("#F28C38");
        internal static readonly Brush AccentSoft = B("#332012");
        internal static readonly Brush Ground = B("#1A1B1E");
        internal static readonly Brush Surface = B("#24262B");
        internal static readonly Brush SurfaceAlt = B("#2E3037");
        internal static readonly Brush Line = B("#3E4046");
        internal static readonly Brush Ink = B("#F3F4F6");
        internal static readonly Brush Muted = B("#9CA3AF");
        internal static readonly Brush Success = B("#1F9D68");
        internal static readonly Brush SuccessSoft = B("#123025");
        internal static readonly Brush Warning = B("#D97706");
        internal static readonly Brush Danger = B("#DC2626");

        internal static readonly FontFamily Font = new FontFamily("DM Sans, Segoe UI");
        internal static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas, Courier New");

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        internal static void ApplyWindowChrome(Window w, string title, double width, double height)
        {
            w.Title = title;
            w.Width = width;
            w.Height = height;
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.Background = Ground;
            w.Foreground = Ink;
            w.FontFamily = Font;
            w.FontSize = 13;
            w.SourceInitialized += (s, e) =>
            {
                try
                {
                    IntPtr hwnd = new WindowInteropHelper(w).Handle;
                    int dark = 1;
                    DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
                }
                catch { }
            };
        }

        internal static BitmapImage LoadIcon(string fileName)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string resName = "Scio.Cad.Icons." + fileName;
                using (Stream stream = asm.GetManifestResourceStream(resName))
                {
                    if (stream == null) return null;
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.StreamSource = stream;
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.EndInit();
                    img.Freeze();
                    return img;
                }
            }
            catch { return null; }
        }

        internal static Border HeaderBar(string title, string subtitle, string badgeText, Brush badgeBg, Brush badgeFg, string iconName = "status32.png")
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = LoadIcon(iconName);
            if (icon != null)
            {
                var img = new Image
                {
                    Source = icon,
                    Width = 30,
                    Height = 30,
                    Margin = new Thickness(0, 0, 12, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(img, 0);
                grid.Children.Add(img);
            }

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Ink
            });
            textStack.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 12,
                Foreground = Muted,
                Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(textStack, 1);
            grid.Children.Add(textStack);

            if (!string.IsNullOrEmpty(badgeText))
            {
                var badge = new Border
                {
                    Background = badgeBg,
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(10, 4, 10, 4),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = badgeText,
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = badgeFg
                    }
                };
                Grid.SetColumn(badge, 2);
                grid.Children.Add(badge);
            }

            return new Border
            {
                Background = Surface,
                BorderBrush = Line,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(18, 14, 18, 14),
                Child = grid
            };
        }

        internal static Border Card(UIElement content, Thickness? margin = null)
        {
            return new Border
            {
                Background = Surface,
                BorderBrush = Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14),
                Margin = margin ?? new Thickness(0, 0, 0, 12),
                Child = content
            };
        }

        internal static Grid KeyValueRow(string key, string value, bool mono = false)
        {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var k = new TextBlock { Text = key, Foreground = Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var v = new TextBlock
            {
                Text = value ?? "—",
                Foreground = Ink,
                FontSize = 12,
                FontFamily = mono ? Mono : Font,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(k, 0);
            Grid.SetColumn(v, 1);
            g.Children.Add(k);
            g.Children.Add(v);
            return g;
        }

        internal static Button PrimaryButton(string text, RoutedEventHandler onClick)
        {
            var btn = new Button
            {
                Content = text,
                Background = Accent,
                Foreground = Brushes.White,
                BorderBrush = Accent,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 7, 14, 7),
                FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btn.Click += onClick;
            return btn;
        }

        internal static Button SecondaryButton(string text, RoutedEventHandler onClick)
        {
            var btn = new Button
            {
                Content = text,
                Background = SurfaceAlt,
                Foreground = Ink,
                BorderBrush = Line,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 7, 14, 7),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btn.Click += onClick;
            return btn;
        }

        internal static CheckBox StyledCheck(string label, bool isChecked)
        {
            return new CheckBox
            {
                Content = label,
                IsChecked = isChecked,
                Foreground = Ink,
                Margin = new Thickness(0, 4, 0, 4),
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        internal static TextBox StyledInput(string value, double width = 90)
        {
            return new TextBox
            {
                Text = value,
                Width = width,
                Background = Ground,
                Foreground = Ink,
                BorderBrush = Line,
                Padding = new Thickness(6, 3, 6, 3),
                FontFamily = Mono,
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        private static Brush B(string hex)
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }
    }
}
