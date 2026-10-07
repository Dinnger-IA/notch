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
    /// añade aquí su vista (o, si es de un plugin, regístrala con <see cref="Plugins.PluginRegistry.AddGifDemo"/>).
    /// </summary>
    public static class GifDemos
    {
        public static string Help => BuiltInHelp + string.Concat(Plugins.PluginHost.GifDemos.Select(d => $" · {d.Name} ({d.Description})"));

        private const string BuiltInHelp =
            "Vistas: notas[=X.Y.Z] (ventana de notas recorriendo las pestañas desde esa versión) · " +
            "actualizacion[=X.Y.Z] (tarjeta de versión nueva con las novedades de esa versión) · " +
            "tabla (respuesta del chat con tablas Markdown) · instalador (ventana del instalador) · " +
            "terminal (sesión de Claude Code de la terminal en una pestaña de codi-claude) · " +
            "aviso-panel (aviso de Claude Code de la terminal con el panel abierto) · " +
            "acciones (las 5 últimas acciones de un agente de código, la más reciente arriba) · " +
            "editor-color (los colores del editor de agentes, en una sola fila) · " +
            "sesion-ocupada (sesión de Codex abierta en otro programa y el botón para seguir en una nueva) · " +
            "git (panel de git de un workspace: preparar, el personaje escribe el commit, commit y push) · " +
            "mencion (escribir @ en el chat para citar otro workspace como contexto)";

        internal static readonly Brush NotchBg = Frozen(Color.FromRgb(0x0C, 0x0C, 0x0F));
        internal static readonly Brush WindowBg = Frozen(Color.FromRgb(0x20, 0x20, 0x20));

        public static async Task<int> RunAsync(string view, string path)
        {
            var (name, arg) = view.Split('=', 2) is [var n, var a] ? (n, a) : (view, "");
            Version.TryParse(arg, out var version);
            path = Path.GetFullPath(path);
            var plugin = Plugins.PluginHost.GifDemos.FirstOrDefault(d => d.Name == name);
            int frames = plugin != null ? await plugin.Record(path) : name switch
            {
                "notas" => await ReleaseNotesAsync(version, path),
                "actualizacion" => await UpdateCardAsync(version, path),
                "tabla" => await ChatTableAsync(path),
                "terminal" => await TerminalSessionAsync(path),
                "aviso-panel" => await PanelNoticeAsync(path),
                "acciones" => await RecentActionsAsync(path),
                "editor-color" => await EditorColorAsync(path),
                "sesion-ocupada" => await BusySessionAsync(path),
                "git" => await GitPanelAsync(path),
                "mencion" => await MentionAsync(path),
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

        /// <summary>La ventana del instalador, cambiando sus opciones (no instala nada).</summary>
        private static async Task<int> InstallerAsync(string path)
        {
            // Alto fijo: las etapas miden distinto y el GIF no admite cambios de tamaño
            var win = new InstallerWindow(uninstall: false, Installer.Version) { SizeToContent = SizeToContent.Manual, Height = 640 };
            var content = (FrameworkElement)win.Content;
            await ShowOffscreenAsync(win);
            var desktop = (CheckBox)win.FindName("DesktopOption");
            var next = (Button)win.FindName("MainButton");
            var click = System.Windows.Controls.Primitives.ButtonBase.ClickEvent;
            var n = await new GifWriter(content, Frozen(Color.FromRgb(0x14, 0x14, 0x18)), 560).RecordAsync(path, async () =>
            {
                // Etapas de los plugins (van primero): cada una hace su demostración y se pasa a la siguiente
                foreach (var demo in win.PluginStageDemos)
                {
                    await demo();
                    next.RaiseEvent(new RoutedEventArgs(click));
                }
                await Task.Delay(3200); // etapa Herramientas: estado real de Claude Code y Codex
                next.RaiseEvent(new RoutedEventArgs(click)); // → Opciones (no instala nada)
                await Task.Delay(1200);
                desktop.IsChecked = true; await Task.Delay(1800);
            });
            win.Close();
            return n;
        }

        /// <summary>Las últimas acciones de un agente de código: cada nueva entra arriba y las demás bajan difuminándose.</summary>
        private static async Task<int> RecentActionsAsync(string path)
        {
            // Misma fila que ActionLineTemplate de NotchWindow.xaml (icono, herramienta y detalle)
            var template = (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                              xmlns:v="clr-namespace:AgentManagerNotch.Views;assembly=AgentManagerNotch">
                    <Grid Margin="0,3">
                        <Grid.Resources><v:ToolActionConverter x:Key="ToolAction" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"/></Grid.Resources>
                        <Grid.ColumnDefinitions><ColumnDefinition Width="18"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                        <TextBlock Text="{Binding Text, Converter={StaticResource ToolAction}, ConverterParameter=icon}"
                                   FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" FontSize="10.5" Foreground="#8C8C99" Margin="0,2,0,0"/>
                        <StackPanel Grid.Column="1">
                            <TextBlock Text="{Binding Text, Converter={StaticResource ToolAction}, ConverterParameter=name}"
                                       FontSize="11" FontWeight="SemiBold" Foreground="#C8C8D2" TextTrimming="CharacterEllipsis"/>
                            <TextBlock Text="{Binding Text, Converter={StaticResource ToolAction}, ConverterParameter=detail}"
                                       FontFamily="Cascadia Mono, Consolas" FontSize="10" Foreground="#6E6E7C" TextTrimming="CharacterEllipsis"/>
                        </StackPanel>
                    </Grid>
                </DataTemplate>
                """);
            var actions = new[]
            {
                "Bash  npm test -- fechas", "Read  src/utils/fechas.ts", "Grep  formatearFecha", "Read  src/utils/zona.ts",
                "Edit  src/utils/fechas.ts", "Bash  npm test -- fechas", "Edit  test/fechas.test.ts", "Bash  npm test"
            }.Select(t => new Models.ChatMessage { Role = Models.ChatRole.Tool, Text = t }).ToList();

            var panel = new StackPanel { Width = 230 };
            var scroll = new ScrollViewer { Content = panel, Height = 190, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
            var claude = Color.FromRgb(0xD9, 0x77, 0x57);
            var head = new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10),
                Children =
                {
                    new Controls.MochiView { Width = 40, Height = 40, BodyColor = claude, State = Controls.MochiState.Working, Interactive = false },
                    new TextBlock { Text = "codi-claude", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Frozen(Color.FromRgb(0xEC, 0xEC, 0xF2)),
                                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) }
                }
            };
            var host = new Border { Background = NotchBg, Padding = new Thickness(16, 12, 16, 12), Child = new StackPanel { Children = { head, scroll } } };
            var win = new Window { Content = host, SizeToContent = SizeToContent.WidthAndHeight };
            await ShowOffscreenAsync(win);
            RecentActionList.Update(panel, actions.Take(2).Reverse().Cast<object>().ToList(), template, animate: false);
            var n = await new GifWriter(host, NotchBg, 262).RecordAsync(path, async () =>
            {
                await Task.Delay(700);
                for (int i = 3; i <= actions.Count; i++)
                {
                    RecentActionList.Update(panel, actions.Take(i).Reverse().Cast<object>().ToList(), template, animate: true);
                    await Task.Delay(900);
                }
                // Con scroll se ven las anteriores, ya nítidas
                await Task.Delay(500);
                RecentActionList.SetSharp(panel, true);
                for (int k = 1; k <= 12; k++) { scroll.ScrollToVerticalOffset(scroll.ScrollableHeight * k / 12); await Task.Delay(40); }
                await Task.Delay(1400);
            });
            win.Close();
            return n;
        }

        /// <summary>El editor de agentes eligiendo colores de la fila de muestras (agente nuevo, sin datos del usuario).</summary>
        private static async Task<int> EditorColorAsync(string path)
        {
            var win = new AgentEditorWindow(null) { Width = 780, Height = 520 };
            var content = (FrameworkElement)win.Content;
            await ShowOffscreenAsync(win);
            var swatches = ((Panel)win.FindName("Swatches")).Children.OfType<Border>().ToList();
            var n = await new GifWriter(content, WindowBg, 700).RecordAsync(path, async () =>
            {
                await Task.Delay(900);
                foreach (var i in new[] { 0, 3, 6, 8, 10, 14 })
                {
                    swatches[i].RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                        { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                    await Task.Delay(800);
                }
                await Task.Delay(600);
            });
            win.Close();
            return n;
        }

        /// <summary>
        /// La sesión de Codex sigue abierta en otro programa: el error lo explica y su botón continúa en una sesión nueva
        /// (maqueta del chat, como la plantilla Message de NotchWindow.xaml).
        /// </summary>
        private static async Task<int> BusySessionAsync(string path)
        {
            var text = Frozen(Color.FromRgb(0xEC, 0xEC, 0xF2));
            var dim = Frozen(Color.FromRgb(0x6E, 0x6E, 0x7C));
            Border Bubble(string s) => new()
            {
                Background = Frozen(Color.FromRgb(0x2C, 0x2C, 0x33)), CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 7, 14, 7),
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 6),
                Child = new TextBlock { Text = s, FontSize = 13.5, Foreground = text }
            };
            var button = new Button { Content = "Seguir en una sesión nueva", Padding = new Thickness(12, 5, 12, 5), FontSize = 12,
                                      Margin = new Thickness(0, 8, 0, 2), HorizontalAlignment = HorizontalAlignment.Left };
            if (Application.Current.TryFindResource("PillButton") is Style pill) button.Style = pill;
            var error = new Border
            {
                Background = (Brush)Application.Current.TryFindResource("ErrorBg") ?? Frozen(Color.FromRgb(0x3A, 0x1E, 0x22)),
                CornerRadius = new CornerRadius(16), Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 6, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 500,
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            FontSize = 12, TextWrapping = TextWrapping.Wrap,
                            Foreground = (Brush)Application.Current.TryFindResource("ErrorFg") ?? Frozen(Color.FromRgb(0xFF, 0x8A, 0x8A)),
                            Text = "Esa sesión de Codex sigue abierta en otro programa: la terminal, la app de Codex o su servicio de fondo, " +
                                   "que sigue en marcha aunque cierres la app. Codex solo deja usarla a uno a la vez.\n" +
                                   "Ciérrala allí y vuelve a enviar el mensaje, o sigue aquí en una sesión nueva (sin lo hablado antes)."
                        },
                        button
                    }
                }
            };
            var notice = new TextBlock { Text = "Sesión nueva: Codex no recuerda lo hablado antes en esta pestaña.", FontSize = 11, Foreground = dim,
                                         HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 3) };
            var again = Bubble("¿Es un problema de red?");
            var answer = MarkdownBlock("Sí: el error **invalid peer certificate** viene de la inspección TLS de la red, no del repositorio.", text);
            var chat = new StackPanel { Children = { Bubble("¿Es un problema de red?"), error } };
            var host = new Border { Background = NotchBg, Padding = new Thickness(16, 10, 16, 10), Width = 560, Height = 330, Child = chat };
            var win = new Window { Content = host, SizeToContent = SizeToContent.WidthAndHeight };
            await ShowOffscreenAsync(win);
            var n = await new GifWriter(host, NotchBg, 560).RecordAsync(path, async () =>
            {
                await Task.Delay(3200);
                button.Visibility = Visibility.Collapsed;
                chat.Children.Add(notice);
                chat.Children.Add(again);
                await Task.Delay(1100);
                chat.Children.Add(answer);
                await Task.Delay(2600);
            });
            win.Close();
            return n;
        }

        /// <summary>Con el panel abierto, el aviso de Claude Code de la terminal aparece flotando sobre la vista general.</summary>
        private static async Task<int> PanelNoticeAsync(string path)
        {
            var text = Frozen(Color.FromRgb(0xEC, 0xEC, 0xF2));
            var dim = Frozen(Color.FromRgb(0x8C, 0x8C, 0x99));
            var pane = Frozen(Color.FromRgb(0x12, 0x12, 0x17));
            var claude = Color.FromRgb(0xD9, 0x77, 0x57);

            // Panel abierto (maqueta): barra superior, tarjeta grande y chips
            var card = new Border
            {
                Background = pane, CornerRadius = new CornerRadius(18), Padding = new Thickness(16), Width = 300, Height = 170,
                Child = new StackPanel
                {
                    Children =
                    {
                        new Controls.MochiView { Width = 70, Height = 70, BodyColor = Color.FromRgb(0x8E, 0xB8, 0xFF), Interactive = false, HorizontalAlignment = HorizontalAlignment.Left },
                        new TextBlock { Text = "Mochi", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = text, Margin = new Thickness(0, 10, 0, 0) },
                        new TextBlock { Text = "Asistente general", FontSize = 12, Foreground = dim }
                    }
                }
            };
            var chips = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Width = 270, Margin = new Thickness(10, 0, 0, 0) };
            foreach (var (name, c) in new[] { ("codi-claude", claude), ("Doky", Color.FromRgb(0xFF, 0xB0, 0x20)), ("Clima", Color.FromRgb(0x5E, 0xC2, 0x9A)), ("Escritor", Color.FromRgb(0xC0, 0x8E, 0xFF)) })
                chips.Children.Add(new Border
                {
                    Background = pane, CornerRadius = new CornerRadius(14), Margin = new Thickness(4), Padding = new Thickness(8),
                    Child = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Children =
                        {
                            new Controls.MochiView { Width = 30, Height = 30, BodyColor = c, Interactive = false },
                            new TextBlock { Text = name, FontSize = 12, Foreground = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) }
                        }
                    }
                });
            var body = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 44, 0, 0), Children = { card, chips } };
            var top = new TextBlock { Text = "⌂   Agentes", FontSize = 12, Foreground = dim, Margin = new Thickness(6, 12, 0, 0), VerticalAlignment = VerticalAlignment.Top };

            // El aviso flotante, como PanelNotice en NotchWindow.xaml
            var notice = new Border
            {
                Background = Frozen(Color.FromRgb(0x2A, 0x2A, 0x31)), BorderBrush = Frozen(Color.FromRgb(0x3A, 0x3A, 0x44)), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 8, 14, 8), Margin = new Thickness(0, 44, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Opacity = 0,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.5, Color = Colors.Black },
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new Controls.MochiView { Width = 34, Height = 34, BodyColor = claude, State = Controls.MochiState.Waiting, Interactive = false },
                        new StackPanel
                        {
                            Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                            Children =
                            {
                                new TextBlock { Text = "Claude Code · mi-proyecto", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = text },
                                new TextBlock { Text = "Claude needs your permission to use Bash", FontSize = 11.5, Foreground = dim }
                            }
                        }
                    }
                }
            };
            var host = new Border { Background = NotchBg, Padding = new Thickness(14, 0, 14, 14), Child = new Grid { Children = { top, body, notice } } };
            var win = new Window { Content = host, SizeToContent = SizeToContent.WidthAndHeight };
            await ShowOffscreenAsync(win);
            var n = await new GifWriter(host, NotchBg, 600).RecordAsync(path, async () =>
            {
                await Task.Delay(1200);
                for (int i = 1; i <= 6; i++) { notice.Opacity = i / 6.0; await Task.Delay(30); }
                await Task.Delay(3500);
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

        /// <summary>
        /// El panel de git de un workspace: se preparan dos archivos, el personaje escribe el mensaje del commit al
        /// pulsarlo y se hace commit y push (datos de ejemplo; no toca ningún repositorio).
        /// </summary>
        private static async Task<int> GitPanelAsync(string path)
        {
            var text = Frozen(Color.FromRgb(0xEC, 0xEC, 0xF2));
            var dim = Frozen(Color.FromRgb(0x9A, 0x9A, 0xA8));
            var faint = Frozen(Color.FromRgb(0x6E, 0x6E, 0x7C));
            var claude = Color.FromRgb(0xD9, 0x77, 0x57);
            Border Pill(UIElement child, Color bg) => new()
            {
                Background = Frozen(bg), CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 4, 10, 4),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Child = child
            };
            TextBlock Branch(string count)
            {
                var t = new TextBlock { FontSize = 12, Foreground = text };
                t.Inlines.Add(new System.Windows.Documents.Run("⑂ ") { FontFamily = new FontFamily("Segoe UI Symbol"), Foreground = dim });
                t.Inlines.Add(new System.Windows.Documents.Run("main"));
                if (count != "") t.Inlines.Add(new System.Windows.Documents.Run(" " + count) { Foreground = Frozen(Color.FromRgb(0xFF, 0xB0, 0x20)) });
                return t;
            }

            var changes = new System.Collections.Generic.List<GitChange>
            {
                new("docs/zona-horaria.md", '?', '?', null),
                new("src/utils/fechas.ts", ' ', 'M', null),
                new("test/fechas.test.ts", ' ', 'M', null)
            };
            int ahead = 1;
            var list = new StackPanel();
            var topCount = new Border();
            var sync = new TextBlock { FontSize = 11.5, Foreground = dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            void Render()
            {
                var st = new GitStatus { Branch = "main", Upstream = "origin/main", Ahead = ahead, Changes = changes.OrderBy(c => c.Path).ToList() };
                NotchWindow.FillGitList(list, st, (_, _) => { }, null);
                topCount.Child = Pill(Branch(changes.Count > 0 ? $"●{changes.Count}" : ""), Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
                sync.Text = ahead > 0 ? $"↑{ahead}" : "";
            }
            void Stage(string p) { var i = changes.FindIndex(c => c.Path == p); changes[i] = changes[i] with { Index = 'M', WorkTree = ' ' }; Render(); }

            var mochi = new Controls.MochiView { Width = 46, Height = 46, BodyColor = claude, Interactive = false };
            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            top.Children.Add(new TextBlock { Text = "codi-claude", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            top.Children.Add(Pill(new TextBlock { Text = "mi-proyecto", FontSize = 12, Foreground = text }, Color.FromRgb(0x2C, 0x2C, 0x33)));
            top.Children.Add(topCount);

            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            head.Children.Add(Pill(Branch(""), Color.FromRgb(0x2A, 0x2A, 0x33)));
            head.Children.Add(sync);
            var message = new TextBlock { FontSize = 12.5, Foreground = text, TextWrapping = TextWrapping.Wrap, MinHeight = 52, Margin = new Thickness(2, 4, 0, 4) };
            const string hint = "Mensaje del commit… (pulsa el personaje para que el agente lo escriba)";
            var msgHost = new Border { CornerRadius = new CornerRadius(12), Background = Frozen(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 10, 0, 0), Child = message };
            var info = new TextBlock { FontSize = 11.5, Foreground = dim, VerticalAlignment = VerticalAlignment.Center };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(Pill(new TextBlock { Text = "✓  Commit", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = text }, Color.FromRgb(0x7C, 0x6C, 0xF0)));
            buttons.Children.Add(Pill(new TextBlock { Text = "↑  Push", FontSize = 12.5, Foreground = text }, Color.FromRgb(0x2E, 0x2E, 0x38)));
            var foot = new Grid { Margin = new Thickness(0, 8, 0, 0), Children = { info, buttons } };
            var panel = new Border
            {
                CornerRadius = new CornerRadius(16), Background = Frozen(Color.FromRgb(0x14, 0x14, 0x18)), BorderBrush = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1), Padding = new Thickness(12, 8, 12, 10), Height = 290,
                Child = new DockPanel { LastChildFill = true }
            };
            var dock = (DockPanel)panel.Child;
            DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(foot, Dock.Bottom); DockPanel.SetDock(msgHost, Dock.Bottom);
            dock.Children.Add(head); dock.Children.Add(foot); dock.Children.Add(msgHost); dock.Children.Add(list);

            var stage = new Grid { Width = 600 };
            stage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            stage.ColumnDefinitions.Add(new ColumnDefinition());
            var right = new StackPanel { Children = { top, panel } };
            Grid.SetColumn(right, 1);
            stage.Children.Add(mochi);
            stage.Children.Add(right);
            mochi.VerticalAlignment = VerticalAlignment.Top;
            var host = new Border { Background = NotchBg, Padding = new Thickness(14, 12, 16, 14), Child = stage };
            Render();
            message.Text = hint; message.Foreground = faint;

            var win = new Window { Content = host, SizeToContent = SizeToContent.WidthAndHeight };
            await ShowOffscreenAsync(win);
            var n = await new GifWriter(host, NotchBg, 630).RecordAsync(path, async () =>
            {
                await Task.Delay(1200);
                Stage("src/utils/fechas.ts"); await Task.Delay(900);
                Stage("test/fechas.test.ts"); await Task.Delay(1100);
                // Clic en el personaje: escribe el commit
                mochi.Poke();
                mochi.State = Controls.MochiState.Thinking;
                message.Text = "codi-claude está escribiendo el commit… (pulsa otra vez para cancelar)";
                await Task.Delay(2200);
                const string msg = "Usar UTC al formatear fechas\n\nEl test fallaba en CI por la zona horaria del servidor: formatearFecha\nahora trabaja en UTC y el test lo comprueba.";
                message.Foreground = text;
                for (int i = 6; i < msg.Length; i += 6) { message.Text = msg[..i]; await Task.Delay(40); }
                message.Text = msg;
                mochi.State = Controls.MochiState.Done;
                info.Text = "Mensaje listo para lo preparado.";
                await Task.Delay(2200);
                mochi.State = Controls.MochiState.Idle;
                info.Text = "Confirmando…"; await Task.Delay(600);
                changes.RemoveAll(c => c.IsStaged); ahead = 2; Render();
                message.Text = hint; message.Foreground = faint;
                info.Text = "Commit hecho."; await Task.Delay(1400);
                info.Text = "Haciendo push…"; await Task.Delay(900);
                ahead = 0; Render();
                info.Text = "Push hecho."; await Task.Delay(2200);
            });
            win.Close();
            return n;
        }

        /// <summary>Escribir «@» en el chat: la lista de los otros workspaces, elegir uno y el agente lo lee como contexto.</summary>
        private static async Task<int> MentionAsync(string path)
        {
            var text = Frozen(Color.FromRgb(0xEC, 0xEC, 0xF1));
            var dim = Frozen(Color.FromRgb(0x8A, 0x8A, 0x98));
            var faint = Frozen(Color.FromRgb(0x6E, 0x6E, 0x7C));
            var claude = Color.FromRgb(0xD9, 0x77, 0x57);
            Border Pill(string t, Color bg) => new()
            {
                Background = Frozen(bg), CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 4, 10, 4),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
                Child = new TextBlock { Text = t, FontSize = 12, Foreground = text }
            };

            var mochi = new Controls.MochiView { Width = 46, Height = 46, BodyColor = claude, Interactive = false, VerticalAlignment = VerticalAlignment.Top };
            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            top.Children.Add(new TextBlock { Text = "codi-claude", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            top.Children.Add(Pill("tienda-web", Color.FromRgb(0x2C, 0x2C, 0x33)));

            var chat = new StackPanel { MinHeight = 150 };
            var typed = new TextBlock { FontSize = 13.5, Foreground = text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            const string placeholder = "Continuar… (@ para citar otro workspace)";
            var input = new Border { CornerRadius = new CornerRadius(20), Background = Frozen(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)), Padding = new Thickness(14, 8, 14, 8), Child = typed };

            var workspaces = new[] { ("api-pagos", @"D:\Proyectos\api-pagos"), ("app-movil", @"D:\Proyectos\app-movil"), ("diseno-sistema", @"D:\Proyectos\diseno-sistema") };
            var rows = new System.Collections.Generic.List<Border>();
            var rowPanel = new StackPanel();
            foreach (var (name, folder) in workspaces)
            {
                var label = new TextBlock { FontSize = 12.5, Foreground = text };
                label.Inlines.Add(new System.Windows.Documents.Run("@") { Foreground = dim });
                label.Inlines.Add(new System.Windows.Documents.Run(name));
                var row = new Border
                {
                    CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 5, 8, 5),
                    Child = new StackPanel { Children = { label, new TextBlock { Text = folder, FontSize = 10.5, Foreground = faint } } }
                };
                rows.Add(row); rowPanel.Children.Add(row);
            }
            void Select(int i) { for (int k = 0; k < rows.Count; k++) rows[k].Background = k == i ? Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent; }
            var popup = new Border
            {
                Background = Frozen(Color.FromRgb(0x12, 0x12, 0x17)), BorderBrush = Frozen(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12), Padding = new Thickness(4), Width = 260, HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(60, 8, 0, 0), Visibility = Visibility.Hidden,
                Child = new StackPanel { Children = { new TextBlock { Text = "CITAR WORKSPACE", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = faint, Margin = new Thickness(8, 4, 8, 4) }, rowPanel } }
            };

            var stage = new Grid { Width = 600 };
            stage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            stage.ColumnDefinitions.Add(new ColumnDefinition());
            var right = new StackPanel { Children = { top, chat, input, popup } };
            Grid.SetColumn(right, 1);
            stage.Children.Add(mochi);
            stage.Children.Add(right);
            var host = new Border { Background = NotchBg, Padding = new Thickness(14, 12, 16, 14), Child = stage };

            void Bubble(string t, bool user)
            {
                chat.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(user ? 80 : 0, 0, user ? 0 : 40, 8),
                    HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    Background = Frozen(user ? Color.FromRgb(0x2C, 0x2C, 0x36) : Color.FromRgb(0x1A, 0x1A, 0x21)),
                    Child = new TextBlock { Text = t, FontSize = 13, Foreground = text, TextWrapping = TextWrapping.Wrap }
                });
            }
            void Tool(string t) => chat.Children.Add(new TextBlock { Text = t, FontSize = 11.5, Foreground = dim, Margin = new Thickness(4, 0, 0, 8) });
            void SetTyped(string t) { typed.Text = t == "" ? placeholder : t + "▏"; typed.Foreground = t == "" ? faint : text; }
            SetTyped("");

            var win = new Window { Content = host, SizeToContent = SizeToContent.WidthAndHeight };
            await ShowOffscreenAsync(win);
            var n = await new GifWriter(host, NotchBg, 630).RecordAsync(path, async () =>
            {
                await Task.Delay(900);
                const string first = "Usa el mismo cliente de pagos que ";
                for (int i = 2; i <= first.Length; i += 2) { SetTyped(first[..i]); await Task.Delay(35); }
                SetTyped(first + "@"); popup.Visibility = Visibility.Visible; Select(0);
                await Task.Delay(1000);
                Select(1); await Task.Delay(500);
                Select(0); await Task.Delay(700);
                popup.Visibility = Visibility.Hidden;
                const string withRef = first + "@api-pagos ";
                SetTyped(withRef); await Task.Delay(700);
                const string rest = "en el checkout";
                for (int i = 2; i <= rest.Length; i += 2) { SetTyped(withRef + rest[..i]); await Task.Delay(35); }
                SetTyped(withRef + rest); await Task.Delay(800);
                SetTyped("");
                Bubble(withRef + rest, user: true);
                mochi.State = Controls.MochiState.Working;
                await Task.Delay(700);
                Tool(@"Read  D:\Proyectos\api-pagos\src\cliente-pagos.ts"); await Task.Delay(800);
                Tool(@"Edit  src\checkout\pago.ts"); await Task.Delay(900);
                mochi.State = Controls.MochiState.Done;
                Bubble("Listo: el checkout usa ahora el ClientePagos de api-pagos, con sus mismos reintentos y tiempos de espera.", user: false);
                await Task.Delay(2600);
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

        internal static async Task ShowOffscreenAsync(Window win)
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

        internal static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    }
}
