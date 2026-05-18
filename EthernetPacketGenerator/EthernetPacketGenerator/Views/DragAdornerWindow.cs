using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using EthernetPacketGenerator.Converters;
using EthernetPacketGenerator.Models;

namespace EthernetPacketGenerator.Views;

internal sealed class DragAdornerWindow : Window
{
    private const int GWL_EXSTYLE       = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED     = 0x00080000;
    private const int WS_EX_NOACTIVATE  = 0x08000000;

    [DllImport("user32.dll")] private static extern int  GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int  SetWindowLong(IntPtr hwnd, int index, int newStyle);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);

    // Chip size in WPF DIP units
    internal const double ChipW = 70;
    internal const double ChipH = 54;

    private static readonly ProtocolTypeToColorConverter  _colorConv  = new();
    private static readonly ProtocolTypeToAbbrevConverter _abbrevConv = new();

    // DPI scale — set when the first adorner window is initialised
    private static double _scaleX = 1.0;
    private static double _scaleY = 1.0;

    private DragAdornerWindow() { }

    // ── factory ──────────────────────────────────────────────────────────────
    public static DragAdornerWindow Create(ProtocolBlock block, double opacity = 0.82)
    {
        var win = new DragAdornerWindow
        {
            WindowStyle       = WindowStyle.None,
            AllowsTransparency = true,
            Background        = Brushes.Transparent,
            ShowInTaskbar     = false,
            Topmost           = true,
            IsHitTestVisible  = false,
            Width             = ChipW,
            Height            = ChipH,
            Opacity           = opacity,
            Content           = BuildChip(block),
            ShowActivated     = false,
        };

        win.SourceInitialized += (_, _) =>
        {
            var hwnd  = new WindowInteropHelper(win).Handle;
            int style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE,
                style | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE);

            // Read actual DPI for this monitor
            var src = HwndSource.FromHwnd(hwnd);
            if (src != null)
            {
                _scaleX = src.CompositionTarget.TransformToDevice.M11;
                _scaleY = src.CompositionTarget.TransformToDevice.M22;
            }
        };

        return win;
    }

    // ── cursor helpers ────────────────────────────────────────────────────────

    /// <summary>Cursor position in screen pixels (integer).</summary>
    internal static void GetCursorScreenPx(out int x, out int y)
    {
        GetCursorPos(out var pt);
        x = pt.X; y = pt.Y;
    }

    /// <summary>
    /// Cursor position converted to WPF DIP units
    /// (the same unit as Window.Left / Window.Top).
    /// </summary>
    internal static Point CursorDip()
    {
        GetCursorPos(out var pt);
        return new Point(pt.X / _scaleX, pt.Y / _scaleY);
    }

    // ── positioning ───────────────────────────────────────────────────────────

    /// <summary>
    /// Move the adorner so that <paramref name="chipOffsetDip"/>
    /// (position within the chip in WPF DIP units) sits exactly under the cursor.
    /// </summary>
    public void PlaceAtCursor(Point chipOffsetDip)
    {
        var cur = CursorDip();
        Left = cur.X - chipOffsetDip.X;
        Top  = cur.Y - chipOffsetDip.Y;
    }

    // ── chip visual ───────────────────────────────────────────────────────────
    private static Border BuildChip(ProtocolBlock block)
    {
        var color  = _colorConv .Convert(block.Type, typeof(Brush),  null!, System.Globalization.CultureInfo.InvariantCulture) as SolidColorBrush ?? Brushes.Gray;
        var abbrev = _abbrevConv.Convert(block.Type, typeof(string), null!, System.Globalization.CultureInfo.InvariantCulture) as string ?? "???";

        return new Border
        {
            Width           = ChipW,
            Height          = ChipH,
            CornerRadius    = new CornerRadius(4),
            Background      = color,
            BorderThickness = new Thickness(2),
            BorderBrush     = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
            Child = new StackPanel
            {
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text                = abbrev,
                        FontWeight          = FontWeights.Bold,
                        FontSize            = 13,
                        Foreground          = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text                = block.ByteLength.ToString(),
                        FontSize            = 9,
                        Foreground          = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xFF)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text                = "B",
                        FontSize            = 8,
                        Foreground          = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xCC)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin              = new Thickness(0, -2, 0, 0),
                    },
                }
            }
        };
    }
}
