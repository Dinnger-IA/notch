using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentManagerNotch.Controls;
using AgentManagerNotch.Models;
using AgentManagerNotch.Providers;
using AgentManagerNotch.Services;

namespace AgentManagerNotch.Views
{
    /// <summary>
    /// Lo que añade la rama agent-manager al instalador (todo detrás de la bandera <c>UseAgentManager</c>):
    /// <list type="bullet">
    /// <item>Primera etapa «Agent Manager»: la opción <i>Usar Agent Manager</i>; si se marca, no se pasa de etapa sin
    /// pulsar <i>Comprobar conexión</i> y que todo esté bien (<see cref="AgentManagerCheck"/>), y cada fallo dice el
    /// motivo. Sin marcarla, se instala por el camino sin Agent Manager.</item>
    /// <item>Tras instalar: guarda la elección en config.json y, con Agent Manager, inicia sesión y añade su MCP a los
    /// CLI instalados (<see cref="McpSetup"/>).</item>
    /// </list>
    /// </summary>
    public partial class InstallerWindow
    {
        internal CheckBox AmOption = null!;
        internal Button AmCheckButton = null!;
        private TextBlock _amSummary = null!;
        private StackPanel _amSteps = null!;
        /// <summary>Pasó la comprobación (se pierde al cambiar la opción).</summary>
        private bool _amPassed;

        partial void ConfigureStages()
        {
            _stages.Insert(0, new Stage("Agent Manager", BuildAmStage(), CanContinue: () => AmOption.IsChecked != true || _amPassed));
            _afterInstall.Add(AfterInstallAsync);
        }

        private Border BuildAmStage()
        {
            AmOption = new CheckBox { Style = (Style)FindResource("Switch"), HorizontalAlignment = HorizontalAlignment.Right, IsChecked = SavedUseAgentManager() ?? true };
            AmOption.Click += (_, _) => ResetAmCheck();
            var head = new Grid();
            var title = new StackPanel { Margin = new Thickness(0, 0, 56, 0) };
            title.Children.Add(new TextBlock { Style = (Style)FindResource("OptionTitle"), Text = "Usar Agent Manager" });
            title.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("OptionHint"),
                Text = "Inicio de sesión con tu cuenta, el asistente Doky (LLM de Agent Manager) y su MCP en los CLI. Sin él, los agentes trabajan solo con los CLI de este equipo (Claude Code, Codex…)."
            });
            head.Children.Add(title);
            head.Children.Add(AmOption);

            AmCheckButton = new Button { Style = (Style)FindResource("PillButton"), Padding = new Thickness(14, 6, 14, 6) };
            AmCheckButton.Content = new TextBlock { Inlines = { new System.Windows.Documents.Run("") { FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11 }, new System.Windows.Documents.Run("  Comprobar conexión") } };
            AmCheckButton.Click += async (_, _) => await RunAmCheckAsync();
            _amSummary = new TextBlock { FontSize = 12, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 300, Text = "Hay que comprobarlo antes de seguir." };
            var checkRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), Children = { AmCheckButton, _amSummary } };
            _amSteps = new StackPanel { Margin = new Thickness(2, 10, 0, 0) };
            var checkPanel = new StackPanel { Children = { checkRow, _amSteps } };
            checkPanel.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(CheckBox.IsChecked))
            {
                Source = AmOption,
                Converter = new BoolToVisibilityConverter()
            });

            var card = new Border { Style = (Style)FindResource("StageCard"), Padding = new Thickness(16, 16, 16, 14), Child = new StackPanel { Children = { head, checkPanel } } };
            StageArea.Children.Add(card);
            return card;
        }

        private void ResetAmCheck()
        {
            _amPassed = false;
            _amSteps.Children.Clear();
            _amSummary.Text = "Hay que comprobarlo antes de seguir.";
            _amSummary.Foreground = DimBrush;
            Mochi.State = MochiState.Idle;
            RefreshNav();
        }

        private async Task RunAmCheckAsync()
        {
            AmCheckButton.IsEnabled = false;
            _amSteps.Children.Clear();
            _amSummary.Text = "Comprobando…";
            _amSummary.Foreground = DimBrush;
            Mochi.State = MochiState.Thinking;
            try
            {
                var steps = await AgentManagerCheck.RunAsync();
                foreach (var s in steps)
                {
                    var row = new StepRow(this, s.Name);
                    row.Set(s.Ok, s.Detail);
                    _amSteps.Children.Add(row.Root);
                }
                _amPassed = steps.All(s => s.Ok);
                Mochi.State = _amPassed ? MochiState.Done : MochiState.Error;
                _amSummary.Text = _amPassed ? "Todo correcto: ya puedes seguir."
                    : "No se puede seguir con Agent Manager hasta resolverlo (o desmarca la opción para instalar sin él).";
                _amSummary.Foreground = _amPassed ? OkBrush : ErrorBrush;
                Log.Info("Instalador: comprobación de Agent Manager " + (_amPassed ? "correcta" : "fallida: " +
                    string.Join(" | ", steps.Where(s => !s.Ok).Select(s => s.Detail))));
            }
            finally
            {
                AmCheckButton.IsEnabled = true;
                RefreshNav();
            }
        }

        /// <summary>Tras copiar el programa: guarda la elección y, con Agent Manager, sesión y MCP.</summary>
        private async Task<bool> AfterInstallAsync()
        {
            bool useAm = AmOption.IsChecked == true;
            AgentManagerCheck.SaveChoice(useAm);
            if (!useAm)
            {
                AddStep("Agent Manager").Set(null, "No se usa: los agentes trabajarán con los CLI del equipo. Se puede activar luego en ⚙ Configuración → Sesión.", info: true);
                return true;
            }

            // Sesión: la guardada si sigue valiendo; si no, se inicia en el navegador
            var auth = new AuthService();
            var session = AddStep("Sesión de Agent Manager");
            session.Set(null, "Comprobando si ya hay una sesión…");
            await auth.RestoreAsync();
            if (!auth.IsAuthenticated)
            {
                var done = new TaskCompletionSource<bool>();
                session.Set(null, "Inicia sesión con tu cuenta para añadir el MCP de Agent Manager a tus CLI. Se abrirá el navegador.");
                Mochi.State = MochiState.Waiting;
                var login = session.AddAction("Iniciar sesión");
                login.Click += async (_, _) =>
                {
                    login.IsEnabled = false;
                    session.Set(null, "Completa el inicio de sesión en el navegador…");
                    Mochi.State = MochiState.Thinking;
                    var error = await auth.LoginAsync();
                    if (auth.IsAuthenticated) { done.TrySetResult(true); return; }
                    session.Set(false, error ?? "No se completó el inicio de sesión. Vuelve a intentarlo.");
                    Mochi.State = MochiState.Error;
                    login.IsEnabled = true;
                };
                if (!await done.Task) return false;
                login.Visibility = Visibility.Collapsed;
            }
            session.Set(true, $"Sesión iniciada como {auth.UserName ?? "tu usuario"}.");
            Mochi.State = MochiState.Working;

            // MCP de Agent Manager en cada CLI instalado
            var rows = McpClis.ToDictionary(c => c, c => AddStep($"MCP de Agent Manager · {ProviderFactory.Label(c)}"));
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

        private static readonly ProviderKind[] McpClis = { ProviderKind.ClaudeCode, ProviderKind.Codex };

        /// <summary>Lo elegido en una instalación anterior (null si no hay configuración).</summary>
        private static bool? SavedUseAgentManager()
        {
            try
            {
                var path = System.IO.Path.Combine(ConfigStore.Root, "config.json");
                if (!System.IO.File.Exists(path)) return null;
                return System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(path))?["Settings"]?["UseAgentManager"]?.GetValue<bool>() ?? true;
            }
            catch { return null; }
        }
    }
}
