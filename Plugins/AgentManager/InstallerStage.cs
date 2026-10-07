using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using AgentManagerNotch.Controls;
using AgentManagerNotch.Models;
using AgentManagerNotch.Providers;
using AgentManagerNotch.Services;

namespace AgentManagerNotch.Plugins.AgentManager
{
    /// <summary>
    /// Lo que añade Agent Manager al instalador (todo detrás de la opción <c>UseAgentManager</c>):
    /// <list type="bullet">
    /// <item>Primera etapa «Agent Manager»: la opción <i>Usar Agent Manager</i>; si se marca, no se pasa de etapa sin
    /// pulsar <i>Comprobar conexión</i> y que todo esté bien (<see cref="AgentManagerCheck"/>), y cada fallo dice el
    /// motivo. Sin marcarla, se instala por el camino sin Agent Manager.</item>
    /// <item>Tras instalar: guarda la elección en config.json y, con Agent Manager, inicia sesión y añade su MCP a los
    /// CLI instalados (<see cref="McpSetup"/>).</item>
    /// </list>
    /// </summary>
    internal sealed class InstallerStage
    {
        private static readonly Brush OkBrush = Frozen(Color.FromRgb(0x4D, 0xD0, 0xA1));
        private static readonly Brush DimBrush = Frozen(Color.FromRgb(0x9A, 0x9A, 0xA8));
        private static readonly Brush TextBrush = Frozen(Color.FromRgb(0xEC, 0xEC, 0xF2));
        private static readonly ProviderKind[] McpClis = { ProviderKind.ClaudeCode, ProviderKind.Codex };

        private readonly IInstallerHost _w;
        private CheckBox _option = null!;
        private Button _checkButton = null!;
        private TextBlock _summary = null!;
        private StackPanel _steps = null!;
        /// <summary>Pasó la comprobación (se pierde al cambiar la opción).</summary>
        private bool _passed;

        public InstallerStage(IInstallerHost installer) { _w = installer; }
        private Brush ErrorBrush => (Brush)_w.FindResource("ErrorFg");

        public void Add()
        {
            _w.AddStage("Agent Manager", Build(), canContinue: () => _option.IsChecked != true || _passed, gifDemo: GifDemoAsync);
            _w.AddAfterInstall(AfterInstallAsync);
        }

        private Border Build()
        {
            _option = new CheckBox { Style = (Style)_w.FindResource("Switch"), HorizontalAlignment = HorizontalAlignment.Right, IsChecked = SavedUseAgentManager() ?? true };
            _option.Click += (_, _) => Reset();
            var head = new Grid();
            var title = new StackPanel { Margin = new Thickness(0, 0, 56, 0) };
            title.Children.Add(new TextBlock { Style = (Style)_w.FindResource("OptionTitle"), Text = "Usar Agent Manager" });
            title.Children.Add(new TextBlock
            {
                Style = (Style)_w.FindResource("OptionHint"),
                Text = "Inicio de sesión con tu cuenta, el asistente Doky (LLM de Agent Manager) y su MCP en los CLI. Sin él, los agentes trabajan solo con los CLI de este equipo (Claude Code, Codex…)."
            });
            head.Children.Add(title);
            head.Children.Add(_option);

            _checkButton = new Button { Style = (Style)_w.FindResource("PillButton"), Padding = new Thickness(14, 6, 14, 6) };
            _checkButton.Content = new TextBlock { Inlines = { new Run("") { FontFamily = (FontFamily)_w.FindResource("IconFont"), FontSize = 11 }, new Run("  Comprobar conexión") } };
            _checkButton.Click += async (_, _) => await RunCheckAsync();
            _summary = new TextBlock { FontSize = 12, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 300, Text = "Hay que comprobarlo antes de seguir." };
            var checkRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), Children = { _checkButton, _summary } };
            _steps = new StackPanel { Margin = new Thickness(2, 10, 0, 0) };
            var checkPanel = new StackPanel { Children = { checkRow, _steps } };
            checkPanel.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding(nameof(CheckBox.IsChecked))
            {
                Source = _option,
                Converter = new BooleanToVisibilityConverter()
            });

            var card = new Border { Style = (Style)_w.FindResource("StageCard"), Padding = new Thickness(16, 16, 16, 14), Child = new StackPanel { Children = { head, checkPanel } } };
            _w.StageArea.Children.Add(card);
            return card;
        }

        private void Reset()
        {
            _passed = false;
            _steps.Children.Clear();
            _summary.Text = "Hay que comprobarlo antes de seguir.";
            _summary.Foreground = DimBrush;
            _w.MochiState = MochiState.Idle;
            _w.RefreshNav();
        }

        private async Task RunCheckAsync()
        {
            _checkButton.IsEnabled = false;
            _steps.Children.Clear();
            _summary.Text = "Comprobando…";
            _summary.Foreground = DimBrush;
            _w.MochiState = MochiState.Thinking;
            try
            {
                var steps = await AgentManagerCheck.RunAsync();
                foreach (var s in steps) _steps.Children.Add(CheckRow(s.Name, s.Ok, s.Detail));
                _passed = steps.All(s => s.Ok);
                _w.MochiState = _passed ? MochiState.Done : MochiState.Error;
                _summary.Text = _passed ? "Todo correcto: ya puedes seguir."
                    : "No se puede seguir con Agent Manager hasta resolverlo (o desmarca la opción para instalar sin él).";
                _summary.Foreground = _passed ? OkBrush : ErrorBrush;
                Log.Info("Instalador: comprobación de Agent Manager " + (_passed ? "correcta" : "fallida: " +
                    string.Join(" | ", steps.Where(s => !s.Ok).Select(s => s.Detail))));
            }
            finally
            {
                _checkButton.IsEnabled = true;
                _w.RefreshNav();
            }
        }

        /// <summary>Una fila del resultado de la comprobación (icono, nombre y detalle).</summary>
        private FrameworkElement CheckRow(string name, bool ok, string detail)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock
            {
                Text = ok ? "" : "", FontFamily = (FontFamily)_w.FindResource("IconFont"), FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0), Foreground = ok ? OkBrush : ErrorBrush
            });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = name, FontSize = 13, Foreground = TextBrush });
            text.Children.Add(new TextBlock { Text = detail, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), Foreground = ok ? DimBrush : ErrorBrush });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            return row;
        }

        /// <summary>GIF del instalador: comprobación real (red, servidor y puerto del inicio de sesión) y, si falla, sin Agent Manager.</summary>
        private async Task GifDemoAsync()
        {
            await Task.Delay(1200);
            await RunCheckAsync();
            await Task.Delay(2500);
            if (!_passed)
            {
                // Si falla, se ve el motivo y que sin Agent Manager sí se puede seguir
                _option.IsChecked = false;
                Reset();
                await Task.Delay(1500);
            }
        }

        /// <summary>Tras copiar el programa: guarda la elección y, con Agent Manager, sesión y MCP.</summary>
        private async Task<bool> AfterInstallAsync()
        {
            bool useAm = _option.IsChecked == true;
            AgentManagerCheck.SaveChoice(useAm);
            if (!useAm)
            {
                _w.AddStep("Agent Manager").Set(null, "No se usa: los agentes trabajarán con los CLI del equipo. Se puede activar luego en ⚙ Configuración → Sesión.", info: true);
                return true;
            }

            // Sesión: la guardada si sigue valiendo; si no, se inicia en el navegador
            var auth = new AuthService();
            var session = _w.AddStep("Sesión de Agent Manager");
            session.Set(null, "Comprobando si ya hay una sesión…");
            await auth.RestoreAsync();
            if (!auth.IsAuthenticated)
            {
                var done = new TaskCompletionSource<bool>();
                session.Set(null, "Inicia sesión con tu cuenta para añadir el MCP de Agent Manager a tus CLI. Se abrirá el navegador.");
                _w.MochiState = MochiState.Waiting;
                var login = session.AddAction("Iniciar sesión");
                login.Click += async (_, _) =>
                {
                    login.IsEnabled = false;
                    session.Set(null, "Completa el inicio de sesión en el navegador…");
                    _w.MochiState = MochiState.Thinking;
                    var error = await auth.LoginAsync();
                    if (auth.IsAuthenticated) { done.TrySetResult(true); return; }
                    session.Set(false, error ?? "No se completó el inicio de sesión. Vuelve a intentarlo.");
                    _w.MochiState = MochiState.Error;
                    login.IsEnabled = true;
                };
                if (!await done.Task) return false;
                login.Visibility = Visibility.Collapsed;
            }
            session.Set(true, $"Sesión iniciada como {auth.UserName ?? "tu usuario"}.");
            _w.MochiState = MochiState.Working;

            // MCP de Agent Manager en cada CLI instalado
            var rows = McpClis.ToDictionary(c => c, c => _w.AddStep($"MCP de Agent Manager · {ProviderFactory.Label(c)}"));
            foreach (var r in rows.Values) r.Set(null, "Añadiendo…");
            var results = await McpSetup.EnsureAsync(auth.Token!);
            foreach (var (cli, row) in rows)
            {
                var r = results.FirstOrDefault(x => x.Cli == cli);
                switch (r?.Status)
                {
                    case McpSetup.Status.Installed: row.Set(true, "Añadido."); break;
                    case McpSetup.Status.Updated: row.Set(true, "Actualizado con tu sesión."); break;
                    case McpSetup.Status.AlreadyConfigured: row.Set(true, "Ya estaba configurado."); break;
                    case McpSetup.Status.NotInstalled: row.Set(null, "El CLI no está instalado: se añadirá al instalarlo y abrir el notch.", info: true); break;
                    case McpSetup.Status.Failed: row.Set(false, $"No se pudo añadir: {r.Error}"); break;
                    default: row.Set(false, "No se pudo revisar (mira el registro)."); break;
                }
            }
            return true;
        }

        /// <summary>Lo elegido en una instalación anterior (null si no hay configuración).</summary>
        private static bool? SavedUseAgentManager()
        {
            try
            {
                var path = System.IO.Path.Combine(ConfigStore.Root, "config.json");
                if (!System.IO.File.Exists(path)) return null;
                return System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(path))?["Settings"]?[AgentManagerPlugin.UseKey]?.GetValue<bool>() ?? true;
            }
            catch { return null; }
        }

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    }
}
