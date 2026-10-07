using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentManagerNotch.Services;
using AgentManagerNotch.Views;
using static AgentManagerNotch.Views.GifDemos;

namespace AgentManagerNotch.Plugins.Voz
{
    /// <summary>Vistas de <c>--grabar-gif</c> del plugin de voz (<c>voz</c> y <c>microfono</c>).</summary>
    internal static class VoiceGifDemos
    {
        /// <summary>El agente leyendo en voz alta su respuesta: boca y ondas al ritmo de la voz.</summary>
        public static async Task<int> SpeakingAsync(string path)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // Volumen de ejemplo: sílabas (~6 por segundo) con una pausa breve entre frases
            Controls.MochiView.SpeechLevel = () =>
            {
                var t = clock.Elapsed.TotalSeconds;
                if (t % 2.2 > 1.9) return 0;
                return Math.Max(0, Math.Sin(t * Math.PI * 6.4)) * (0.45 + 0.55 * Math.Abs(Math.Sin(t * 1.7)));
            };
            var mochi = new Controls.MochiView { Width = 84, Height = 84, BodyColor = Color.FromRgb(0xFF, 0x8A, 0x65), Interactive = false, VerticalAlignment = VerticalAlignment.Center };
            var answer = new RichTextBox { FontSize = 13.5, Width = 380, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) };
            if (Application.Current.TryFindResource("SelectableDoc") is Style st) answer.Style = st;
            answer.Foreground = Frozen(Color.FromRgb(0xEC, 0xEC, 0xF2));
            System.Windows.Documents.Block.SetLineHeight(answer, 20);
            Markdown.SetText(answer, "Revisé la documentación: el despliegue de **Hermes** falla porque falta la variable `DB_HOST` en el entorno de pruebas. Ya la añadí y el pod arranca bien.");
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Children = { mochi, answer } };
            var host = new Border { Background = NotchBg, Padding = new Thickness(20, 18, 20, 18), Child = panel };
            var win = new Window { Content = host, SizeToContent = SizeToContent.WidthAndHeight };
            await ShowOffscreenAsync(win);
            var n = await new GifWriter(host, NotchBg, 560).RecordAsync(path, async () =>
            {
                await Task.Delay(700);
                mochi.IsSpeaking = true;
                await Task.Delay(4400);
                mochi.IsSpeaking = false;
                await Task.Delay(900);
            });
            win.Close();
            Controls.MochiView.SpeechLevel = null;
            return n;
        }

        /// <summary>
        /// Hablar manteniendo pulsado el micrófono: la caja de entrada (como InputBar de NotchWindow.xaml) en reposo,
        /// escuchando, a punto de cancelar, transcribiendo y, al enviar, el agente respondiendo en voz alta.
        /// </summary>
        public static async Task<int> PushToTalkAsync(string path)
        {
            var text = Frozen(Color.FromRgb(0xEC, 0xEC, 0xF2));
            var dim = Frozen(Color.FromRgb(0x6E, 0x6E, 0x7C));
            var red = Frozen(Color.FromRgb(0xFF, 0x6B, 0x6B));
            if (Application.Current.TryFindResource("ErrorFg") is Brush err) red = err;
            var iconFont = Application.Current.TryFindResource("IconFont") as FontFamily ?? new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

            var caption = new TextBlock { FontSize = 12.5, Foreground = Frozen(Color.FromRgb(0x9A, 0x9A, 0xA8)), Margin = new Thickness(4, 0, 0, 10) };
            var placeholder = new TextBlock { Text = "Continuar…", FontSize = 13.5, Foreground = dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = red, VerticalAlignment = VerticalAlignment.Center };
            var hintText = new TextBlock { FontSize = 13.5, Foreground = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            var hint = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0), Visibility = Visibility.Collapsed, Children = { dot, hintText } };
            var scale = new ScaleTransform();
            var pulse = new System.Windows.Shapes.Ellipse { Fill = red, Opacity = 0, Margin = new Thickness(2), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale };
            var mic = new TextBlock { Text = "", FontFamily = iconFont, FontSize = 15, Foreground = text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var micBox = new Grid { Width = 34, Height = 34, Children = { pulse, mic } };
            var attach = new TextBlock { Text = "", FontFamily = iconFont, FontSize = 15, Foreground = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            var row = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(micBox, Dock.Right); DockPanel.SetDock(attach, Dock.Right);
            row.Children.Add(micBox); row.Children.Add(attach);
            row.Children.Add(new Grid { Children = { placeholder, hint } });
            var bar = new Border
            {
                CornerRadius = new CornerRadius(20), Background = Frozen(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Transparent, Padding = new Thickness(8, 4, 8, 4), Child = row
            };

            // Lo que se envía y la respuesta hablada
            var bubble = new Border
            {
                Background = Frozen(Color.FromRgb(0x2C, 0x2C, 0x33)), CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 7, 14, 7),
                HorizontalAlignment = HorizontalAlignment.Right, Visibility = Visibility.Hidden,
                Child = new TextBlock { Text = "¿Cómo va el despliegue de Hermes?", FontSize = 13.5, Foreground = text }
            };
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Controls.MochiView.SpeechLevel = () =>
            {
                var t = clock.Elapsed.TotalSeconds;
                return t % 2.2 > 1.9 ? 0 : Math.Max(0, Math.Sin(t * Math.PI * 6.4)) * (0.45 + 0.55 * Math.Abs(Math.Sin(t * 1.7)));
            };
            var mochi = new Controls.MochiView { Width = 46, Height = 46, BodyColor = Color.FromRgb(0xFF, 0x8A, 0x65), Interactive = false };
            var answer = new TextBlock
            {
                Text = "Falta la variable DB_HOST en pruebas; ya la añadí.", FontSize = 13.5, Foreground = text, TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Width = 330
            };
            var reply = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8), Visibility = Visibility.Hidden, Children = { mochi, answer } };

            var host = new Border
            {
                Background = NotchBg, Padding = new Thickness(18, 14, 18, 16), Width = 520,
                Child = new StackPanel { Children = { bubble, reply, caption, bar } }
            };
            var win = new Window { Content = host, SizeToContent = SizeToContent.WidthAndHeight };
            await ShowOffscreenAsync(win);

            void State(string say, bool listening, bool away = false, bool transcribing = false)
            {
                caption.Text = say;
                var busy = listening || transcribing;
                placeholder.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
                hint.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
                hintText.Text = transcribing ? "Transcribiendo…" : away ? "Suelta para cancelar" : "Escuchando… suelta para enviar";
                hintText.Opacity = away ? 0.6 : 1;
                dot.Visibility = listening && !away ? Visibility.Visible : Visibility.Collapsed;
                bar.BorderBrush = listening && !away ? red : Brushes.Transparent;
                mic.Foreground = listening && !away ? Brushes.White : text;
                if (!(listening && !away)) pulse.Opacity = 0;
            }
            async Task Listen(int ms)
            {
                // El halo late como en la app (650 ms de ida y vuelta)
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < ms)
                {
                    var k = (1 - Math.Cos(sw.ElapsedMilliseconds / 650.0 * Math.PI)) / 2;
                    pulse.Opacity = 0.85 - 0.4 * k;
                    scale.ScaleX = scale.ScaleY = 1 + 0.25 * k;
                    await Task.Delay(40);
                }
            }

            var n = await new GifWriter(host, NotchBg, 520).RecordAsync(path, async () =>
            {
                State("Mantén pulsado el micrófono para hablar", false);
                await Task.Delay(1600);
                State("Habla mientras lo mantienes pulsado…", true);
                await Listen(2400);
                State("Si alejas el cursor antes de soltar, se cancela", true, away: true);
                await Task.Delay(1800);
                State("…y suéltalo para enviar", true);
                await Listen(1300);
                State("…y suéltalo para enviar", false, transcribing: true);
                await Task.Delay(1000);
                bubble.Visibility = Visibility.Visible;
                State("Lo que pides hablando te lo responde en voz alta", false);
                await Task.Delay(600);
                reply.Visibility = Visibility.Visible;
                mochi.IsSpeaking = true;
                await Task.Delay(3200);
                mochi.IsSpeaking = false;
                await Task.Delay(800);
            });
            win.Close();
            Controls.MochiView.SpeechLevel = null;
            return n;
        }
    }
}
