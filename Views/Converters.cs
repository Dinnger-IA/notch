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

    /// <summary>
    /// Acción de un agente (mensaje «Herramienta  detalle»): parámetro "icon" → glifo según el tipo,
    /// "name" → herramienta, "detail" → lo que hizo, "detailvis" → visible si hay detalle.
    /// </summary>
    public class ToolActionConverter : IValueConverter
    {
        public object Convert(object? value, Type t, object? p, CultureInfo c)
        {
            var text = value as string ?? "";
            var cut = text.IndexOf("  ", StringComparison.Ordinal);
            var name = (cut < 0 ? text : text[..cut]).Trim();
            var detail = cut < 0 ? "" : text[(cut + 2)..].Trim();
            return (p as string) switch
            {
                "name" => Pretty(name),
                "detail" => detail,
                "detailvis" => detail == "" ? Visibility.Collapsed : Visibility.Visible,
                _ => Icon(name)
            };
        }

        /// <summary>mcp__servidor__herramienta → «herramienta · servidor».</summary>
        private static string Pretty(string tool)
        {
            var parts = tool.Split("__");
            return parts.Length == 3 && parts[0] == "mcp" ? $"{parts[2]} · {parts[1]}" : tool;
        }

        private static string Icon(string tool)
        {
            var n = tool.ToLowerInvariant();
            if (n.Contains("todo")) return "";                                                      // lista
            if (n is "read" or "notebookread" or "read_file" or "read_many_files") return "\uE8A5";        // documento
            if (n.Contains("write") || n.Contains("edit") || n == "replace") return "\uE70F";             // lápiz
            if (n is "bash" or "shell" or "powershell" or "run_shell_command") return "\uE756";          // terminal
            if (n is "grep" or "glob" or "ls" or "list_directory" or "search_file_content") return "\uE721"; // lupa
            if (n.StartsWith("web") || n.Contains("fetch") || n.Contains("search")) return "\uE774";     // globo
            if (n.StartsWith("mcp")) return "\uE943";                                                     // conector
            if (n is "task" or "agent") return "\uE716";                                                  // persona
            return "\uE713";                                                                              // engranaje
        }

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
