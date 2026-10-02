using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentManagerNotch.Services;

namespace AgentManagerNotch.Views
{
    /// <summary>
    /// GIF de las notas de la versión: <c>AgentManagerNotch.exe --grabar-gif &lt;vista&gt; &lt;salida.gif&gt;</c>.
    /// Corre en un proceso aparte que no carga los datos del usuario ni toca el notch abierto: monta la vista en una
    /// ventana fuera de la pantalla, la graba con <see cref="GifWriter"/> y se cierra. Para un cambio visual nuevo,
    /// añade aquí su vista.
    /// </summary>
    public static class GifDemos
    {
        public const string Help =
            "Vistas: notas[=X.Y.Z] (ventana de notas recorriendo las pestañas desde esa versión) · " +
            "actualizacion[=X.Y.Z] (tarjeta de versión nueva con las novedades de esa versión) · " +
            "tabla (respuesta del chat con tablas Markdown)";

        private static readonly Brush NotchBg = Frozen(Color.FromRgb(0x0C, 0x0C, 0x0F));
        private static readonly Brush WindowBg = Frozen(Color.FromRgb(0x20, 0x20, 0x20));

        public static async Task<int> RunAsync(string view, string path)
        {
            var (name, arg) = view.Split('=', 2) is [var n, var a] ? (n, a) : (view, "");
            Version.TryParse(arg, out var version);
            path = Path.GetFullPath(path);
            int frames = name switch
            {
                "notas" => await ReleaseNotesAsync(version, path),
                "actualizacion" => await UpdateCardAsync(version, path),
                "tabla" => await ChatTableAsync(path),
                _ => throw new ArgumentException($"Vista desconocida «{name}». {Help}")
            };
            Log.Info($"GIF guardado: {path} ({frames} fotogramas, {new FileInfo(path).Length / 1024} KB)");
            return frames;
        }

        /// <summary>La ventana de notas, pasando por cada pestaña (y dejando ver su GIF si lo tiene).</summary>
        private static async Task<int> ReleaseNotesAsync(Version? from, string path)
        {
            var win = new ReleaseNotesWindow(from) { Width = 860, Height = 470 };
            var content = (FrameworkElement)win.Content;
            await ShowOffscreenAsync(win);
            var all = ReleaseNotes.All;
            var start = Math.Max(0, all.ToList().FindIndex(n => n.Version == (from ?? all[0].Version)));
            var order = all.Skip(start).Concat(all.Take(start)).ToList();
            var gif = new GifWriter(content, WindowBg, 800);
            var n = await gif.RecordAsync(path, async () =>
            {
                foreach (var note in order)
                {
                    win.Select(note.Version);
                    await Task.Delay(note.Gif != null ? 5000 : 2600);
                }
                win.Select(order[0].Version);
                await Task.Delay(1200);
            });
            win.Close();
            return n;
        }

        /// <summary>La tarjeta del agente de actualización con las novedades de una versión.</summary>
        private static async Task<int> UpdateCardAsync(Version? version, string path)
        {
            var all = ReleaseNotes.All;
            var note = all.FirstOrDefault(n => n.Version == version) ?? all[0];
            var previous = all.SkipWhile(n => n.Version >= note.Version).FirstOrDefault()?.Name ?? "anterior";
            var card = new UpdateCard { Width = 580 };
            card.Show(note.Name, UpdateService.NoteLines(note.Body),
                $"Tienes la {previous}. Se descarga, se compila y el notch se vuelve a abrir solo.");
            var host = new Border { Background = NotchBg, Padding = new Thickness(10), Child = card };
            var win = new Window { Content = host, SizeToContent = SizeToContent.WidthAndHeight };
            await ShowOffscreenAsync(win);
            var n = await new GifWriter(host, NotchBg, 760).RecordAsync(path, () => Task.Delay(4500));
            win.Close();
            return n;
        }

        /// <summary>Respuesta de un agente con tablas Markdown, como se ve en el chat.</summary>
        private static async Task<int> ChatTableAsync(string path)
        {
            const string md = """
                Aquí tienes el resumen de los agentes:

                | Agente | Estado | Descripción |
                |---|:---:|---|
                | Mochi | Activo | Asistente general que responde preguntas y redacta textos |
                | Doky | En pausa | Configuración inicial |
                | Codex | Activo | Agente de código con varias pestañas de trabajo |

                Y las tareas pendientes:

                | # | Tarea | Responsable | Fecha |
                |--:|---|---|---|
                | 1 | Revisar `config.json` | Walter | 03/10 |
                | 2 | Publicar la versión | Mochi | 04/10 |

                ¿Quieres más detalle de alguno?
                """;
            var question = new Border
            {
                Background = Frozen(Color.FromRgb(0x2C, 0x2C, 0x33)), CornerRadius = new CornerRadius(16),
                Padding = new Thickness(14, 7, 14, 7), HorizontalAlignment = HorizontalAlignment.Right,
                Child = new TextBlock { Text = "¿Cómo van los agentes?", FontSize = 13.5, Foreground = Frozen(Color.FromRgb(0xEC, 0xEC, 0xF2)) }
            };
            var answer = new RichTextBox { FontSize = 13.5, MaxWidth = 500, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Hidden };
            if (Application.Current.TryFindResource("SelectableDoc") is Style st) answer.Style = st;
            answer.Foreground = Frozen(Color.FromRgb(0xEC, 0xEC, 0xF2));
            System.Windows.Documents.Block.SetLineHeight(answer, 20);
            Markdown.SetText(answer, md);
            question.Margin = answer.Margin = new Thickness(0, 6, 0, 6);
            var host = new Border { Background = NotchBg, Padding = new Thickness(16, 10, 16, 10), Width = 560, Child = new StackPanel { Children = { question, answer } } };
            var win = new Window { Content = host, SizeToContent = SizeToContent.WidthAndHeight };
            await ShowOffscreenAsync(win);
            var n = await new GifWriter(host, NotchBg, 560).RecordAsync(path, async () =>
            {
                await Task.Delay(900);
                answer.Visibility = Visibility.Visible;
                await Task.Delay(4000);
            });
            win.Close();
            return n;
        }

        private static async Task ShowOffscreenAsync(Window win)
        {
            win.WindowStyle = WindowStyle.None;
            win.ShowInTaskbar = false;
            win.ShowActivated = false;
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.Left = SystemParameters.VirtualScreenLeft - 5000;
            win.Top = 0;
            win.Show();
            await Task.Delay(600); // carga, primer dibujo y animaciones en marcha
        }

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    }
}
