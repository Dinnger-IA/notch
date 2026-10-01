using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AgentManagerNotch.Views
{
    public class NullToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }
        public object Convert(object? value, Type t, object? p, CultureInfo c)
            => (value == null) ^ Invert ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
    }

    public class BoolToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }
        public object Convert(object? value, Type t, object? p, CultureInfo c)
            => (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
    }

    /// <summary>Texto vacío o null → Collapsed.</summary>
    public class EmptyToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type t, object? p, CultureInfo c)
            => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
    }

    /// <summary>0 → Collapsed; cualquier otro número → Visible.</summary>
    public class ZeroToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type t, object? p, CultureInfo c)
            => value is int i && i != 0 ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
    }

    /// <summary>Color → SolidColorBrush, con alfa opcional (parámetro 0..255).</summary>
    public class ColorToBrushConverter : IValueConverter
    {
        public object Convert(object? value, Type t, object? p, CultureInfo c)
        {
            var col = value is Color cc ? cc : Colors.Gray;
            if (p != null && byte.TryParse(p.ToString(), out var a)) col = Color.FromArgb(a, col.R, col.G, col.B);
            var b = new SolidColorBrush(col);
            b.Freeze();
            return b;
        }
        public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
    }
}
