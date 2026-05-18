using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using EthernetPacketGenerator.Models;

namespace EthernetPacketGenerator.Converters;

public class ValidationSeverityToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ValidationSeverity severity)
            return severity switch
            {
                ValidationSeverity.Error   => Brushes.Red,
                ValidationSeverity.Warning => Brushes.Orange,
                _ => Brushes.Transparent
            };
        return Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class ValidationSeverityToBorderBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush _errorBrush   = Freeze(Color.FromRgb(220, 50, 50));
    private static readonly SolidColorBrush _warningBrush = Freeze(Color.FromRgb(220, 150, 20));
    private static readonly SolidColorBrush _defaultBrush = Freeze(Color.FromRgb(80, 80, 80));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ValidationSeverity severity)
            return severity switch
            {
                ValidationSeverity.Error   => _errorBrush,
                ValidationSeverity.Warning => _warningBrush,
                _ => _defaultBrush
            };
        return _defaultBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();

    private static SolidColorBrush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}

public class BoolToHighlightConverter : IValueConverter
{
    private static readonly SolidColorBrush _on = Freeze(Color.FromArgb(80, 255, 200, 50));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? _on : Brushes.Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();

    private static SolidColorBrush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}

public class BoolToSelectedHighlightConverter : IValueConverter
{
    private static readonly SolidColorBrush _on = Freeze(Color.FromArgb(120, 100, 180, 255));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? _on : Brushes.Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();

    private static SolidColorBrush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;
}

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value == null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class ProtocolTypeToColorConverter : IValueConverter
{
    private static readonly Dictionary<ProtocolType, SolidColorBrush> _cache = BuildCache();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is ProtocolType t && _cache.TryGetValue(t, out var brush) ? brush : Brushes.Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();

    private static Dictionary<ProtocolType, SolidColorBrush> BuildCache()
    {
        var map = new Dictionary<ProtocolType, SolidColorBrush>
        {
            [ProtocolType.Ethernet]   = Freeze(Color.FromRgb(70, 130, 180)),
            [ProtocolType.ARP]        = Freeze(Color.FromRgb(160, 82,  45)),
            [ProtocolType.IPv4]       = Freeze(Color.FromRgb(34,  139, 34)),
            [ProtocolType.ICMP]       = Freeze(Color.FromRgb(148, 0,   211)),
            [ProtocolType.TCP]        = Freeze(Color.FromRgb(205, 92,  92)),
            [ProtocolType.UDP]        = Freeze(Color.FromRgb(210, 105, 30)),
            [ProtocolType.VLAN]       = Freeze(Color.FromRgb(70,  160, 160)),
            [ProtocolType.RawPayload] = Freeze(Color.FromRgb(105, 105, 105)),
        };
        return map;
    }

    private static SolidColorBrush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}

public class ProtocolTypeToAbbrevConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ProtocolType type)
            return type switch
            {
                ProtocolType.Ethernet   => "ETH",
                ProtocolType.ARP        => "ARP",
                ProtocolType.IPv4       => "IPv4",
                ProtocolType.ICMP       => "ICMP",
                ProtocolType.TCP        => "TCP",
                ProtocolType.UDP        => "UDP",
                ProtocolType.VLAN       => "VLAN",
                ProtocolType.RawPayload => "RAW",
                _ => "???"
            };
        return "???";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
