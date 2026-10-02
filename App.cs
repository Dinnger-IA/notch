using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AgentManagerNotch.Controls;
using AgentManagerNotch.Models;
using AgentManagerNotch.Providers;
using AgentManagerNotch.Services;
using AgentManagerNotch.Views;

namespace AgentManagerNotch
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            // Modo hook: lo invoca Claude Code antes de usar una herramienta (no abre ventanas).
            if (args.Length > 0 && args[0] == "--hook") return HookBridge.Run();
            // Modo LLM: lo lanza un agente con proveedor Agent Manager (stdin = mensajes, stdout = eventos).
            if (args.Length > 0 && args[0] == "--llm") return LlmBridge.Run();
            // Comandos de control: --notify, --ask, --claude-event
            if (ControlChannel.TryRunCli(args) is int code) return code;

            using var mutex = new Mutex(true, "AgentManagerNotch.SingleInstance", out bool first);
            if (!first)
            {
                // Ya hay una instancia: le pedimos que se muestre
                if (!ControlChannel.Send(new { cmd = "show" }))
                    MessageBox.Show("Agent Manager Notch ya se está ejecutando (busca el icono en la bandeja o pulsa Ctrl+Alt+Espacio).",
                        "Agent Manager Notch", MessageBoxButton.OK, MessageBoxImage.Information);
                return 0;
            }
            var app = new App();
            return app.Run();
        }
    }

    public class App : Application
    {
        public new static App Current => (App)Application.Current;

        public ConfigStore Store { get; } = new();
        /// <summary>Agentes tal como se ven en la interfaz (los de código agrupan sus pestañas).</summary>
        public ObservableCollection<AgentEntry> Agents { get; } = new();
        /// <summary>Todas las sesiones (una por agente, o una por pestaña en los de código).</summary>
        public ObservableCollection<AgentSession> Sessions { get; } = new();
        public ApprovalServer Approvals { get; } = new();
        /// <summary>Sesión con Agent Manager: sin ella el notch solo muestra el inicio de sesión.</summary>
        public AuthService Auth { get; } = new();
        /// <summary>Comprueba cada 2 horas si hay una versión nueva en el repositorio.</summary>
        public UpdateService Updates { get; } = new();
        public ReminderService Reminders { get; private set; } = null!;
        public ScheduleService Scheduler { get; private set; } = null!;
        public NotificationService Notifier { get; private set; } = null!;
        public NotchWindow Notch { get; private set; } = null!;
        public string HookSettingsPath { get; private set; } = "";
        private readonly CancellationTokenSource _cts = new();
        private static readonly System.Windows.Media.Color ClaudeColor = System.Windows.Media.Color.FromRgb(0xD9, 0x77, 0x57);

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ThemeMode = ThemeMode.Dark; // Controles Fluent nativos de Windows 11
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/AgentManagerNotch;component/Themes/Theme.xaml")
            });

            DispatcherUnhandledException += (_, ex) =>
            {
                Log.Error("Excepción no controlada", ex.Exception);
                ex.Handled = true;
            };
            TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("Tarea no observada", ex.Exception); ex.SetObserved(); };

            Store.Load();
            MigrateConfig();
            Store.Save();
            HookSettingsPath = HookBridge.WriteClaudeHookSettings();

            foreach (var p in Store.Config.Agents) Agents.Add(CreateEntry(p));
            ApplyDefault();
            Agents.CollectionChanged += (_, _) => ApplyDefault();

            Approvals.Handler = req => Dispatcher.InvokeAsync(() =>
            {
                var s = Sessions.FirstOrDefault(x => x.Key == req.AgentId);
                return s != null ? s.RequestApproval(req) : Task.FromResult(ApprovalDecision.Deny);
            }).Task.Unwrap();
            Approvals.Start();

            Notifier = new NotificationService(() => Store.Config.Settings);
            NotificationService.MigrateStartupEntry();
            ControlChannel.RefreshClaudeIntegrationPath();
            Notifier.ShowRequested += () => Notch.Expand(focusInput: true);
            Notifier.NotificationClicked += key => Notch.Expand(focusInput: true, sessionKey: key);
            Notifier.NewAgentRequested += () => Notch.OpenEditor(null);
            Notifier.RemindersRequested += () => Notch.OpenReminders();
            Notifier.ExitRequested += ExitApp;

            Reminders = new ReminderService(Store);
            Reminders.Fired += OnReminderFired;
            Scheduler = new ScheduleService(() => Sessions, SaveAgents);

            Notch = new NotchWindow();
            Notch.Show();
            Auth.Changed += Notch.OnAuthChanged;
            Auth.Changed += () => { if (Auth.Token is string t) _ = EnsureMcpAsync(t); };
            _ = Auth.RestoreAsync();
            Updates.Changed += Notch.RefreshUpdateInfo;
            Updates.Found += () =>
            {
                Notch.ShowUpdateBanner();
                Notifier.Toast("Nueva versión de Agent Manager Notch", "Abre el notch → ⚙ Configuración → Actualizar.", null);
            };
            Updates.Start();
            Reminders.Start();
            Scheduler.Start();
            ControlChannel.StartServer(msg => Dispatcher.BeginInvoke(() => HandleControl(msg)), _cts.Token);
            Log.Info("Agent Manager Notch iniciado");
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
            Notch.ShowBannerRaw(ClaudeColor, failed.Count > 0 ? MochiState.Error : MochiState.Done,
                "MCP de Agent Manager", body, null, sticky: failed.Count > 0);
        }

        // =============================================================== agentes, pestañas y sesiones
        private AgentEntry CreateEntry(AgentProfile p)
        {
            var entry = new AgentEntry(p);
            if (p.Kind == AgentKind.Code)
            {
                foreach (var ws in p.Workspaces) AddSession(entry, CreateSession(p, ws));
            }
            else AddSession(entry, CreateSession(p, null));
            entry.RaiseAll();
            return entry;
        }

        private void AddSession(AgentEntry entry, AgentSession s)
        {
            entry.Tabs.Add(s);
            Sessions.Add(s);
        }

        private AgentSession CreateSession(AgentProfile p, WorkspaceInfo? ws)
        {
            AgentSession? s = null;
            s = new AgentSession(p, Store, () => BuildContext(s!), ws);
            s.TurnFinished += OnTurnFinished;
            s.ApprovalRequested += OnApprovalRequested;
            s.ReminderCreated += (_, r) => Reminders.Add(r);
            return s;
        }

        public AgentEntry? EntryOf(AgentSession s) => Agents.FirstOrDefault(a => a.Tabs.Contains(s));

        private RunContext BuildContext(AgentSession s)
        {
            var now = DateTime.Now;
            var addendum =
                "\n\n---\n[Contexto de Agent Manager Notch]\n" +
                $"Eres «{s.Profile.Name}», un agente que vive en el notch de Agent Manager Notch en Windows. " +
                $"Fecha y hora local: {now:yyyy-MM-dd HH:mm} ({TimeZoneInfo.Local.DisplayName}).\n" +
                "Si el usuario te pide que le recuerdes algo, incluye en tu respuesta una línea con el formato exacto " +
                "[[reminder: <cuándo> | <texto del aviso>]]. Si te pide hacer algo tú mismo más tarde (o de forma periódica), usa " +
                "[[task: <cuándo> | <instrucción que debes ejecutar>]]. <cuándo> puede ser una duración (10m, 2h, 1d) o una " +
                "fecha-hora local ISO (2026-10-02T09:00). Agent Manager Notch elimina la etiqueta y programa el aviso; confirma brevemente al usuario.";
            if (s.IsCode)
                addendum += $"\nEres un agente de código. Tu carpeta de trabajo (workspace) es «{s.WorkDir}»: trabaja solo dentro de ella.";
            else if (s.IsScheduled)
                addendum += "\nEsta es una ejecución PROGRAMADA y automática: nadie va a responder preguntas. Haz la tarea de principio a fin.\n" +
                            "FORMATO DE LA RESPUESTA FINAL (Agent Manager Notch la muestra en una tarjeta): la PRIMERA línea es un titular muy corto " +
                            "(máximo 40 caracteres) con el dato principal, p. ej. «22 °C · Soleado» o «3 PRs pendientes»; después, de 1 a 3 " +
                            "líneas cortas (una idea por línea, sin viñetas ni Markdown). Nada más.";
            if (s.IsConversational)
                addendum += "\nPROGRESO: Agent Manager Notch muestra al usuario una lista con tus pasos. Si la petición tiene 2 o más pasos, " +
                            "escribe al empezar tu plan en este formato exacto (en su propio mensaje, antes de usar herramientas):\n" +
                            "[[tasks]]\n- [~] primer paso (en curso)\n- [ ] siguiente paso\n[[/tasks]]\n" +
                            "Cada vez que termines un paso, vuelve a escribir el bloque completo actualizado: [x] hecho, [~] en curso, [ ] pendiente. " +
                            "Agent Manager Notch oculta el bloque del chat, así que no lo menciones. Para peticiones simples de un paso no lo uses.";
            return new RunContext
            {
                AgentId = s.Key,
                AgentName = s.Profile.Name,
                PipeName = Approvals.PipeName,
                HookSettingsPath = HookSettingsPath,
                FullSystemPrompt = (s.Profile.SystemPrompt ?? "").Trim() + addendum,
                History = s.Messages.ToList()
            };
        }

        public void SaveAgents()
        {
            Store.Config.Agents = Agents.Select(x => x.Profile).ToList();
            Store.Save();
        }

        private void MigrateConfig()
        {
            var st = Store.Config.Settings;
            // Agente de código por defecto (solo una vez)
            if (!st.SeededCodeAgent)
            {
                if (!Store.Config.Agents.Any(a => a.Kind == AgentKind.Code))
                {
                    var code = ConfigStore.NewCodeAgent(null, "Código", "#FF9E5E");
                    Store.Config.Agents.Insert(0, code);
                    st.DefaultAgentId = code.Id;
                }
                st.SeededCodeAgent = true;
            }
            // Proyecto renombrado: las carpetas que apuntaban a la carpeta vieja pasan a la nueva
            foreach (var a in Store.Config.Agents)
            {
                a.WorkingDirectory = FixRenamedFolder(a.WorkingDirectory);
                foreach (var w in a.Workspaces) w.Folder = FixRenamedFolder(w.Folder);
            }
            // Versión anterior: un agente de código por carpeta → ahora la carpeta es un workspace (pestaña)
            foreach (var a in Store.Config.Agents.Where(a => a.Kind == AgentKind.Code))
            {
                if (!string.IsNullOrWhiteSpace(a.WorkingDirectory) && a.Workspaces.Count == 0)
                    a.Workspaces.Add(new WorkspaceInfo { Id = WorkspaceInfo.LegacyId, Folder = a.WorkingDirectory });
                a.WorkingDirectory = "";
            }
        }

        /// <summary>Si la carpeta ya no existe pero sí con el nombre nuevo del proyecto, devuelve la nueva.</summary>
        private static string FixRenamedFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || System.IO.Directory.Exists(folder)) return folder;
            var renamed = System.Text.RegularExpressions.Regex.Replace(folder, @"\\CoucouWin(?=\\|$)", @"\agent-manager-notch",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return renamed != folder && System.IO.Directory.Exists(renamed) ? renamed : folder;
        }

        /// <summary>Marca el agente principal; si no existe, el primero pasa a serlo.</summary>
        private void ApplyDefault()
        {
            var st = Store.Config.Settings;
            if (Agents.Count > 0 && !Agents.Any(x => x.Profile.Id == st.DefaultAgentId))
                st.DefaultAgentId = Agents[0].Profile.Id;
            foreach (var x in Agents)
            {
                x.IsDefault = x.Profile.Id == st.DefaultAgentId;
                foreach (var t in x.Tabs) t.IsDefault = x.IsDefault;
            }
        }

        public void SetDefault(AgentEntry e)
        {
            Store.Config.Settings.DefaultAgentId = e.Profile.Id;
            ApplyDefault();
            Store.Save();
        }

        public AgentEntry AddAgent(AgentProfile p)
        {
            var e = CreateEntry(p);
            Agents.Add(e);
            foreach (var s in e.Tabs.Where(x => x.IsScheduled)) ScheduleService.Reschedule(s);
            SaveAgents();
            return e;
        }

        /// <summary>
        /// Abre un workspace (pestaña) para una carpeta en un agente de código: el indicado, el principal si es de
        /// código, o el primero de código (se crea uno si no hay). Si ya hay una pestaña con esa carpeta, la devuelve.
        /// </summary>
        public AgentSession AddWorkspace(string folder, AgentEntry? target = null)
        {
            folder = System.IO.Path.GetFullPath(folder).TrimEnd('\\');
            var existing = Sessions.FirstOrDefault(s => s.IsCode && s.Workspace != null &&
                string.Equals(System.IO.Path.GetFullPath(s.WorkDir).TrimEnd('\\'), folder, StringComparison.OrdinalIgnoreCase)
                && (target == null || target.Tabs.Contains(s)));
            if (existing != null) return existing;

            target ??= Agents.FirstOrDefault(a => a.IsDefault && a.IsCode) ?? Agents.FirstOrDefault(a => a.IsCode)
                       ?? AddAgent(ConfigStore.NewCodeAgent(null, "Código", "#FF9E5E"));
            var ws = new WorkspaceInfo { Folder = folder };
            target.Profile.Workspaces.Add(ws);
            var s = CreateSession(target.Profile, ws);
            AddSession(target, s);
            target.ActiveTab = s;
            SaveAgents();
            s.AddNotice($"📁 Workspace: {folder}");
            return s;
        }

        /// <summary>Cierra una pestaña de un agente de código (detiene su CLI y borra su historial).</summary>
        public void CloseWorkspace(AgentSession s)
        {
            var entry = EntryOf(s);
            s.Cancel();
            if (entry != null)
            {
                entry.Tabs.Remove(s);
                if (s.Workspace != null) entry.Profile.Workspaces.RemoveAll(w => w.Id == s.Workspace.Id);
            }
            Sessions.Remove(s);
            Store.DeleteHistory(s.Key);
            SaveAgents();
        }

        /// <summary>
        /// Aplica cambios del editor. Si cambia el tipo de agente se recrea (sus pestañas cambian de forma);
        /// si cambia el CLI, las conversaciones empiezan de nuevo.
        /// </summary>
        public AgentEntry UpdateAgent(AgentEntry e, AgentProfile p)
        {
            if (e.Profile.Kind != p.Kind)
            {
                int idx = Agents.IndexOf(e);
                foreach (var s in e.Tabs.ToList()) { s.Cancel(); Sessions.Remove(s); }
                Agents.Remove(e);
                var ne = CreateEntry(p);
                Agents.Insert(Math.Max(0, Math.Min(idx, Agents.Count)), ne);
                foreach (var s in ne.Tabs.Where(x => x.IsScheduled)) ScheduleService.Reschedule(s);
                SaveAgents();
                return ne;
            }
            bool providerChanged = e.Profile.Provider != p.Provider;
            e.UpdateProfile(p);
            foreach (var s in e.Tabs)
            {
                if (providerChanged) s.NewConversation();
                if (s.IsScheduled) ScheduleService.Reschedule(s);
            }
            SaveAgents();
            return e;
        }

        /// <summary>Elimina un agente con todas sus pestañas.</summary>
        public void RemoveAgent(AgentEntry e)
        {
            foreach (var s in e.Tabs.ToList())
            {
                s.Cancel();
                Sessions.Remove(s);
                Store.DeleteHistory(s.Key);
            }
            Agents.Remove(e);
            ApplyDefault(); // si era el principal, el primero pasa a serlo
            SaveAgents();
        }

        // =============================================================== notificaciones
        private void OnTurnFinished(AgentSession s, TurnResult r)
        {
            if (r.Cancelled) return;
            var title = r.Success ? $"{s.DisplayName} terminó" : $"{s.DisplayName} tuvo un problema";
            var body = string.IsNullOrWhiteSpace(r.Summary) ? (r.Success ? "Tarea completada" : "Revisa la conversación") : r.Summary;
            if (r.Duration.TotalSeconds >= 15) body += $"  ·  {FormatDuration(r.Duration)}";

            bool userIsLooking = Notch.IsLookingAt(s);
            if (!userIsLooking) Notch.ShowBanner(s, title, body);
            if (Store.Config.Settings.NotifyOnDone && (!userIsLooking || r.FromSchedule))
                Notifier.Toast(title, body, s.Key);
            Notifier.Play(r.Success ? NotificationService.SoundKind.Done : NotificationService.SoundKind.Error);
        }

        private void OnApprovalRequested(AgentSession s, ApprovalRequest req)
        {
            Notifier.Play(NotificationService.SoundKind.Attention);
            if (!Notch.IsLookingAt(s))
            {
                Notch.ShowBanner(s, $"{s.DisplayName} necesita permiso", $"{req.Tool}: {FirstLine(req.Detail)}", sticky: true);
                Notifier.Toast($"{s.DisplayName} necesita permiso", $"{req.Tool}: {FirstLine(req.Detail)}", s.Key, System.Windows.Forms.ToolTipIcon.Warning);
            }
        }

        /// <summary>Sesión de un agente por id de perfil (la pestaña activa en los de código).</summary>
        private AgentSession? SessionOfAgent(string? agentId)
        {
            var e = Agents.FirstOrDefault(a => a.Profile.Id == agentId);
            return e?.ActiveTab ?? Sessions.FirstOrDefault(x => x.Key == agentId);
        }

        private void OnReminderFired(Reminder r)
        {
            var s = SessionOfAgent(r.AgentId) ?? Sessions.FirstOrDefault(x => !x.IsScheduled) ?? Sessions.FirstOrDefault();
            if (r.Kind == ReminderKind.RunPrompt && s != null)
            {
                if (s.IsBusy)
                {
                    // Reintenta en 1 minuto si el agente está ocupado
                    Reminders.Add(new Reminder { Text = r.Text, DueAt = DateTime.Now.AddMinutes(1), Kind = r.Kind, AgentId = r.AgentId });
                    return;
                }
                Notifier.Toast($"⏰ {s.DisplayName} empieza una tarea programada", r.Text, s.Key);
                _ = s.SendAsync(r.Text, fromSchedule: true);
                return;
            }

            var name = s?.DisplayName ?? "Agent Manager Notch";
            Notifier.Play(NotificationService.SoundKind.Reminder);
            Notifier.Toast($"⏰ Recordatorio de {name}", r.Text, s?.Key);
            if (s != null)
            {
                s.AddNotice($"⏰ Recordatorio: {r.Text}");
                if (!s.IsBusy)
                {
                    s.State = MochiState.Alarm;
                    var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                    t.Tick += (_, _) => { t.Stop(); if (!s.IsBusy && s.State == MochiState.Alarm) s.State = MochiState.Idle; };
                    t.Start();
                }
                Notch.ShowBanner(s, $"⏰ Recordatorio", r.Text, sticky: true);
            }
        }

        /// <summary>Busca por nombre de agente, por nombre de carpeta de una pestaña o por clave.</summary>
        private AgentSession? FindSession(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            name = name.Trim();
            var e = Agents.FirstOrDefault(a => a.Profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (e != null) return e.ActiveTab;
            return Sessions.FirstOrDefault(s => s.IsCode && s.FolderName.Equals(name, StringComparison.OrdinalIgnoreCase))
                   ?? Sessions.FirstOrDefault(s => s.Key == name);
        }

        /// <summary>Mensajes que llegan por el canal de control (scripts, otras apps, hooks globales).</summary>
        private void HandleControl(System.Text.Json.Nodes.JsonObject msg)
        {
            try
            {
                var cmd = msg["cmd"]?.GetValue<string>();
                switch (cmd)
                {
                    case "show":
                        Notch.Expand(focusInput: true);
                        break;
                    case "code":
                        {
                            var folder = msg["folder"]?.GetValue<string>() ?? "";
                            if (!System.IO.Directory.Exists(folder)) break;
                            var s = AddWorkspace(folder);
                            Notch.Expand(focusInput: true, sessionKey: s.Key);
                            break;
                        }
                    case "new-agent":
                        Notch.OpenEditor(null);
                        break;
                    case "reminders":
                        Notch.OpenReminders();
                        break;
                    case "notify":
                        {
                            var title = msg["title"]?.GetValue<string>() ?? "Agent Manager Notch";
                            var body = msg["body"]?.GetValue<string>() ?? "";
                            var s = FindSession(msg["agent"]?.GetValue<string>()) ?? Sessions.FirstOrDefault();
                            if (s != null)
                            {
                                s.AddNotice($"🔔 {title}: {body}");
                                Notch.ShowBannerRaw(s.Color, MochiState.Done, title, body, s.Key, sticky: false);
                            }
                            Notifier.Toast(title, body, s?.Key);
                            Notifier.Play(NotificationService.SoundKind.Done);
                            break;
                        }
                    case "ask":
                        {
                            var s = FindSession(msg["agent"]?.GetValue<string>());
                            var text = msg["text"]?.GetValue<string>() ?? "";
                            if (s == null) { Log.Info("ask: agente no encontrado"); break; }
                            _ = s.SendAsync(text); // si está ocupado, queda en la cola
                            break;
                        }
                    case "claude-event":
                        HandleClaudeEvent(msg["payload"] as System.Text.Json.Nodes.JsonObject);
                        break;
                }
            }
            catch (Exception ex) { Log.Error("HandleControl", ex); }
        }

        /// <summary>Eventos de las sesiones de Claude Code que el usuario abre en su terminal.</summary>
        private void HandleClaudeEvent(System.Text.Json.Nodes.JsonObject? p)
        {
            if (p == null) return;
            var ev = p["hook_event_name"]?.GetValue<string>() ?? "";
            var cwd = p["cwd"]?.GetValue<string>() ?? "";
            var project = string.IsNullOrEmpty(cwd) ? "tu terminal" : System.IO.Path.GetFileName(cwd.TrimEnd('\\', '/'));
            if (ev == "Stop")
            {
                Notch.ShowBannerRaw(ClaudeColor, MochiState.Done, "Claude Code terminó", $"Sesión en {project}", null, sticky: false);
                if (Store.Config.Settings.NotifyOnDone) Notifier.Toast("Claude Code terminó", $"Sesión en {project}", null);
                Notifier.Play(NotificationService.SoundKind.Done);
            }
            else if (ev == "Notification")
            {
                var m = p["message"]?.GetValue<string>() ?? "Claude Code necesita tu atención";
                Notch.ShowBannerRaw(ClaudeColor, MochiState.Waiting, $"Claude Code · {project}", m, null, sticky: true);
                Notifier.Toast($"Claude Code · {project}", m, null, System.Windows.Forms.ToolTipIcon.Warning);
                Notifier.Play(NotificationService.SoundKind.Attention);
            }
        }

        public void ExitApp()
        {
            foreach (var s in Sessions) s.Cancel();
            _cts.Cancel();
            Approvals.Stop();
            Notifier.Dispose();
            Shutdown();
        }

        private static string FirstLine(string s) => (s ?? "").Split('\n').FirstOrDefault()?.Trim() ?? "";
        private static string FormatDuration(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{t.Seconds} s";
    }
}
