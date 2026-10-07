using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;
using AgentManagerNotch.Controls;
using AgentManagerNotch.Models;
using AgentManagerNotch.Providers;
using AgentManagerNotch.Services;

namespace AgentManagerNotch.Plugins.AgentManager
{
    /// <summary>
    /// Agent Manager (<c>agent-manager.distelsa.net</c>): inicio de sesión antes de ver los agentes, su LLM como
    /// proveedor de agentes (modo <c>--llm</c>), su MCP en los CLI instalados, el asistente Doky en la primera
    /// configuración y la etapa del instalador con su comprobación.
    /// Todo depende del ajuste <c>UseAgentManager</c> (se elige en el instalador): sin él, el notch se comporta como
    /// el de main (no pide sesión, no instala el MCP, no ofrece este proveedor ni llama al servidor).
    /// </summary>
    public sealed class AgentManagerPlugin : INotchPlugin
    {
        public const string ProviderId = "AgentManager";
        public const string UseKey = "UseAgentManager";
        public const string RepoUrl = "https://dev.azure.com/grupodistelsa/innovation-sandbox/_git/agent-manager-notch";
        private static readonly Color McpColor = Color.FromRgb(0xD9, 0x77, 0x57);

        public string Id => "agent-manager";
        public string Name => "Agent Manager";

        /// <summary>Sesión con Agent Manager (solo en el arranque normal).</summary>
        public AuthService Auth { get; } = new();
        public NotchHost? Host { get; private set; }
        private LoginGate? _gate;
        private SessionCard? _card;

        /// <summary>Se usa Agent Manager (por defecto sí; se elige en el instalador).</summary>
        public bool Enabled => Host?.Get(UseKey, true) ?? (PluginHost.Host?.Get(UseKey, true) ?? true);
        private static bool EnabledIn(AppSettings s) => s.GetExtra(UseKey, true);

        // =============================================================== registro y línea de comandos
        public void Register(PluginRegistry registry)
        {
            // Esta rama se actualiza desde Azure DevOps (las copias sueltas se descargan de allí)
            UpdateService.RepoUrl = RepoUrl;
            registry.AddProvider(new PluginProvider(ProviderId, "Agent Manager", () => new AgentManagerProvider())
            {
                IsAvailable = () => PluginHost.Host?.Get(UseKey, true) ?? true,
                StatusText = "✓ Usa el LLM de Agent Manager con tu sesión (no necesita CLI)."
            });
        }

        /// <summary>Modo LLM: lo lanza un agente con proveedor Agent Manager (stdin = mensajes, stdout = eventos).</summary>
        public int? RunCommandLine(string[] args) => args.Length > 0 && args[0] == "--llm" ? LlmBridge.Run() : null;

        /// <summary>Con Agent Manager (salvo --sin-agent-manager) hay que pasar la comprobación, como en la ventana.</summary>
        public int? BeforeSilentInstall(string[] args)
        {
            bool useAm = !args.Contains("--sin-agent-manager");
            if (useAm)
            {
                var failed = AgentManagerCheck.RunAsync().GetAwaiter().GetResult().Where(s => !s.Ok).ToList();
                if (failed.Count > 0)
                {
                    var why = string.Join(" | ", failed.Select(s => $"{s.Name}: {s.Detail}"));
                    Log.Info("Instalación silenciosa cancelada, falló la comprobación de Agent Manager: " + why);
                    Console.Error.WriteLine("No se instaló. " + why);
                    return 3;
                }
            }
            AgentManagerCheck.SaveChoice(useAm);
            return null;
        }

        public void ConfigureInstaller(IInstallerHost installer) => new InstallerStage(installer).Add();

        // =============================================================== primera configuración
        /// <summary>
        /// Agentes iniciales. Con algún CLI instalado: Codi (código), Doky (chat con el LLM de Agent Manager) y
        /// Nube (chat), Codi y Nube con ese CLI. Sin CLI: Doky y Nube, los dos con el LLM de Agent Manager.
        /// Sin Agent Manager no hay Doky y Nube usa el CLI (Claude Code si no hay ninguno).
        /// </summary>
        public void ConfigureDefaultAgents(List<AgentProfile> agents, AppSettings settings)
        {
            bool useAm = EnabledIn(settings);
            var cli = FirstInstalledCli();
            agents.Clear();
            if (cli is ProviderKind c)
            {
                var codi = ConfigStore.NewCodeAgent(null, "Codi", "#4DD0A1");
                codi.Provider = c;
                agents.Add(codi);
            }
            if (useAm) agents.Add(new AgentProfile
            {
                Name = "Doky", ColorHex = "#64B5F6", Kind = AgentKind.Interactive,
                Provider = ProviderKind.Plugin, PluginProvider = ProviderId,
                Approval = ApprovalMode.ReadOnly,
                SystemPrompt = "Eres Doky, el asistente de Agent Manager. Respondes en español, de forma breve y clara. " +
                               "Ayudas con preguntas rápidas, redacción, ideas y recordatorios."
            });
            var nube = new AgentProfile
            {
                Name = "Nube", ColorHex = "#9C8CFF", Kind = AgentKind.Interactive,
                Provider = cli ?? (useAm ? ProviderKind.Plugin : ProviderKind.ClaudeCode),
                Approval = ApprovalMode.ReadOnly,
                SystemPrompt = "Eres Nube, un asistente personal de planificación. Ayudas a organizar el día, dividir tareas grandes en pasos y creas recordatorios cuando el usuario lo pide."
            };
            if (nube.Provider == ProviderKind.Plugin) nube.PluginProvider = ProviderId;
            agents.Add(nube);
            Log.Info("Primera configuración: " + string.Join(", ", agents.Select(a => $"{a.Name} ({ProviderFactory.Label(a)})")));
        }

        /// <summary>Primer CLI instalado (Claude Code, Codex o Gemini), o null si no hay ninguno.</summary>
        public static ProviderKind? FirstInstalledCli() =>
            new[] { ProviderKind.ClaudeCode, ProviderKind.Codex, ProviderKind.Gemini }
                .Cast<ProviderKind?>()
                .FirstOrDefault(k => CliResolver.Find(ProviderFactory.Executable(k!.Value)) != null);

        // =============================================================== arranque normal
        public void Start(NotchHost host)
        {
            Host = host;
            _gate = new LoginGate(Auth, () => Enabled);
            host.AddAccessGate(_gate);
            _card = new SessionCard(this);
            host.AddSettingsCard(_card, SettingsColumn.Right, _ => _card.Refresh());
            Auth.Changed += () => { if (Auth.Token is string t && Enabled) _ = EnsureMcpAsync(t); };
            // Sin Agent Manager (elegido en el instalador) no se restaura la sesión ni se pide iniciarla
            host.Started += () => { if (Enabled) _ = Auth.RestoreAsync(); };
        }

        /// <summary>Se instaló sin Agent Manager y ahora se quiere usar: se guarda y se pide la sesión.</summary>
        public async Task EnableAsync()
        {
            if (Host == null) return;
            Host.Set(UseKey, true);
            await Auth.RestoreAsync();
            _gate?.Raise();
            Host.RefreshSettings();
        }

        /// <summary>Tras validar la sesión: el MCP de Agent Manager en los CLI instalados (solo avisa si cambió algo).</summary>
        private async Task EnsureMcpAsync(string token)
        {
            var results = await McpSetup.EnsureAsync(token);
            var installed = results.Where(r => r.Status is McpSetup.Status.Installed or McpSetup.Status.Updated).Select(r => ProviderFactory.Label(r.Cli)).ToList();
            var failed = results.Where(r => r.Status == McpSetup.Status.Failed).Select(r => ProviderFactory.Label(r.Cli)).ToList();
            if (installed.Count == 0 && failed.Count == 0) return;
            var body = string.Join(" · ", new[]
            {
                installed.Count > 0 ? "Listo en " + string.Join(", ", installed) : null,
                failed.Count > 0 ? "Falló en " + string.Join(", ", failed) + " (ver registro)" : null
            }.Where(x => x != null));
            Host?.ShowBanner(McpColor, failed.Count > 0 ? MochiState.Error : MochiState.Done,
                "MCP de Agent Manager", body, sticky: failed.Count > 0);
        }
    }

    // =====================================================================
    // Agent Manager: el LLM de Agent Manager vía «AgentManagerNotch.exe --llm» (ver LlmBridge)
    // =====================================================================
    public class AgentManagerProvider : ICliProvider
    {
        public bool PlainText => false;

        public LaunchSpec Build(AgentProfile p, string prompt, string? sessionId, RunContext ctx)
        {
            // Sin sesión en el servidor: se manda la conversación reciente en cada petición.
            var messages = new List<object>();
            if (!string.IsNullOrWhiteSpace(ctx.FullSystemPrompt)) messages.Add(new { role = "system", content = ctx.FullSystemPrompt });
            var hist = ctx.History.Where(m => m.Role is ChatRole.User or ChatRole.Agent && !string.IsNullOrWhiteSpace(m.Text)).TakeLast(30).ToList();
            // El último mensaje del historial es el que se está enviando: va con el texto real del prompt
            foreach (var m in hist.Take(Math.Max(0, hist.Count - 1)))
                messages.Add(new { role = m.Role == ChatRole.User ? "user" : "assistant", content = m.Text });
            messages.Add(new { role = "user", content = prompt });

            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("No se encontró el ejecutable de Agent Manager Notch.");
            var psi = ProviderFactory.NewPsi(exe, p);
            psi.ArgumentList.Add("--llm");
            return new LaunchSpec { Psi = psi, StdinText = JsonSerializer.Serialize(new { model = p.Model, messages }) };
        }

        public IEnumerable<AgentEvent> Parse(string line)
        {
            JsonElement root;
            try { using var doc = JsonDocument.Parse(line); root = doc.RootElement.Clone(); }
            catch (JsonException) { yield break; }
            switch (ProviderFactory.Str(root, "type"))
            {
                case "delta":
                    yield return new DeltaEvent(ProviderFactory.Str(root, "text"));
                    break;
                case "result":
                    var ok = root.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
                    var err = ProviderFactory.Str(root, "error");
                    yield return new ResultEvent(ok, ok ? null : (err != "" ? err : "Agent Manager no respondió."), null);
                    break;
            }
        }
    }
}
