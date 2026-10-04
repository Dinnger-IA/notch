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
            "tabla (respuesta del chat con tablas Markdown) · voz (el agente leyendo su respuesta en voz alta) · instalador (ventana del instalador) · " +
            "terminal (sesión de Claude Code de la terminal en una pestaña de codi-claude)";

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
                "voz" => await VoiceAsync(path),
                "terminal" => await TerminalSessionAsync(path),
                "instalador" => await InstallerAsync(path),
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

        /// <summary>El agente leyendo en voz alta su respuesta: boca y ondas al ritmo de la voz.</summary>
        private static async Task<int> VoiceAsync(string path)
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

        /// <summary>La ventana del instalador, cambiando sus opciones (no instala nada).</summary>
        private static async Task<int> InstallerAsync(string path)
        {
            // Alto fijo: las etapas miden distinto y el GIF no admite cambios de tamaño
            var win = new InstallerWindow(uninstall: false, Installer.Version) { SizeToContent = SizeToContent.Manual, Height = 680 };
            var content = (FrameworkElement)win.Content;
            await ShowOffscreenAsync(win);
            var desktop = (CheckBox)win.FindName("DesktopOption");
            var next = (Button)win.FindName("MainButton");
            var click = System.Windows.Controls.Primitives.ButtonBase.ClickEvent;
            var n = await new GifWriter(content, Frozen(Color.FromRgb(0x14, 0x14, 0x18)), 560).RecordAsync(path, async () =>
            {
                // Etapa Agent Manager: comprobación real (red, servidor y puerto del inicio de sesión)
                await Task.Delay(1200);
                win.AmCheckButton.RaiseEvent(new RoutedEventArgs(click));
                await Task.Delay(4000);
                if (!next.IsEnabled)
                {
                    // Si falla, se ve el motivo y que sin Agent Manager sí se puede seguir
                    win.AmOption.IsChecked = false; win.AmOption.RaiseEvent(new RoutedEventArgs(click));
                    await Task.Delay(1500);
                }
                next.RaiseEvent(new RoutedEventArgs(click)); // → Herramientas
                await Task.Delay(3200); // estado real de Claude Code y Codex
                next.RaiseEvent(new RoutedEventArgs(click)); // → Opciones (no instala nada)
                await Task.Delay(1200);
                desktop.IsChecked = true; await Task.Delay(1800);
            });
            win.Close();
            return n;
        }

        /// <summary>
        /// Sesión de Claude Code de la terminal: llega el aviso y, al abrirlo, la sesión está en una pestaña de
        /// codi-claude (leída con <see cref="ClaudeTranscript"/> de una transcripción de ejemplo).
        /// </summary>
        private static async Task<int> TerminalSessionAsync(string path)
        {
            object Line(string type, object content, int min) => new
            {
                type, sessionId = "demo", timestamp = DateTime.Today.AddHours(10).AddMinutes(min).ToUniversalTime().ToString("o"),
                message = new { role = type, content }
            };
            var lines = new[]
            {
                Line("user", "Arregla el test de fechas que falla en CI", 0),
                Line("assistant", new object[] { new { type = "tool_use", name = "Bash", input = new { command = "npm test -- fechas" } } }, 1),
                Line("assistant", new object[] { new { type = "tool_use", name = "Read", input = new { file_path = "src/utils/fechas.ts" } } }, 1),
                Line("assistant", new object[] { new { type = "tool_use", name = "Edit", input = new { file_path = "src/utils/fechas.ts" } } }, 2),
                Line("assistant", new object[] { new { type = "text", text = "Listo: el test fallaba por la **zona horaria** del servidor de CI. Ahora `formatearFecha` usa UTC y los 14 tests pasan." } }, 3)
            };
            var file = Path.Combine(Path.GetTempPath(), "agent-manager-notch-demo-sesion.jsonl");
            File.WriteAllLines(file, lines.Select(l => System.Text.Json.JsonSerializer.Serialize(l)));
            var messages = ClaudeTranscript.Read(file) ?? new();
            File.Delete(file);

            var text = Frozen(Color.FromRgb(0xEC, 0xEC, 0xF2));
            var dim = Frozen(Color.FromRgb(0x8C, 0x8C, 0x99));
            var claude = Color.FromRgb(0xD9, 0x77, 0x57);

            // 1) El aviso del notch
            var banner = new Border
            {
                Background = Frozen(Color.FromRgb(0x1C, 0x1C, 0x22)), CornerRadius = new CornerRadius(18), Padding = new Thickness(14, 10, 18, 10),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 0, 0),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new Controls.MochiView { Width = 40, Height = 40, BodyColor = claude, State = Controls.MochiState.Done, Interactive = false },
                        new StackPanel
                        {
                            Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                            Children =
                            {
                                new TextBlock { Text = "Claude Code terminó", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = text },
                                new TextBlock { Text = "Sesión en mi-proyecto · clic para verla", FontSize = 12, Foreground = dim }
                            }
                        }
                    }
                }
            };

            // 2) La pestaña de codi-claude con la sesión
            var chat = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            header.Children.Add(new Controls.MochiView { Width = 34, Height = 34, BodyColor = claude, Interactive = false });
            header.Children.Add(new TextBlock { Text = "codi-claude", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 10, 0) });
            header.Children.Add(new Border
            {
                Background = Frozen(Color.FromRgb(0x2C, 0x2C, 0x33)), CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 3, 10, 3), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "mi-proyecto  ✓", FontSize = 12, Foreground = text }
            });
            chat.Children.Add(header);
            chat.Children.Add(new TextBlock { Text = AgentSession.TerminalNotice(), FontSize = 11.5, Foreground = dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            foreach (var m in messages)
            {
                FrameworkElement el = m.Role switch
                {
                    ChatRoleUser => new Border
                    {
                        Background = Frozen(Color.FromRgb(0x2C, 0x2C, 0x33)), CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 7, 14, 7),
                        HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 6),
                        Child = new TextBlock { Text = m.Text, FontSize = 13.5, Foreground = text }
                    },
                    ChatRoleTool => new TextBlock { Text = "›  " + m.Text, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11, Foreground = dim, Margin = new Thickness(2, 1, 0, 1) },
                    _ => MarkdownBlock(m.Text, text)
                };
                chat.Children.Add(el);
            }

            var stage = new Grid { Width = 560, Height = 330 };
            stage.Children.Add(banner);
            var chatHost = new Border { Child = chat, Visibility = Visibility.Hidden, Padding = new Thickness(8, 4, 8, 0) };
            stage.Children.Add(chatHost);
            var host = new Border { Background = NotchBg, Padding = new Thickness(12), Child = stage };
            var win = new Window { Content = host, SizeToContent = SizeToContent.WidthAndHeight };
            await ShowOffscreenAsync(win);
            var n = await new GifWriter(host, NotchBg, 580).RecordAsync(path, async () =>
            {
                await Task.Delay(2200);
                banner.Visibility = Visibility.Hidden;
                chatHost.Visibility = Visibility.Visible;
                await Task.Delay(4500);
            });
            win.Close();
            return n;
        }

        private const Models.ChatRole ChatRoleUser = Models.ChatRole.User, ChatRoleTool = Models.ChatRole.Tool;

        private static FrameworkElement MarkdownBlock(string md, Brush foreground)
        {
            var box = new RichTextBox { FontSize = 13.5, Margin = new Thickness(0, 6, 0, 0) };
            if (Application.Current.TryFindResource("SelectableDoc") is Style st) box.Style = st;
            box.Foreground = foreground;
            System.Windows.Documents.Block.SetLineHeight(box, 20);
            Markdown.SetText(box, md);
            return box;
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
