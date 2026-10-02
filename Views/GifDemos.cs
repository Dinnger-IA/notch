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
            "actualizacion[=X.Y.Z] (tarjeta de versión nueva con las novedades de esa versión)";

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
