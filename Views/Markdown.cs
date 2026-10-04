using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgentManagerNotch.Views
{
    /// <summary>
    /// Renderizador Markdown mínimo para un RichTextBox de solo lectura (texto seleccionable):
    /// títulos, listas, tablas, **negrita**, *cursiva*, `código`, bloques ``` con botón de copiar y
    /// [enlaces](url). Uso: v:Markdown.Text="{Binding Text}".
    /// </summary>
    public static class Markdown
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
            "Text", typeof(string), typeof(Markdown), new PropertyMetadata("", (d, e) =>
            {
                if (d is RichTextBox rtb) Render(rtb, e.NewValue as string ?? "");
            }));

        public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);
        public static void SetText(DependencyObject d, string v) => d.SetValue(TextProperty, v);

        private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
        private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
        private static readonly Brush CodeBg = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)));
        private static readonly Brush CodeFg = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xC9, 0x8B)));
        private static readonly Brush LinkFg = Freeze(new SolidColorBrush(Color.FromRgb(0x8E, 0xB8, 0xFF)));
        private static readonly Brush BlockBg = Freeze(new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x12)));
        private static readonly Brush Selection = Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0x7A, 0x8C, 0xFF)));
        private const string CopyGlyph = "", DoneGlyph = "";

        private static readonly Regex InlineRx = new(
            @"(\*\*(?<b>.+?)\*\*)|(`(?<c>[^`]+)`)|(\[(?<lt>[^\]]+)\]\((?<lu>https?://[^)\s]+)\))|((?<![\w*])\*(?<i>[^*\s][^*]*?)\*(?![\w*]))",
            RegexOptions.Compiled);

        private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

        private static bool _wheelHooked;

        /// <summary>
        /// Los cuadros de solo lectura (mensajes seleccionables) se quedarían la rueda del ratón:
        /// se reenvía al contenedor para que el chat siga desplazándose.
        /// </summary>
        public static void EnableWheelPassThrough()
        {
            if (_wheelHooked) return;
            _wheelHooked = true;
            EventManager.RegisterClassHandler(typeof(TextBoxBase), UIElement.PreviewMouseWheelEvent,
                new MouseWheelEventHandler((s, e) =>
                {
                    if (e.Handled || s is not TextBoxBase { IsReadOnly: true } box) return;
                    if (VisualTreeHelper.GetParent(box) is not UIElement parent) return;
                    e.Handled = true;
                    parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                    {
                        RoutedEvent = UIElement.MouseWheelEvent,
                        Source = box
                    });
                }));
        }

        private static void Render(RichTextBox rtb, string text)
        {
            var doc = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontFamily = rtb.FontFamily,
                FontSize = rtb.FontSize,
                Foreground = rtb.Foreground,
                LineHeight = Block.GetLineHeight(rtb)
            };
            var lines = text.Replace("\r", "").Trim().Split('\n');
            bool inCode = false;
            var code = new List<string>();
            Paragraph? para = null;

            // Línea nueva dentro del párrafo actual (los bloques de código lo cortan).
            Paragraph Line()
            {
                if (para == null) { para = new Paragraph { Margin = new Thickness(0) }; doc.Blocks.Add(para); }
                else para.Inlines.Add(new LineBreak());
                return para;
            }
            // Tabla o bloque de código: ya llevan su propio margen, así que las líneas en blanco que los
            // rodean no deben sumar renglones vacíos (el salto final del párrafo anterior se quita).
            void AddBlock(Block block)
            {
                while (para?.Inlines.LastInline is LineBreak br) para.Inlines.Remove(br);
                if (para is { Inlines.Count: 0 }) doc.Blocks.Remove(para);
                doc.Blocks.Add(block);
                para = null;
            }
            void FlushCode()
            {
                AddBlock(CodeBlock(string.Join("\n", code)));
                code.Clear();
            }

            for (int i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                if (raw.TrimStart().StartsWith("```"))
                {
                    if (inCode) FlushCode();
                    inCode = !inCode;
                    continue;
                }
                if (inCode) { code.Add(raw); continue; }

                // Tabla: fila de encabezado + separador |---|:---:| + filas mientras empiecen por |
                if (raw.TrimStart().StartsWith('|') && i + 1 < lines.Length && TableSepRx.IsMatch(lines[i + 1]))
                {
                    var rows = new List<string> { raw };
                    var sep = lines[++i];
                    while (i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith('|')) rows.Add(lines[++i]);
                    AddBlock(TableBlock(rows, sep, rtb.FontSize));
                    continue;
                }

                var line = raw;
                if (para == null && doc.Blocks.LastBlock is Table or BlockUIContainer && string.IsNullOrWhiteSpace(line)) continue;
                var h = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
                if (h.Success)
                {
                    var span = new Span { FontWeight = FontWeights.SemiBold, FontSize = rtb.FontSize + (h.Groups[1].Length <= 2 ? 2 : 1) };
                    AddInlines(span.Inlines, h.Groups[2].Value);
                    Line().Inlines.Add(span);
                    continue;
                }
                var li = Regex.Match(line, @"^(\s*)([-*+]|\d+[.)])\s+(.*)$");
                if (li.Success)
                {
                    var indent = new string(' ', li.Groups[1].Length);
                    var marker = char.IsDigit(li.Groups[2].Value[0]) ? li.Groups[2].Value : "•";
                    var p = Line();
                    p.Inlines.Add(new Run($"{indent}  {marker} "));
                    AddInlines(p.Inlines, li.Groups[3].Value);
                    continue;
                }
                if (Regex.IsMatch(line, @"^\s*([-*_])\1{2,}\s*$")) { Line().Inlines.Add(new Run("────────") { Foreground = Brushes.Gray }); continue; }
                var target = Line();
                if (line.StartsWith("> ")) { target.Inlines.Add(new Run("▍ ") { Foreground = Brushes.Gray }); line = line[2..]; }
                AddInlines(target.Inlines, line);
            }
            if (inCode && code.Count > 0) FlushCode();
            rtb.Document = doc;
        }

        private static void AddInlines(InlineCollection target, string text)
        {
            int pos = 0;
            foreach (Match m in InlineRx.Matches(text))
            {
                if (m.Index > pos) target.Add(new Run(text[pos..m.Index]));
                if (m.Groups["b"].Success)
                {
                    var b = new Bold();
                    AddInlines(b.Inlines, m.Groups["b"].Value);
                    target.Add(b);
                }
                else if (m.Groups["c"].Success)
                {
                    target.Add(new Run(" " + m.Groups["c"].Value + " ") { FontFamily = Mono, Background = CodeBg, Foreground = CodeFg, FontSize = 12 });
                }
                else if (m.Groups["lt"].Success)
                {
                    var url = m.Groups["lu"].Value;
                    var link = new Hyperlink(new Run(m.Groups["lt"].Value)) { Foreground = LinkFg, Cursor = Cursors.Hand };
                    link.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } };
                    link.ToolTip = url;
                    target.Add(link);
                }
                else if (m.Groups["i"].Success)
                {
                    target.Add(new Italic(new Run(m.Groups["i"].Value)));
                }
                pos = m.Index + m.Length;
            }
            if (pos < text.Length) target.Add(new Run(text[pos..]));
        }

        private static readonly Regex TableSepRx = new(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$", RegexOptions.Compiled);
        private static readonly Brush TableBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x3A)));
        private static readonly Brush TableHeadBg = Freeze(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x26)));
        private static readonly Brush TableAltBg = Freeze(new SolidColorBrush(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF)));

        private static List<string> SplitRow(string row)
        {
            var s = row.Trim();
            if (s.StartsWith('|')) s = s[1..];
            if (s.EndsWith('|') && !s.EndsWith("\\|")) s = s[..^1];
            var cells = new List<string>();
            foreach (var c in Regex.Split(s, @"(?<!\\)\|")) cells.Add(c.Replace("\\|", "|").Trim());
            return cells;
        }

        /// <summary>Tabla Markdown: encabezado resaltado, filas alternas, alineación por columna y
        /// anchos según el contenido (las tablas de FlowDocument no admiten ancho automático).</summary>
        private static Block TableBlock(List<string> rows, string sepLine, double fontSize)
        {
            var header = SplitRow(rows[0]);
            var sep = SplitRow(sepLine);
            var body = rows.Skip(1).Select(SplitRow).ToList();
            int cols = Math.Max(header.Count, body.Count == 0 ? 0 : body.Max(r => r.Count));

            var align = new TextAlignment[cols];
            var weight = new double[cols];
            for (int c = 0; c < cols; c++)
            {
                var s = c < sep.Count ? sep[c] : "";
                align[c] = s.StartsWith(':') && s.EndsWith(':') ? TextAlignment.Center
                         : s.EndsWith(':') ? TextAlignment.Right : TextAlignment.Left;
                // Peso = palabra más larga (para no partir palabras) + padding + algo por el texto
                // restante (amortiguado: las columnas largas igual se ajustan en varias líneas).
                int len = 0, word = 0;
                foreach (var r in body.Prepend(header))
                {
                    if (c >= r.Count) continue;
                    len = Math.Max(len, r[c].Length);
                    foreach (var w in r[c].Split(' ', StringSplitOptions.RemoveEmptyEntries)) word = Math.Max(word, w.Length);
                }
                word = Math.Min(word, 25);
                weight[c] = word * 1.15 + 4 + Math.Sqrt(Math.Max(0, len - word)) * 2;
            }

            var table = new Table
            {
                CellSpacing = 0,
                BorderBrush = TableBorder,
                BorderThickness = new Thickness(1, 1, 0, 0),
                Margin = new Thickness(0, 8, 0, 8),
                FontSize = fontSize - 1,
                LineHeight = double.NaN
            };
            for (int c = 0; c < cols; c++) table.Columns.Add(new TableColumn { Width = new GridLength(weight[c], GridUnitType.Star) });

            var group = new TableRowGroup();
            table.RowGroups.Add(group);
            void AddRow(List<string> cells, bool head, bool alt)
            {
                var row = new TableRow();
                if (head) { row.Background = TableHeadBg; row.FontWeight = FontWeights.SemiBold; }
                else if (alt) row.Background = TableAltBg;
                for (int c = 0; c < cols; c++)
                {
                    var p = new Paragraph { Margin = new Thickness(0), TextAlignment = align[c] };
                    var parts = Regex.Split(c < cells.Count ? cells[c] : "", @"<br\s*/?>", RegexOptions.IgnoreCase);
                    for (int k = 0; k < parts.Length; k++)
                    {
                        if (k > 0) p.Inlines.Add(new LineBreak());
                        AddInlines(p.Inlines, parts[k]);
                    }
                    row.Cells.Add(new TableCell(p)
                    {
                        Padding = new Thickness(8, 4, 8, 4),
                        BorderBrush = TableBorder,
                        BorderThickness = new Thickness(0, 0, 1, 1)
                    });
                }
                group.Rows.Add(row);
            }
            AddRow(header, true, false);
            for (int r = 0; r < body.Count; r++) AddRow(body[r], false, r % 2 == 1);
            return table;
        }

        /// <summary>Bloque ``` : texto seleccionable y botón de copiar arriba a la derecha.</summary>
        private static Block CodeBlock(string code)
        {
            var box = new TextBox
            {
                Text = code,
                IsReadOnly = true,
                FontFamily = Mono,
                FontSize = 11.5,
                Foreground = CodeFg,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                TextWrapping = TextWrapping.Wrap,
                SelectionBrush = Selection,
                CaretBrush = Brushes.Transparent,
                Cursor = Cursors.IBeam,
                Margin = new Thickness(0, 0, 26, 0)
            };
            if (Application.Current?.TryFindResource("SelectableText") is Style sel) box.Style = sel;
            box.FontFamily = Mono;
            box.FontSize = 11.5;
            box.Foreground = CodeFg;

            var glyph = new TextBlock { Text = CopyGlyph, FontFamily = Icons, FontSize = 12 };
            var copy = new Button
            {
                Content = glyph,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -4, -6, 0),
                ToolTip = "Copiar código"
            };
            if (Application.Current?.TryFindResource("IconButton") is Style st) copy.Style = st;
            copy.Width = copy.Height = 26;
            copy.Click += (_, _) =>
            {
                try { Clipboard.SetText(code); } catch { return; }
                glyph.Text = DoneGlyph;
                copy.ToolTip = "Copiado";
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                t.Tick += (_, _) => { t.Stop(); glyph.Text = CopyGlyph; copy.ToolTip = "Copiar código"; };
                t.Start();
            };

            var grid = new Grid();
            grid.Children.Add(box);
            grid.Children.Add(copy);
            var border = new Border
            {
                Background = BlockBg,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Child = grid
            };
            return new BlockUIContainer(border) { Margin = new Thickness(0, 4, 0, 4) };
        }
    }
}
