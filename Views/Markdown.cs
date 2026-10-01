using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace CoucouWin.Views
{
    /// <summary>
    /// Renderizador Markdown mínimo para TextBlock: títulos, listas, **negrita**, *cursiva*,
    /// `código`, bloques ``` y [enlaces](url). Uso: v:Markdown.Text="{Binding Text}".
    /// </summary>
    public static class Markdown
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
            "Text", typeof(string), typeof(Markdown), new PropertyMetadata("", (d, e) =>
            {
                if (d is TextBlock tb) Render(tb, e.NewValue as string ?? "");
            }));

        public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);
        public static void SetText(DependencyObject d, string v) => d.SetValue(TextProperty, v);

        private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
        private static readonly Brush CodeBg = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)));
        private static readonly Brush CodeFg = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xC9, 0x8B)));
        private static readonly Brush LinkFg = Freeze(new SolidColorBrush(Color.FromRgb(0x8E, 0xB8, 0xFF)));
        private static readonly Brush BlockBg = Freeze(new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x12)));

        private static readonly Regex InlineRx = new(
            @"(\*\*(?<b>.+?)\*\*)|(`(?<c>[^`]+)`)|(\[(?<lt>[^\]]+)\]\((?<lu>https?://[^)\s]+)\))|((?<![\w*])\*(?<i>[^*\s][^*]*?)\*(?![\w*]))",
            RegexOptions.Compiled);

        private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

        private static void Render(TextBlock tb, string text)
        {
            tb.Inlines.Clear();
            var lines = text.Replace("\r", "").Trim().Split('\n');
            bool inCode = false;
            var code = new List<string>();
            bool first = true;

            void Break() { if (!first) tb.Inlines.Add(new LineBreak()); first = false; }

            foreach (var raw in lines)
            {
                if (raw.TrimStart().StartsWith("```"))
                {
                    if (inCode)
                    {
                        Break();
                        tb.Inlines.Add(CodeBlock(string.Join("\n", code)));
                        code.Clear();
                    }
                    inCode = !inCode;
                    continue;
                }
                if (inCode) { code.Add(raw); continue; }

                Break();
                var line = raw;
                var h = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
                if (h.Success)
                {
                    var span = new Span { FontWeight = FontWeights.SemiBold, FontSize = tb.FontSize + (h.Groups[1].Length <= 2 ? 2 : 1) };
                    AddInlines(span.Inlines, h.Groups[2].Value);
                    tb.Inlines.Add(span);
                    continue;
                }
                var li = Regex.Match(line, @"^(\s*)([-*+]|\d+[.)])\s+(.*)$");
                if (li.Success)
                {
                    var indent = new string(' ', li.Groups[1].Length);
                    var marker = char.IsDigit(li.Groups[2].Value[0]) ? li.Groups[2].Value : "•";
                    tb.Inlines.Add(new Run($"{indent}  {marker} "));
                    AddInlines(tb.Inlines, li.Groups[3].Value);
                    continue;
                }
                if (Regex.IsMatch(line, @"^\s*([-*_])\1{2,}\s*$")) { tb.Inlines.Add(new Run("────────") { Foreground = Brushes.Gray }); continue; }
                if (line.StartsWith("> ")) { tb.Inlines.Add(new Run("▍ ") { Foreground = Brushes.Gray }); line = line[2..]; }
                AddInlines(tb.Inlines, line);
            }
            if (inCode && code.Count > 0) { Break(); tb.Inlines.Add(CodeBlock(string.Join("\n", code))); }
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
                    var link = new Hyperlink(new Run(m.Groups["lt"].Value)) { Foreground = LinkFg, Cursor = System.Windows.Input.Cursors.Hand };
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

        private static Inline CodeBlock(string code)
        {
            var tb = new TextBlock
            {
                Text = code,
                FontFamily = Mono,
                FontSize = 11.5,
                Foreground = CodeFg,
                TextWrapping = TextWrapping.Wrap
            };
            var border = new Border
            {
                Background = BlockBg,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 4, 0, 4),
                Child = tb,
                MaxWidth = 400
            };
            return new InlineUIContainer(border) { BaselineAlignment = BaselineAlignment.Top };
        }
    }
}
