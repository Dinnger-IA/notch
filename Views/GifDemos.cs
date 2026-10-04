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
            "tabla (respuesta del chat con tablas Markdown) · voz (el agente leyendo su respuesta en voz alta) · " +
            "microfono (hablar manteniendo pulsado el micrófono) · instalador (ventana del instalador) · " +
            "terminal (sesión de Claude Code de la terminal en una pestaña de codi-claude) · " +
            "aviso-panel (aviso de Claude Code de la terminal con el panel abierto) · " +
            "acciones (las 5 últimas acciones de un agente de código, la más reciente arriba) · " +
            "editor-color (los colores del editor de agentes, en una sola fila)";

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
                "microfono" => await PushToTalkAsync(path),
                "terminal" => await TerminalSessionAsync(path),
                "aviso-panel" => await PanelNoticeAsync(path),
                "acciones" => await RecentActionsAsync(path),
                "editor-color" => await EditorColorAsync(path),
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

        /// <summary>
        /// Hablar manteniendo pulsado el micrófono: la caja de entrada (como InputBar de NotchWindow.xaml) en reposo,
        /// escuchando, a punto de cancelar, transcribiendo y, al enviar, el agente respondiendo en voz alta.
        /// </summary>
        private static async Task<int> PushToTalkAsync(string path)
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
