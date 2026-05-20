using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using EthernetPacketGenerator.Models;

namespace EthernetPacketGenerator.Views;

/// <summary>Floating chip shown while dragging a SequenceItem row.</summary>
internal sealed class SeqItemAdornerWindow : Window
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

    private static double _scaleX = 1.0;
    private static double _scaleY = 1.0;

    private SeqItemAdornerWindow() { }

    internal static SeqItemAdornerWindow Create(SequenceItem item)
    {
        bool isEvent = item.Kind == SequenceItemKind.Event;
        var bg    = isEvent ? Color.FromRgb(0x3A, 0x2A, 0x10) : Color.FromRgb(0x1E, 0x1E, 0x3A);
        var bd    = isEvent ? Color.FromRgb(0xFF, 0xAA, 0x44) : Color.FromRgb(0x44, 0x88, 0xFF);
        var label = item.DisplayName.Length > 22
            ? item.DisplayName[..22] + "…"
            : item.DisplayName;

        var chip = new Border
        {
            Width           = 160,
            Height          = 28,
            CornerRadius    = new CornerRadius(4),
            Background      = new SolidColorBrush(bg),
            BorderBrush     = new SolidColorBrush(bd),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text                = label,
                Foreground          = Brushes.White,
                FontSize            = 11,
                FontFamily          = new FontFamily("Consolas"),
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Padding             = new Thickness(6, 0, 6, 0),
            }
        };

        var win = new SeqItemAdornerWindow
        {
            WindowStyle        = WindowStyle.None,
            AllowsTransparency = true,
            Background         = Brushes.Transparent,
            ShowInTaskbar      = false,
            Topmost            = true,
            IsHitTestVisible   = false,
            Width              = 160,
            Height             = 28,
            Opacity            = 0.85,
            Content            = chip,
            ShowActivated      = false,
        };

        win.SourceInitialized += (_, _) =>
        {
            var hwnd  = new WindowInteropHelper(win).Handle;
            int style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE,
                style | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE);
            var src = HwndSource.FromHwnd(hwnd);
            if (src != null)
            {
                _scaleX = src.CompositionTarget.TransformToDevice.M11;
                _scaleY = src.CompositionTarget.TransformToDevice.M22;
            }
        };

        return win;
    }

    internal void PlaceAtCursor(Point chipOffset)
    {
        GetCursorPos(out var pt);
        Left = pt.X / _scaleX - chipOffset.X;
        Top  = pt.Y / _scaleY - chipOffset.Y;
    }
}
