using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using AgentManagerNotch.Controls;
using AgentManagerNotch.Models;
using AgentManagerNotch.Providers;

namespace AgentManagerNotch.Services
{
    public record TurnResult(bool Success, bool Cancelled, string Summary, TimeSpan Duration, bool FromSchedule);

    /// <summary>Estado vivo de un agente: conversación, proceso CLI, aprobaciones pendientes y animación.</summary>
    public class AgentSession : INotifyPropertyChanged
    {
        private readonly ConfigStore _store;
        private readonly Func<RunContext> _contextFactory;
        private Process? _proc;
        private bool _cancelled;
        private readonly DispatcherTimer _settleTimer;
        private readonly List<ApprovalRequest> _approvals = new();

        public AgentProfile Profile { get; private set; }
        public ObservableCollection<ChatMessage> Messages { get; } = new();
        public string? SessionId { get; private set; }
        public HashSet<string> AlwaysAllow { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Tareas en cola (mensajes enviados mientras el agente trabajaba).</summary>
        public ObservableCollection<AgentTask> PendingTasks { get; } = new();
        /// <summary>Historial de tareas terminadas, la más reciente primero.</summary>
        public ObservableCollection<AgentTask> CompletedTasks { get; } = new();
        private const int MaxCompleted = 60;

        public event Action<AgentSession, TurnResult>? TurnFinished;
        public event Action<AgentSession, ApprovalRequest>? ApprovalRequested;
        public event Action<AgentSession, Reminder>? ReminderCreated;

        /// <summary>Pestaña de workspace (solo agentes de código).</summary>
        public WorkspaceInfo? Workspace { get; }
        /// <summary>Identificador único de esta sesión (agente o agente + pestaña). Se usa para historial y aprobaciones.</summary>
        public string Key => Workspace == null || Workspace.Id == WorkspaceInfo.LegacyId ? Profile.Id : $"{Profile.Id}_{Workspace.Id}";
        /// <summary>Carpeta donde se abre el CLI.</summary>
        public string WorkDir => Workspace?.Folder ?? Profile.WorkingDirectory;
        /// <summary>Nombre para avisos: «Código · mi-proyecto» en pestañas.</summary>
        public string DisplayName => Workspace != null ? $"{Profile.Name} · {FolderName}" : Profile.Name;

        private DateTime _lastActivity = DateTime.MinValue;
        /// <summary>Última vez que empezó o terminó una tarea (para ordenar pestañas).</summary>
        public DateTime LastActivity { get => _lastActivity; private set => Set(ref _lastActivity, value); }

        public AgentSession(AgentProfile profile, ConfigStore store, Func<RunContext> contextFactory, WorkspaceInfo? workspace = null)
        {
            Workspace = workspace;
            Profile = profile;
            _store = store;
            _contextFactory = contextFactory;
            _settleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _settleTimer.Tick += (_, _) => { _settleTimer.Stop(); if (!IsBusy) { State = MochiState.Idle; Activity = ""; } };

            var h = store.LoadHistory(Key);
            SessionId = h.SessionId;
            foreach (var m in h.Messages) Messages.Add(UpgradeLegacy(m));
            foreach (var t in h.Pending) { t.Status = AgentTaskStatus.Pending; PendingTasks.Add(t); }
            foreach (var t in h.Completed) CompletedTasks.Add(t);
            if (CompletedTasks.FirstOrDefault()?.Finished is { } lf) _lastActivity = lf;
            // Si había cola al cerrar, no arrancamos solos: el usuario decide con "Reanudar".
            _queuePaused = PendingTasks.Count > 0;
            PendingTasks.CollectionChanged += (_, _) => { OnChanged(nameof(PendingCount)); OnChanged(nameof(CanResumeQueue)); RebuildTaskLines(); OnChanged(nameof(OverviewBadge)); };
            CompletedTasks.CollectionChanged += (_, _) => { OnChanged(nameof(CompletedCount)); RebuildTaskLines(); };
            Messages.CollectionChanged += (_, _) => RaiseOutputChanged();
            RebuildTaskLines();
        }

        // ---------------- Lista lateral (plana: completadas → actual + pasos → pendientes) ----------------
        public ObservableCollection<TaskLine> TaskLines { get; } = new();
        public bool HasTaskLines => TaskLines.Count > 0;

        private void RebuildTaskLines()
        {
            TaskLines.Clear();
            foreach (var t in CompletedTasks.Take(10).Reverse())
                TaskLines.Add(new TaskLine
                {
                    Text = t.Title, Detail = t.Prompt,
                    Tone = t.Status switch { AgentTaskStatus.Failed => TaskTone.Failed, AgentTaskStatus.Cancelled => TaskTone.Cancelled, _ => TaskTone.Done }
                });
            if (_current != null)
            {
                TaskLines.Add(new TaskLine { Text = _current.Title, Detail = _current.Prompt, Tone = TaskTone.Running });
                foreach (var st in _current.Steps)
                    TaskLines.Add(new TaskLine
                    {
                        Text = st.Text, Detail = st.Text,
                        Tone = st.Status switch { StepStatus.Completed => TaskTone.StepDone, StepStatus.InProgress => TaskTone.StepActive, _ => TaskTone.StepPending }
                    });
            }
            foreach (var t in PendingTasks)
                TaskLines.Add(new TaskLine { Text = t.Title, Detail = t.Prompt, Tone = TaskTone.Pending });
            OnChanged(nameof(HasTaskLines));
        }

        // ---------------- Resumen (vista general y barra informativa) ----------------
        private ChatMessage? LastAgentMessage => Messages.LastOrDefault(m => m.Role == ChatRole.Agent && !string.IsNullOrWhiteSpace(m.Text));

        /// <summary>Lo último que escribió el agente (texto plano, sin Markdown).</summary>
        public string LastOutputText
        {
            get
            {
                var m = LastAgentMessage;
                if (m == null) return IsScheduled ? "Aún no se ha ejecutado." : "Sin mensajes todavía.";
                var t = Regex.Replace(m.Text, @"```.*?```", " [código] ", RegexOptions.Singleline);
                t = Regex.Replace(t, @"[#*`>_\[\]]+", "").Replace("\r", "");
                t = Regex.Replace(t, @"\s*\n\s*", " · ").Trim(' ', '·');
                return t.Length > 260 ? t[..260] + "…" : t;
            }
        }

        public string LastOutputTimeLabel => LastAgentMessage is { } m ? "Escrito " + FormatWhen(m.Time) : "";
        public DateTime? LastOutputTime => LastAgentMessage?.Time;

        /// <summary>
        /// Último resultado partido en titular + detalles (para la tarjeta de los programados).
        /// El titular es la primera línea (Agent Manager Notch pide a los programados que sea corta); si la respuesta es
        /// un solo párrafo, se parte por frases.
        /// </summary>
        public (string Headline, List<string> Details) LastOutputParts()
        {
            var m = LastAgentMessage;
            if (m == null) return ("", new List<string>());
            var text = Regex.Replace(m.Text, @"```.*?```", " ", RegexOptions.Singleline);
            var lines = text.Replace("\r", "").Split('\n')
                .Select(l => Regex.Replace(l, @"^\s*([-*+>]|\d+[.)])\s+", ""))   // viñetas
                .Select(l => Regex.Replace(l, @"[*_`#]+", "").Trim())
                .Where(l => l.Length > 0).ToList();
            if (lines.Count == 1)
            {
                // Un solo párrafo: titular = primera frase, detalles = el resto de frases
                var sentences = Regex.Split(lines[0], @"(?<=[.;!?])\s+").Where(x => x.Trim().Length > 0).ToList();
                lines = sentences;
            }
            if (lines.Count == 0) return ("", new List<string>());
            string Cut(string s, int max) => s.Length > max ? s[..max].TrimEnd() + "…" : s;
            // Sin emojis/símbolos al inicio: WPF los dibuja como contorno monocromo
            var head = Regex.Replace(lines[0], @"^[^\p{L}\p{N}¡¿""«(]+", "");
            var headline = Cut(head.TrimEnd('.', ':'), 60);
            var details = lines.Skip(1).Take(3).Select(l => Cut(l, 90)).ToList();
            return (headline, details);
        }

        /// <summary>Una línea para la vista general.</summary>
        public string OverviewLine
        {
            get
            {
                if (PendingApproval != null) return $"Necesita permiso · {PendingApproval.Tool}";
                if (NeedsFolder) return "Arrastra una carpeta para empezar";
                if (IsBusy)
                {
                    var step = _current?.Steps.FirstOrDefault(s => s.Status == StepStatus.InProgress)?.Text;
                    return step ?? (string.IsNullOrEmpty(Activity) ? _current?.Title ?? "Trabajando…" : Activity);
                }
                return LastOutputText;
            }
        }

        /// <summary>Indicador corto a la derecha de la tarjeta.</summary>
        public string OverviewBadge
        {
            get
            {
                if (PendingApproval != null) return "permiso";
                if (IsBusy && _current is { HasSteps: true } c) return c.StepsSummary.Replace(" pasos", "");
                if (IsBusy) return "trabajando";
                if (PendingTasks.Count > 0) return $"{PendingTasks.Count} en cola";
                if (IsScheduled && !Profile.Schedule.Paused && Profile.Schedule.NextRun is { } n) return n.ToString("HH:mm");
                return "";
            }
        }

        /// <summary>Línea principal de la barra informativa de un agente programado.</summary>
        public string InfoLine => IsBusy ? $"Ejecutando · {(string.IsNullOrEmpty(Activity) ? "…" : Activity)}" : LastOutputText;

        private void RaiseOutputChanged()
        {
            OnChanged(nameof(LastOutputText)); OnChanged(nameof(LastOutputTimeLabel));
            OnChanged(nameof(OverviewLine)); OnChanged(nameof(InfoLine));
        }

        private void RaiseOverview()
        {
            OnChanged(nameof(OverviewLine)); OnChanged(nameof(OverviewBadge)); OnChanged(nameof(InfoLine));
            RaiseTabState();
        }

        // ---------------- Propiedades enlazables ----------------
        private MochiState _state = MochiState.Idle;
        public MochiState State { get => _state; set => Set(ref _state, value); }

        private string _activity = "";
        public string Activity { get => _activity; set { if (Set(ref _activity, value)) { OnChanged(nameof(StatusLine)); RaiseOverview(); } } }

        private bool _isBusy;
        public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) { OnChanged(nameof(IsIdle)); RaiseOverview(); } } }
        public bool IsIdle => !_isBusy;

        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

        private bool _isBox;
        public bool IsBox { get => _isBox; set => Set(ref _isBox, value); }

        private ApprovalRequest? _pending;
        public ApprovalRequest? PendingApproval { get => _pending; private set { if (Set(ref _pending, value)) RaiseOverview(); } }

        public Color Color => ParseColor(Profile.ColorHex);
        public string ProviderLabel => ProviderFactory.Label(Profile.Provider);
        public string StatusLine => string.IsNullOrEmpty(Activity) ? ProviderLabel : Activity;

        // ---- rol
        public bool IsCode => Profile.Kind == AgentKind.Code;
        public bool IsScheduled => Profile.Kind == AgentKind.Scheduled;
        public bool IsConversational => Profile.Kind != AgentKind.Scheduled;
        /// <summary>Agente de código que todavía no tiene carpeta asignada.</summary>
        public bool NeedsFolder => IsCode && string.IsNullOrWhiteSpace(WorkDir);
        public bool HasFolder => IsCode && !NeedsFolder;
        public bool CanType => IsConversational && !NeedsFolder;
        public string FolderPath => WorkDir;
        public string FolderName
        {
            get
            {
                if (string.IsNullOrWhiteSpace(WorkDir)) return "sin carpeta";
                var n = Path.GetFileName(WorkDir.TrimEnd('\\', '/'));
                return string.IsNullOrEmpty(n) ? WorkDir : n;
            }
        }
        /// <summary>Texto de la píldora de contexto del chat: carpeta (código) o CLI.</summary>
        public string ContextLabel => IsCode ? (HasFolder ? FolderName : "sin carpeta") : ProviderLabel;

        public string KindLabel => Profile.Kind switch
        {
            AgentKind.Code => "Código",
            AgentKind.Scheduled => "Programado",
            _ => "Chat"
        };

        private bool _isDefault;
        /// <summary>Agente principal (se muestra grande en el notch).</summary>
        public bool IsDefault { get => _isDefault; set => Set(ref _isDefault, value); }

        // ---- programación
        public string ScheduleSummary => IsScheduled ? Profile.Schedule.Summary : "";
        public bool IsPaused => IsScheduled && Profile.Schedule.Paused;
        public string NextRunLabel
        {
            get
            {
                if (!IsScheduled) return "";
                if (Profile.Schedule.Paused) return "En pausa";
                return Profile.Schedule.NextRun is { } n ? "Próxima: " + FormatWhen(n) : "Sin próximas ejecuciones";
            }
        }
        public string LastRunLabel => Profile.Schedule.LastRun is { } l ? "Última: " + FormatWhen(l) : "Aún no se ha ejecutado";

        /// <summary>Texto corto bajo el nombre en las pestañas.</summary>
        public string SubLabel => IsCode ? FolderName
            : IsScheduled ? (Profile.Schedule.Paused ? "en pausa" : Profile.Schedule.NextRun is { } n ? FormatWhen(n) : "—")
            : ProviderLabel;

        // ---- tareas
        private AgentTask? _current;
        public AgentTask? CurrentTask
        {
            get => _current;
            private set
            {
                if (_current != null) _current.PropertyChanged -= CurrentTask_PropertyChanged;
                if (!Set(ref _current, value)) return;
                if (_current != null) _current.PropertyChanged += CurrentTask_PropertyChanged;
                OnChanged(nameof(HasCurrentTask));
                RebuildTaskLines();
                RaiseOverview();
            }
        }

        private void CurrentTask_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AgentTask.Steps)) { RebuildTaskLines(); RaiseOverview(); }
        }
        public bool HasCurrentTask => _current != null;
        public int PendingCount => PendingTasks.Count;
        public int CompletedCount => CompletedTasks.Count;
        private bool _queuePaused;
        public bool CanResumeQueue => _queuePaused && !IsBusy && PendingTasks.Count > 0;

        private static string FormatWhen(DateTime t)
        {
            var today = DateTime.Today;
            if (t.Date == today) return "hoy " + t.ToString("HH:mm");
            if (t.Date == today.AddDays(1)) return "mañana " + t.ToString("HH:mm");
            if (t.Date == today.AddDays(-1)) return "ayer " + t.ToString("HH:mm");
            return t.ToString("ddd dd, HH:mm");
        }

        public void UpdateProfile(AgentProfile p)
        {
            Profile = p;
            RaiseProfileChanged();
        }

        public void RaiseProfileChanged()
        {
            foreach (var n in new[] { nameof(Profile), nameof(Color), nameof(ProviderLabel), nameof(StatusLine), nameof(IsCode),
                         nameof(IsScheduled), nameof(IsConversational), nameof(NeedsFolder), nameof(HasFolder), nameof(CanType),
                         nameof(FolderPath), nameof(FolderName), nameof(KindLabel), nameof(ScheduleSummary), nameof(IsPaused),
                         nameof(NextRunLabel), nameof(LastRunLabel), nameof(SubLabel), nameof(OverviewLine), nameof(OverviewBadge),
                         nameof(InfoLine), nameof(LastOutputText), nameof(ContextLabel) })
                OnChanged(n);
        }

        /// <summary>Estado de la última tarea terminada (para chips y pestañas).</summary>
        public AgentTaskStatus? LastTaskStatus => CompletedTasks.FirstOrDefault()?.Status;
        public bool LastFailed => !IsBusy && LastTaskStatus == AgentTaskStatus.Failed;
        /// <summary>Icono de estado de la pestaña: ▶ activa, ! permiso, ✓ terminó, ✗ falló, ○ sin tareas.</summary>
        public string TabIcon => PendingApproval != null ? "\uE7BA" : IsBusy ? "\uE768" : LastTaskStatus switch
        {
            AgentTaskStatus.Done => "\uE73E",
            AgentTaskStatus.Failed => "\uE711",
            AgentTaskStatus.Cancelled => "\uE71A",
            _ => "\uEA3A"
        };
        /// <summary>Tono de la pestaña para colorear el icono.</summary>
        public TaskTone TabTone => PendingApproval != null ? TaskTone.Running : IsBusy ? TaskTone.Running : LastTaskStatus switch
        {
            AgentTaskStatus.Done => TaskTone.Done,
            AgentTaskStatus.Failed => TaskTone.Failed,
            AgentTaskStatus.Cancelled => TaskTone.Cancelled,
            _ => TaskTone.Pending
        };

        /// <summary>Paso o actividad actual en una línea (vacío si está libre).</summary>
        public string CurrentLine
        {
            get
            {
                if (PendingApproval != null) return $"Necesita permiso · {PendingApproval.Tool}";
                if (!IsBusy) return "";
                var step = _current?.Steps.FirstOrDefault(s => s.Status == StepStatus.InProgress)?.Text;
                return step ?? (string.IsNullOrEmpty(Activity) ? _current?.Title ?? "Trabajando…" : Activity);
            }
        }

        private void RaiseTabState()
        {
            OnChanged(nameof(TabIcon)); OnChanged(nameof(TabTone)); OnChanged(nameof(LastTaskStatus));
            OnChanged(nameof(LastFailed)); OnChanged(nameof(CurrentLine));
        }

        // ---------------- Conversación ----------------
        public void NewConversation()
        {
            if (IsBusy) Cancel();
            SessionId = null;
            Messages.Clear();
            AlwaysAllow.Clear();
            Messages.Add(new ChatMessage(ChatRole.Notice, "Nueva conversación"));
            SaveHistory();
        }

        private const string TerminalNoticePrefix = "💻 Sesión de ";
        public static string TerminalNotice(string cli = "Claude Code") =>
            $"{TerminalNoticePrefix}{cli} de tu terminal. Si escribes aquí, se continúa esa misma sesión.";
        private static bool IsTerminalNotice(ChatMessage m) => m.Role == ChatRole.Notice && m.Text.StartsWith(TerminalNoticePrefix);

        /// <summary>
        /// Muestra en esta pestaña una sesión de Claude Code abierta en la terminal y la deja lista para continuarla
        /// desde el notch (el siguiente mensaje usa --resume con su id). No hace nada si el notch está trabajando aquí.
        /// </summary>
        public bool AttachTerminalSession(string sessionId, List<ChatMessage>? transcript)
        {
            if (IsBusy) return false;
            SessionId = sessionId;
            if (transcript != null)
            {
                // Solo se rehace si cambió (cada aviso de la terminal vuelve a leer la sesión entera)
                var shown = Messages.Where(m => !IsTerminalNotice(m)).ToList();
                bool same = shown.Count == transcript.Count && (shown.Count == 0 || shown[^1].Text == transcript[^1].Text);
                if (!same)
                {
                    Messages.Clear();
                    Messages.Add(new ChatMessage(ChatRole.Notice, TerminalNotice()));
                    foreach (var m in transcript) Messages.Add(m);
                }
            }
            LastActivity = DateTime.Now;
            SaveHistory();
            RaiseOutputChanged();
            RaiseOverview();
            return true;
        }

        /// <summary>
        /// Un turno de una sesión de Codex de la terminal (su notify no trae la transcripción: solo lo pedido y la
        /// respuesta): se añade a la pestaña y queda lista para continuar esa sesión desde el notch.
        /// </summary>
        public bool AppendTerminalTurn(string sessionId, IEnumerable<string> asked, string answer, string cli)
        {
            if (IsBusy) return false;
            if (SessionId != sessionId || !Messages.Any(IsTerminalNotice))
                Messages.Add(new ChatMessage(ChatRole.Notice, TerminalNotice(cli)));
            SessionId = sessionId;
            foreach (var q in asked.Where(q => !string.IsNullOrWhiteSpace(q))) Messages.Add(new ChatMessage(ChatRole.User, q.Trim()));
            if (!string.IsNullOrWhiteSpace(answer)) Messages.Add(new ChatMessage(ChatRole.Agent, answer.Trim()));
            LastActivity = DateTime.Now;
            SaveHistory();
            RaiseOutputChanged();
            RaiseOverview();
            return true;
        }

        /// <summary>
        /// Errores de Codex al continuar una sesión, explicados: la sesión sigue abierta en otro sitio (Codex solo deja
        /// escribir en ella a un proceso) o Codex ya no la tiene (entonces se olvida y el próximo mensaje empieza otra).
        /// </summary>
        private ChatMessage ExplainResumeError(string error, string prompt)
        {
            if (error.Contains("already has an active writer", StringComparison.OrdinalIgnoreCase))
                return new ChatMessage(ChatRole.Error,
                    "Esa sesión de Codex sigue abierta en otro programa: la terminal, la app de Codex o su servicio de fondo, " +
                    "que sigue en marcha aunque cierres la app. Codex solo deja usarla a uno a la vez.\n" +
                    "Ciérrala allí y vuelve a enviar el mensaje, o sigue aquí en una sesión nueva (sin lo hablado antes).")
                { Action = ChatMessage.ActionNewSession, ActionPrompt = prompt };
            if (error.Contains("no rollout found", StringComparison.OrdinalIgnoreCase))
            {
                SessionId = null;
                return new ChatMessage(ChatRole.Error,
                    "Codex ya no tiene guardada esa sesión, así que no se puede continuar. Vuelve a enviar el mensaje: " +
                    "empezará una sesión nueva en esta carpeta.");
            }
            return new ChatMessage(ChatRole.Error, error);
        }

        /// <summary>Botón «Seguir en una sesión nueva» de un error: olvida la sesión y vuelve a enviar lo pedido.</summary>
        public void ContinueInNewSession(ChatMessage error)
        {
            if (IsBusy || string.IsNullOrWhiteSpace(error.ActionPrompt)) return;
            var prompt = error.ActionPrompt!;
            error.Action = null;
            SessionId = null;
            AddNotice("Sesión nueva: Codex no recuerda lo hablado antes en esta pestaña.");
            _ = SendAsync(prompt);
        }

        public void AddNotice(string text, bool error = false)
        {
            Messages.Add(new ChatMessage(error ? ChatRole.Error : ChatRole.Notice, text));
            SaveHistory();
        }

        /// <summary>
        /// Envía un mensaje. Si el agente está ocupado, queda en la cola de pendientes y se ejecutará
        /// cuando termine la tarea actual.
        /// </summary>
        public async Task SendAsync(string text, bool fromSchedule = false)
        {
            text = text.Trim();
            if (text == "") return;
            if (!fromSchedule && TryHandleCommand(text)) return;
            if (NeedsFolder)
            {
                AddNotice("Primero elige la carpeta de trabajo: arrástrala sobre el notch o usa «Seleccionar carpeta».", true);
                return;
            }
            if (IsBusy)
            {
                if (fromSchedule) return; // el planificador reintentará
                PendingTasks.Add(new AgentTask { Prompt = text });
                AddNotice($"📝 En cola · {PendingTasks.Count} pendiente(s)");
                return;
            }
            _queuePaused = false; // escribir de nuevo reanuda la cola que se detuvo
            await RunTaskAsync(new AgentTask { Prompt = text, FromSchedule = fromSchedule });
        }

        public void ClearQueue()
        {
            PendingTasks.Clear();
            SaveHistory();
        }

        /// <summary>Ejecución de un agente programado.</summary>
        public async Task<bool> RunScheduledAsync()
        {
            if (IsBusy || !IsScheduled) return false;
            var instr = Profile.Schedule.Instruction.Trim();
            if (instr == "") return false;
            if (!Profile.Schedule.KeepContext) SessionId = null;
            Profile.Schedule.LastRun = DateTime.Now;
            RaiseProfileChanged();
            await RunTaskAsync(new AgentTask { Prompt = instr, FromSchedule = true });
            return true;
        }

        public void RemoveQueued(AgentTask t)
        {
            PendingTasks.Remove(t);
            SaveHistory();
        }

        public void ResumeQueue()
        {
            _queuePaused = false;
            OnChanged(nameof(CanResumeQueue));
            StartNextQueued();
        }

        public void ClearCompleted()
        {
            CompletedTasks.Clear();
            SaveHistory();
        }

        private void StartNextQueued()
        {
            if (IsBusy || _queuePaused || PendingTasks.Count == 0) return;
            var next = PendingTasks[0];
            PendingTasks.RemoveAt(0);
            _ = RunTaskAsync(next);
        }

        private async Task RunTaskAsync(AgentTask task)
        {
            var text = task.Prompt;
            bool fromSchedule = task.FromSchedule;
            if (IsCode && !System.IO.Directory.Exists(WorkDir))
            {
                AddNotice($"La carpeta {WorkDir} ya no existe. Cierra esta pestaña y abre otra.", true);
                return;
            }
            LastActivity = DateTime.Now;

            task.Status = AgentTaskStatus.Running;
            task.Steps = new List<TaskStep>();
            CurrentTask = task;
            Messages.Add(new ChatMessage(ChatRole.User, fromSchedule ? "⏰ " + text : text));
            IsBusy = true;
            OnChanged(nameof(CanResumeQueue));
            _cancelled = false;
            _settleTimer.Stop();
            State = MochiState.Thinking;
            Activity = "Pensando…";
            var started = DateTime.Now;
            int firstNew = Messages.Count;

            ChatMessage? streaming = null;
            string streamingRaw = "";
            bool gotResult = false, success = true;
            string? error = null;
            var stderr = new StringBuilder();

            try
            {
                var provider = ProviderFactory.Create(Profile.Provider);
                var ctx = _contextFactory();
                // Cada pestaña usa el perfil del agente con su propia carpeta
                var runProfile = Profile;
                if (Workspace != null) { runProfile = Profile.Clone(); runProfile.WorkingDirectory = Workspace.Folder; }
                var spec = provider.Build(runProfile, text, SessionId, ctx);
                var psi = spec.Psi;
                psi.Environment["COUCOU_PIPE"] = ctx.PipeName;
                psi.Environment["COUCOU_AGENT_ID"] = Key;
                psi.Environment["COUCOU_AGENT_NAME"] = Profile.Name;
                psi.Environment["COUCOU_APPROVAL"] = Profile.Approval.ToString();

                Log.Info($"[{Profile.Name}] {psi.FileName} {string.Join(" ", psi.ArgumentList.Select(a => a.Length > 60 ? a[..60] + "…" : a))}");
                _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) { if (stderr.Length < 20000) stderr.AppendLine(e.Data); } };
                _proc.Start();
                _proc.BeginErrorReadLine();

                if (spec.StdinText != null) await _proc.StandardInput.WriteAsync(spec.StdinText);
                _proc.StandardInput.Close();

                string? line;
                while ((line = await _proc.StandardOutput.ReadLineAsync()) != null)
                {
                    foreach (var ev in provider.Parse(line))
                    {
                        switch (ev)
                        {
                            case SessionEvent s:
                                SessionId = s.SessionId;
                                break;
                            case DeltaEvent d:
                                if (streaming == null)
                                {
                                    streaming = new ChatMessage(ChatRole.Agent, "");
                                    streamingRaw = "";
                                    Messages.Add(streaming);
                                }
                                streamingRaw += d.Text;
                                streaming.Text = StripTaskBlocks(streamingRaw, task);
                                if (State != MochiState.Waiting) { State = MochiState.Thinking; Activity = "Escribiendo…"; }
                                break;
                            case TextEvent t:
                                {
                                    var clean = StripTaskBlocks(t.Text, task);
                                    if (streaming != null)
                                    {
                                        streaming.Text = clean;
                                        if (clean == "") Messages.Remove(streaming);
                                        streaming = null;
                                    }
                                    else if (clean != "") Messages.Add(new ChatMessage(ChatRole.Agent, clean));
                                    break;
                                }
                            case ToolEvent tool:
                                if (streaming != null && streaming.Text == "") Messages.Remove(streaming);
                                streaming = null;
                                Messages.Add(new ChatMessage(ChatRole.Tool, $"{tool.Tool}  {tool.Detail}".Trim()));
                                if (State != MochiState.Waiting) State = MochiState.Working;
                                Activity = $"{tool.Tool}: {tool.Detail}";
                                break;
                            case NoticeEvent n:
                                streaming = null;
                                if (!string.IsNullOrWhiteSpace(n.Text))
                                    Messages.Add(new ChatMessage(n.IsError ? ChatRole.Error : ChatRole.Notice, n.Text));
                                break;
                            case ResultEvent r:
                                gotResult = true;
                                success = r.Success;
                                error = r.Error;
                                break;
                            case TodoEvent td:
                                if (!SameSteps(task.Steps, td.Steps.ToList())) task.Steps = td.Steps.ToList();
                                var active = td.Steps.FirstOrDefault(x => x.Status == StepStatus.InProgress);
                                if (active != null) Activity = active.Text;
                                break;
                        }
                    }
                }

                await _proc.WaitForExitAsync();
                var code = _proc.ExitCode;
                if (provider.PlainText)
                {
                    gotResult = true;
                    success = code == 0;
                    if (streaming != null) streaming.Text = StripTaskBlocks(streamingRaw, task).TrimEnd();
                }
                if (!gotResult && !_cancelled)
                {
                    success = code == 0;
                    if (!success)
                    {
                        string err; lock (stderr) err = stderr.ToString().Trim();
                        error = $"El CLI terminó con código {code}." + (err != "" ? "\n" + Tail(err, 900) : "");
                    }
                }
                else if (!success && error == null && !_cancelled)
                {
                    string err; lock (stderr) err = stderr.ToString().Trim();
                    if (err != "") error = Tail(err, 900);
                }
            }
            catch (Exception ex)
            {
                success = false;
                error = ex.Message;
                Log.Error($"[{Profile.Name}] fallo al ejecutar", ex);
            }
            finally
            {
                try { _proc?.Dispose(); } catch { }
                _proc = null;
            }

            if (_cancelled)
            {
                Messages.Add(new ChatMessage(ChatRole.Notice, "Detenido"));
            }
            else if (!success && !string.IsNullOrWhiteSpace(error))
            {
                var shown = ExplainResumeError(error!, text);
                error = shown.Text;
                Messages.Add(shown);
            }

            ExtractSchedules(firstNew);

            // Resolver aprobaciones que quedaron colgando
            foreach (var a in _approvals.ToList()) a.Resolve(ApprovalDecision.Deny);
            _approvals.Clear();
            PendingApproval = null;

            task.Status = _cancelled ? AgentTaskStatus.Cancelled : success ? AgentTaskStatus.Done : AgentTaskStatus.Failed;
            if (success && !_cancelled && task.Steps.Any(x => x.Status == StepStatus.InProgress))
                task.Steps = task.Steps.Select(x => new TaskStep { Text = x.Text, Status = x.Status == StepStatus.InProgress ? StepStatus.Completed : x.Status }).ToList();
            task.Finished = DateTime.Now;
            {
                var finalText = Messages.Skip(firstNew).LastOrDefault(m => m.Role == ChatRole.Agent && !string.IsNullOrWhiteSpace(m.Text))?.Text;
                task.Summary = Summarize(success ? finalText ?? "" : error ?? finalText ?? "");
            }
            CurrentTask = null;
            CompletedTasks.Insert(0, task);
            while (CompletedTasks.Count > MaxCompleted) CompletedTasks.RemoveAt(CompletedTasks.Count - 1);
            if (_cancelled && PendingTasks.Count > 0) _queuePaused = true; // al detener, la cola espera

            IsBusy = false;
            OnChanged(nameof(CanResumeQueue));
            var lastAgent = Messages.Skip(firstNew).LastOrDefault(m => m.Role == ChatRole.Agent)?.Text ?? error ?? "";
            State = _cancelled ? MochiState.Idle : success ? MochiState.Done : MochiState.Error;
            Activity = _cancelled ? "" : success ? "¡Listo!" : "Algo salió mal";
            _settleTimer.Interval = TimeSpan.FromSeconds(success ? 4 : 8);
            _settleTimer.Start();
            SaveHistory();

            LastActivity = DateTime.Now;
            RaiseOutputChanged();
            RaiseOverview();
            TurnFinished?.Invoke(this, new TurnResult(success, _cancelled, Summarize(lastAgent), DateTime.Now - started, fromSchedule));

            // Siguiente tarea de la cola
            if (!_queuePaused && PendingTasks.Count > 0)
            {
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                t.Tick += (_, _) => { t.Stop(); StartNextQueued(); };
                t.Start();
            }
        }

        public void Cancel()
        {
            if (_proc == null) return;
            _cancelled = true;
            try { _proc.Kill(entireProcessTree: true); } catch { }
        }

        // ---------------- Aprobaciones ----------------
        /// <summary>Llamado desde el ApprovalServer (hilo UI). Devuelve la decisión cuando el usuario elige.</summary>
        public Task<ApprovalDecision> RequestApproval(ApprovalRequest req)
        {
            if (AlwaysAllow.Contains(req.Tool))
            {
                LogPermission(req, ApprovalDecision.AllowAlways);
                return Task.FromResult(ApprovalDecision.Allow);
            }
            _approvals.Add(req);
            if (PendingApproval == null) ShowNextApproval();
            return req.Tcs.Task;
        }

        private void ShowNextApproval()
        {
            var next = _approvals.FirstOrDefault();
            PendingApproval = next;
            if (next != null)
            {
                State = MochiState.Waiting;
                Activity = $"Necesita permiso: {next.Tool}";
                ApprovalRequested?.Invoke(this, next);
            }
            else if (IsBusy)
            {
                State = MochiState.Working;
                Activity = "Trabajando…";
            }
        }

        public void Decide(ApprovalDecision d)
        {
            var req = PendingApproval;
            if (req == null) return;
            if (d == ApprovalDecision.AllowAlways) AlwaysAllow.Add(req.Tool);
            _approvals.Remove(req);
            LogPermission(req, d);
            req.Resolve(d);

            // Si eligió "siempre", libera las demás pendientes de la misma herramienta
            if (d == ApprovalDecision.AllowAlways)
                foreach (var other in _approvals.Where(a => a.Tool.Equals(req.Tool, StringComparison.OrdinalIgnoreCase)).ToList())
                { _approvals.Remove(other); LogPermission(other, ApprovalDecision.AllowAlways); other.Resolve(ApprovalDecision.Allow); }

            PendingApproval = null;
            ShowNextApproval();
        }

        /// <summary>Historial anterior: los avisos «✅ Permitido: X» / «✋ Denegado: X» pasan a ser entradas de permiso.</summary>
        private static ChatMessage UpgradeLegacy(ChatMessage m)
        {
            if (m.Role != ChatRole.Notice) return m;
            foreach (var (prefix, decision) in new[] { ("✅ Permitido: ", "Allow"), ("✋ Denegado: ", "Deny") })
                if (m.Text.StartsWith(prefix, StringComparison.Ordinal))
                    return new ChatMessage(ChatRole.Permission, m.Text[prefix.Length..].Trim()) { Time = m.Time, Decision = decision };
            return m;
        }

        /// <summary>Decisión de permiso para el panel de acciones (no sale en la conversación).</summary>
        private void LogPermission(ApprovalRequest req, ApprovalDecision d)
        {
            var detail = (req.Detail ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l != "") ?? "";
            if (detail.Length > 200) detail = detail[..200] + "…";
            Messages.Add(new ChatMessage(ChatRole.Permission, $"{req.Tool}  {detail}".Trim()) { Decision = d.ToString() });
        }

        // ---------------- Comandos de chat ----------------
        private bool TryHandleCommand(string text)
        {
            if (!text.StartsWith('/')) return false;
            var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var cmd = parts[0].ToLowerInvariant();
            var rest = parts.Length > 1 ? parts[1] : "";
            switch (cmd)
            {
                case "/nuevo":
                case "/new":
                case "/clear":
                    NewConversation();
                    return true;
                case "/recordar":
                case "/remind":
                case "/tarea":
                case "/task":
                    {
                        var kind = cmd is "/tarea" or "/task" ? ReminderKind.RunPrompt : ReminderKind.Notify;
                        // /recordar 10m texto   |   /recordar 15:30 texto
                        var sp = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (sp.Length < 2 || !TimeParser.TryParse(sp[0], out var due))
                        {
                            AddNotice("Uso: /recordar <10m | 2h | 15:30 | 2026-10-02T09:00> <texto>   ·   /tarea <cuándo> <instrucción para el agente>");
                            return true;
                        }
                        var rem = new Reminder { Text = sp[1], DueAt = due, Kind = kind, AgentId = Profile.Id };
                        ReminderCreated?.Invoke(this, rem);
                        AddNotice($"⏰ {(kind == ReminderKind.Notify ? "Recordatorio" : "Tarea")} programado para {due:ddd dd MMM HH:mm}: {sp[1]}");
                        return true;
                    }
                case "/ayuda":
                case "/help":
                    AddNotice("Comandos: /nuevo · /recordar <cuándo> <texto> · /tarea <cuándo> <instrucción> · /ayuda");
                    return true;
            }
            return false;
        }

        // [[tasks]] - [x] hecho  - [~] en curso  - [ ] pendiente [[/tasks]]
        private static readonly Regex TaskBlockRx = new(@"\[\[(?:tasks|tareas|plan)\]\](?<body>.*?)\[\[/(?:tasks|tareas|plan)\]\]",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex OpenTaskBlockRx = new(@"\[\[(?:tasks|tareas|plan)\]\](?:(?!\[\[/).)*$",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex StepLineRx = new(@"^\s*(?:[-*+]|\d+[.)])?\s*\[(?<m>[ xX~>\-✓])\]\s*(?<t>.+?)\s*$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        /// <summary>
        /// Quita los bloques [[tasks]] del texto visible y, si hay alguno completo, actualiza los pasos
        /// de la tarea con el último. Funciona con cualquier CLI (no depende de TodoWrite).
        /// </summary>
        private static string StripTaskBlocks(string raw, AgentTask task)
        {
            if (!raw.Contains("[[")) return raw;
            var ms = TaskBlockRx.Matches(raw);
            if (ms.Count > 0)
            {
                var steps = new List<TaskStep>();
                foreach (Match l in StepLineRx.Matches(ms[^1].Groups["body"].Value))
                {
                    var mk = l.Groups["m"].Value;
                    var st = mk is "x" or "X" or "✓" ? StepStatus.Completed : mk is "~" or ">" or "-" ? StepStatus.InProgress : StepStatus.Pending;
                    steps.Add(new TaskStep { Text = l.Groups["t"].Value, Status = st });
                }
                if (steps.Count > 0 && !SameSteps(task.Steps, steps)) task.Steps = steps;
            }
            var s = TaskBlockRx.Replace(raw, "");
            s = OpenTaskBlockRx.Replace(s, ""); // bloque a medio escribir durante el streaming
            return Regex.Replace(s, @"\n{3,}", "\n\n").Trim();
        }

        private static bool SameSteps(List<TaskStep> a, List<TaskStep> b) =>
            a.Count == b.Count && a.Zip(b).All(p => p.First.Text == p.Second.Text && p.First.Status == p.Second.Status);

        // [[reminder: 10m | texto]]   [[task: 2026-10-02T09:00 | instrucción]]
        private static readonly Regex ScheduleTag = new(@"\[\[\s*(?<k>reminder|recordatorio|task|tarea)\s*:\s*(?<when>[^|\]]+?)\s*\|\s*(?<text>[^\]]+?)\s*\]\]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private void ExtractSchedules(int fromIndex)
        {
            var notices = new List<string>();
            for (int i = fromIndex; i < Messages.Count; i++)
            {
                var m = Messages[i];
                if (m.Role != ChatRole.Agent || !m.Text.Contains("[[")) continue;
                var cleaned = ScheduleTag.Replace(m.Text, match =>
                {
                    var k = match.Groups["k"].Value.ToLowerInvariant();
                    var kind = k is "task" or "tarea" ? ReminderKind.RunPrompt : ReminderKind.Notify;
                    if (TimeParser.TryParse(match.Groups["when"].Value, out var due))
                    {
                        var text = match.Groups["text"].Value.Trim();
                        ReminderCreated?.Invoke(this, new Reminder { Text = text, DueAt = due, Kind = kind, AgentId = Profile.Id });
                        notices.Add($"⏰ {(kind == ReminderKind.Notify ? "Recordatorio" : "Tarea")} para {due:ddd dd MMM HH:mm}: {text}");
                    }
                    return "";
                });
                m.Text = cleaned.Trim();
            }
            foreach (var n in notices) Messages.Add(new ChatMessage(ChatRole.Notice, n));
        }

        private void SaveHistory() => _store.SaveHistory(Key, new AgentHistory
        {
            SessionId = SessionId,
            Messages = Messages.ToList(),
            Pending = PendingTasks.ToList(),
            Completed = CompletedTasks.ToList()
        });

        private static string Tail(string s, int max) => s.Length <= max ? s : "…" + s[^max..];

        private static string Summarize(string s)
        {
            s = Regex.Replace(s, @"[#*`>_]+", "").Replace("\r", "").Trim();
            var first = s.Split('\n').FirstOrDefault(l => l.Trim() != "")?.Trim() ?? "";
            return first.Length > 140 ? first[..140] + "…" : first;
        }

        public static Color ParseColor(string hex)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex); }
            catch { return Color.FromRgb(0xFF, 0x8A, 0x65); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        private bool Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value; OnChanged(n); return true;
        }
    }

    /// <summary>Interpreta "10m", "1h30m", "2 horas", "15:30", "mañana 9:00", ISO-8601...</summary>
    public static class TimeParser
    {
        private static readonly Regex Part = new(@"(?<n>\d+(?:[.,]\d+)?)\s*(?<u>d(?:ías?|ias?)?|h(?:oras?|rs?)?|m(?:in(?:utos?|s)?)?|s(?:eg(?:undos?)?)?)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool TryParse(string input, out DateTime due)
        {
            due = default;
            var s = input.Trim().ToLowerInvariant();
            if (s.StartsWith("en ")) s = s[3..];
            if (s == "") return false;
            var now = DateTime.Now;

            // Duraciones: "10m", "1h30m", "2 horas"
            var stripped = Part.Replace(s, "").Replace("y", "").Trim(' ', ',');
            if (stripped == "" && Part.IsMatch(s))
            {
                double secs = 0;
                foreach (Match m in Part.Matches(s))
                {
                    var n = double.Parse(m.Groups["n"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                    var u = m.Groups["u"].Value;
                    secs += u[0] switch { 'd' => n * 86400, 'h' => n * 3600, 'm' => n * 60, _ => n };
                }
                if (secs <= 0) return false;
                due = now.AddSeconds(secs);
                return true;
            }

            bool tomorrow = false;
            if (s.StartsWith("mañana") || s.StartsWith("manana")) { tomorrow = true; s = s[6..].Trim(); if (s.StartsWith("a las ")) s = s[6..]; }
            if (s.StartsWith("a las ")) s = s[6..];

            // Hora sola: "15:30", "9:00", "9am"
            var hm = Regex.Match(s, @"^(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ap>am|pm|a\.m\.|p\.m\.)?$");
            if (hm.Success && (hm.Groups["m"].Success || hm.Groups["ap"].Success || tomorrow))
            {
                int h = int.Parse(hm.Groups["h"].Value);
                int mi = hm.Groups["m"].Success ? int.Parse(hm.Groups["m"].Value) : 0;
                var ap = hm.Groups["ap"].Value;
                if (ap.StartsWith("p") && h < 12) h += 12;
                if (ap.StartsWith("a") && h == 12) h = 0;
                if (h > 23 || mi > 59) return false;
                due = now.Date.AddHours(h).AddMinutes(mi);
                if (tomorrow) due = due.AddDays(1);
                else if (due <= now) due = due.AddDays(1);
                return true;
            }
            if (tomorrow && s == "") { due = now.Date.AddDays(1).AddHours(9); return true; }

            var formats = new[] { "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "dd/MM/yyyy HH:mm", "yyyy-MM-dd" };
            if (DateTime.TryParseExact(input.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out due)) return true;
            if (DateTime.TryParse(input.Trim(), CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out due)) return true;
            if (DateTime.TryParse(input.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out due)) return true;
            return false;
        }
    }
}
