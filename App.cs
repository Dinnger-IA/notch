using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CoucouWin.Controls;
using CoucouWin.Models;
using CoucouWin.Providers;
using CoucouWin.Services;
using CoucouWin.Views;

namespace CoucouWin
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            // Modo hook: lo invoca Claude Code antes de usar una herramienta (no abre ventanas).
            if (args.Length > 0 && args[0] == "--hook") return HookBridge.Run();
            // Comandos de control: --notify, --ask, --claude-event
            if (ControlChannel.TryRunCli(args) is int code) return code;

            using var mutex = new Mutex(true, "CoucouWin.SingleInstance", out bool first);
            if (!first)
            {
                // Ya hay una instancia: le pedimos que se muestre
                if (!ControlChannel.Send(new { cmd = "show" }))
                    MessageBox.Show("Coucou ya se está ejecutando (busca el icono en la bandeja o pulsa Ctrl+Alt+Espacio).",
                        "Coucou", MessageBoxButton.OK, MessageBoxImage.Information);
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
        public ObservableCollection<AgentSession> Sessions { get; } = new();
        public ApprovalServer Approvals { get; } = new();
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
                Source = new Uri("pack://application:,,,/CoucouWin;component/Themes/Theme.xaml")
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

            foreach (var p in Store.Config.Agents) Sessions.Add(CreateSession(p));
            ApplyDefault();
            Sessions.CollectionChanged += (_, _) => ApplyDefault();

            Approvals.Handler = req => Dispatcher.InvokeAsync(() =>
            {
                var s = Sessions.FirstOrDefault(x => x.Profile.Id == req.AgentId);
                return s != null ? s.RequestApproval(req) : Task.FromResult(ApprovalDecision.Deny);
            }).Task.Unwrap();
            Approvals.Start();

            Notifier = new NotificationService(() => Store.Config.Settings);
            Notifier.ShowRequested += () => Notch.Expand(focusInput: true);
            Notifier.NotificationClicked += id => Notch.Expand(focusInput: true, agentId: id);
            Notifier.NewAgentRequested += () => Notch.OpenEditor(null);
            Notifier.RemindersRequested += () => Notch.OpenReminders();
            Notifier.ExitRequested += ExitApp;

            Reminders = new ReminderService(Store);
            Reminders.Fired += OnReminderFired;
            Scheduler = new ScheduleService(() => Sessions, SaveAgents);

            Notch = new NotchWindow();
            Notch.Show();
            Reminders.Start();
            Scheduler.Start();
            ControlChannel.StartServer(msg => Dispatcher.BeginInvoke(() => HandleControl(msg)), _cts.Token);
            Log.Info("Coucou iniciado");
        }

        public AgentSession CreateSession(AgentProfile p)
        {
            AgentSession? s = null;
            s = new AgentSession(p, Store, () => BuildContext(s!));
            s.TurnFinished += OnTurnFinished;
            s.ApprovalRequested += OnApprovalRequested;
            s.ReminderCreated += (_, r) => Reminders.Add(r);
            return s;
        }

        private RunContext BuildContext(AgentSession s)
        {
            var now = DateTime.Now;
            var addendum =
                "\n\n---\n[Contexto de Coucou para Windows]\n" +
                $"Eres «{s.Profile.Name}», un agente que vive en el notch de Coucou en Windows. " +
                $"Fecha y hora local: {now:yyyy-MM-dd HH:mm} ({TimeZoneInfo.Local.DisplayName}).\n" +
                "Si el usuario te pide que le recuerdes algo, incluye en tu respuesta una línea con el formato exacto " +
                "[[reminder: <cuándo> | <texto del aviso>]]. Si te pide hacer algo tú mismo más tarde (o de forma periódica), usa " +
                "[[task: <cuándo> | <instrucción que debes ejecutar>]]. <cuándo> puede ser una duración (10m, 2h, 1d) o una " +
                "fecha-hora local ISO (2026-10-02T09:00). Coucou elimina la etiqueta y programa el aviso; confirma brevemente al usuario.";
            if (s.IsCode)
                addendum += $"\nEres un agente de código. Tu carpeta de trabajo es «{s.Profile.WorkingDirectory}»: trabaja solo dentro de ella.";
            else if (s.IsScheduled)
                addendum += "\nEsta es una ejecución PROGRAMADA y automática: nadie va a responder preguntas. Haz la tarea de principio a fin " +
                            "y termina con un resumen de 1 a 3 líneas que sirva como notificación.";
            if (s.IsConversational)
                addendum += "\nPROGRESO: Coucou muestra al usuario una barra lateral con tus pasos. Si la petición tiene 2 o más pasos, " +
                            "escribe al empezar tu plan en este formato exacto (en su propio mensaje, antes de usar herramientas):\n" +
                            "[[tasks]]\n- [~] primer paso (en curso)\n- [ ] siguiente paso\n[[/tasks]]\n" +
                            "Cada vez que termines un paso, vuelve a escribir el bloque completo actualizado: [x] hecho, [~] en curso, [ ] pendiente. " +
                            "Coucou oculta el bloque del chat, así que no lo menciones. Para peticiones simples de un paso no lo uses.";
            return new RunContext
            {
                AgentId = s.Profile.Id,
                AgentName = s.Profile.Name,
                PipeName = Approvals.PipeName,
                HookSettingsPath = HookSettingsPath,
                FullSystemPrompt = (s.Profile.SystemPrompt ?? "").Trim() + addendum,
                History = s.Messages.ToList()
            };
        }

        private void OnTurnFinished(AgentSession s, TurnResult r)
        {
            if (r.Cancelled) return;
            var title = r.Success ? $"{s.Profile.Name} terminó" : $"{s.Profile.Name} tuvo un problema";
            var body = string.IsNullOrWhiteSpace(r.Summary) ? (r.Success ? "Tarea completada" : "Revisa la conversación") : r.Summary;
            if (r.Duration.TotalSeconds >= 15) body += $"  ·  {FormatDuration(r.Duration)}";

            bool userIsLooking = Notch.IsLookingAt(s);
            if (!userIsLooking) Notch.ShowBanner(s, title, body);
            if (Store.Config.Settings.NotifyOnDone && (!userIsLooking || r.FromSchedule))
                Notifier.Toast(title, body, s.Profile.Id);
            Notifier.Play(r.Success ? NotificationService.SoundKind.Done : NotificationService.SoundKind.Error);
        }

        private void OnApprovalRequested(AgentSession s, ApprovalRequest req)
        {
            Notifier.Play(NotificationService.SoundKind.Attention);
            if (!Notch.IsLookingAt(s))
            {
                Notch.ShowBanner(s, $"{s.Profile.Name} necesita permiso", $"{req.Tool}: {FirstLine(req.Detail)}", sticky: true);
                Notifier.Toast($"{s.Profile.Name} necesita permiso", $"{req.Tool}: {FirstLine(req.Detail)}", s.Profile.Id, System.Windows.Forms.ToolTipIcon.Warning);
            }
        }

        private void OnReminderFired(Reminder r)
        {
            var s = Sessions.FirstOrDefault(x => x.Profile.Id == r.AgentId) ?? Sessions.FirstOrDefault();
            if (r.Kind == ReminderKind.RunPrompt && s != null)
            {
                if (s.IsBusy)
                {
                    // Reintenta en 1 minuto si el agente está ocupado
                    Reminders.Add(new Reminder { Text = r.Text, DueAt = DateTime.Now.AddMinutes(1), Kind = r.Kind, AgentId = r.AgentId });
                    return;
                }
                Notifier.Toast($"⏰ {s.Profile.Name} empieza una tarea programada", r.Text, s.Profile.Id);
                _ = s.SendAsync(r.Text, fromSchedule: true);
                return;
            }

            var name = s?.Profile.Name ?? "Coucou";
            Notifier.Play(NotificationService.SoundKind.Reminder);
            Notifier.Toast($"⏰ Recordatorio de {name}", r.Text, s?.Profile.Id);
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

        private AgentSession? FindAgent(string? name) =>
            string.IsNullOrWhiteSpace(name) ? null :
            Sessions.FirstOrDefault(x => x.Profile.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? Sessions.FirstOrDefault(x => x.Profile.Id == name);

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
                            // Si ya hay un agente para esa carpeta, se muestra ese
                            var existing = Sessions.FirstOrDefault(x => x.IsCode &&
                                string.Equals(System.IO.Path.GetFullPath(x.FolderPath.Length > 0 ? x.FolderPath : "?").TrimEnd('\\'),
                                              folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                            // Si hay un agente de código esperando carpeta, se le asigna a él
                            var empty = existing == null ? Sessions.FirstOrDefault(x => x.NeedsFolder) : null;
                            if (empty != null && empty.TrySetFolder(folder, out _)) SaveAgents();
                            var s = existing ?? empty ?? AddCodeAgent(folder);
                            Notch.Expand(focusInput: true, agentId: s.Profile.Id);
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
                            var title = msg["title"]?.GetValue<string>() ?? "Coucou";
                            var body = msg["body"]?.GetValue<string>() ?? "";
                            var s = FindAgent(msg["agent"]?.GetValue<string>()) ?? Sessions.FirstOrDefault();
                            if (s != null)
                            {
                                s.AddNotice($"🔔 {title}: {body}");
                                Notch.ShowBannerRaw(s.Color, MochiState.Done, title, body, s.Profile.Id, sticky: false);
                            }
                            Notifier.Toast(title, body, s?.Profile.Id);
                            Notifier.Play(NotificationService.SoundKind.Done);
                            break;
                        }
                    case "ask":
                        {
                            var s = FindAgent(msg["agent"]?.GetValue<string>());
                            var text = msg["text"]?.GetValue<string>() ?? "";
                            if (s == null) { Log.Info("ask: agente no encontrado"); break; }
                            _ = s.SendAsync(text); // si está ocupado, queda en Pendientes
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

        public void SaveAgents()
        {
            Store.Config.Agents = Sessions.Select(x => x.Profile).ToList();
            Store.Save();
        }

        /// <summary>Añade el agente de código por defecto a configuraciones antiguas (solo una vez).</summary>
        private void MigrateConfig()
        {
            var st = Store.Config.Settings;
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
        }

        /// <summary>Marca el agente principal; si no existe, el primero pasa a serlo.</summary>
        private void ApplyDefault()
        {
            var st = Store.Config.Settings;
            if (Sessions.Count > 0 && !Sessions.Any(x => x.Profile.Id == st.DefaultAgentId))
                st.DefaultAgentId = Sessions[0].Profile.Id;
            foreach (var x in Sessions) x.IsDefault = x.Profile.Id == st.DefaultAgentId;
        }

        public void SetDefault(AgentSession s)
        {
            Store.Config.Settings.DefaultAgentId = s.Profile.Id;
            ApplyDefault();
            Store.Save();
        }

        public AgentSession AddAgent(AgentProfile p)
        {
            var s = CreateSession(p);
            Sessions.Add(s);
            if (s.IsScheduled) ScheduleService.Reschedule(s);
            SaveAgents();
            return s;
        }

        /// <summary>Crea un agente de código para una carpeta (no se puede cambiar después).</summary>
        public AgentSession AddCodeAgent(string folder)
        {
            var used = Sessions.Select(x => x.Profile.ColorHex).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var color = ConfigStore.CodePalette.FirstOrDefault(c => !used.Contains(c)) ?? ConfigStore.CodePalette[Sessions.Count % ConfigStore.CodePalette.Length];
            var name = System.IO.Path.GetFileName(folder.TrimEnd('\\', '/'));
            var p = ConfigStore.NewCodeAgent(folder, string.IsNullOrEmpty(name) ? "Código" : name, color);
            var s = AddAgent(p);
            s.AddNotice($"📁 Trabajando en {folder}");
            return s;
        }

        /// <summary>Cierra un agente (detiene su CLI y borra su historial).</summary>
        public void RemoveAgent(AgentSession s)
        {
            s.Cancel();
            Sessions.Remove(s);
            Store.DeleteHistory(s.Profile.Id);
            SaveAgents();
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
