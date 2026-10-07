using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace AgentManagerNotch.Models
{
    /// <summary>CLI que ejecuta al agente por debajo (Plugin: lo aporta un plugin, ver <see cref="AgentProfile.PluginProvider"/>).</summary>
    public enum ProviderKind { ClaudeCode, Codex, Gemini, Custom, Plugin }

    /// <summary>Cómo se manejan los permisos de herramientas.</summary>
    public enum ApprovalMode
    {
        Ask,         // Pregunta en el notch antes de cada acción que modifica algo
        AcceptEdits, // Edita archivos solo, pregunta para comandos
        Auto,        // Sin preguntas (peligroso)
        ReadOnly     // Solo lectura
    }

    public enum ReminderKind { Notify, RunPrompt }
    public enum Recurrence { None, Hourly, Daily, Weekdays, Weekly }

    /// <summary>Rol del agente.</summary>
    public enum AgentKind
    {
        Interactive, // conversacional: chat + barra de tareas
        Code,        // conversacional atado a una carpeta fija (ahí se abren los CLI)
        Scheduled    // no interactivo: ejecuta una instrucción según un horario
    }

    public class AgentProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Mochi";
        public string ColorHex { get; set; } = "#FF8A65";
        public AgentKind Kind { get; set; } = AgentKind.Interactive;
        public string SystemPrompt { get; set; } = "";
        public ProviderKind Provider { get; set; } = ProviderKind.ClaudeCode;
        /// <summary>Con Provider=Plugin: el id del proveedor del plugin.</summary>
        public string? PluginProvider { get; set; }
        public string Model { get; set; } = "";
        /// <summary>En agentes de código es la carpeta de trabajo y no se puede cambiar una vez fijada.</summary>
        public string WorkingDirectory { get; set; } = "";
        public ApprovalMode Approval { get; set; } = ApprovalMode.Ask;
        public string ExtraArgs { get; set; } = "";
        /// <summary>Para Provider=Custom. Marcadores: {prompt} {system} {model}. Si no hay {prompt}, se envía por stdin.</summary>
        public string CustomCommand { get; set; } = "";
        /// <summary>Solo para Kind=Scheduled.</summary>
        public ScheduleSpec Schedule { get; set; } = new();
        /// <summary>Solo para Kind=Code: cada workspace es una pestaña con su carpeta fija y su sesión del CLI.</summary>
        public List<WorkspaceInfo> Workspaces { get; set; } = new();

        public AgentProfile Clone()
        {
            var c = (AgentProfile)MemberwiseClone();
            c.Schedule = Schedule.Clone();
            c.Workspaces = Workspaces.Select(w => w.Clone()).ToList();
            return c;
        }
    }

    /// <summary>Pestaña de un agente de código: una carpeta (no se puede cambiar; se cierra y se abre otra).</summary>
    public class WorkspaceInfo
    {
        /// <summary>Id "legacy" = workspace migrado de la versión anterior (reutiliza el historial del agente).</summary>
        public const string LegacyId = "legacy";
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
        public string Folder { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public WorkspaceInfo Clone() => (WorkspaceInfo)MemberwiseClone();
    }

    public enum ScheduleMode { Interval, FixedTimes }

    /// <summary>
    /// Horario de un agente programado: cada N horas (mínimo 1) dentro de una franja,
    /// o a horas fijas (separadas al menos 1 hora), solo en los días elegidos.
    /// </summary>
    public class ScheduleSpec
    {
        public const int MinIntervalHours = 1;

        public string Instruction { get; set; } = "";
        public ScheduleMode Mode { get; set; } = ScheduleMode.Interval;
        public int IntervalHours { get; set; } = 1;
        public string WindowStart { get; set; } = "09:00";
        public string WindowEnd { get; set; } = "18:00";
        public List<string> Times { get; set; } = new() { "09:00" };
        public List<DayOfWeek> Days { get; set; } = new()
        {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday
        };
        public bool Paused { get; set; }
        /// <summary>Si es true, cada ejecución continúa la sesión anterior del CLI.</summary>
        public bool KeepContext { get; set; }
        public DateTime? NextRun { get; set; }
        public DateTime? LastRun { get; set; }

        public ScheduleSpec Clone()
        {
            var c = (ScheduleSpec)MemberwiseClone();
            c.Times = new List<string>(Times);
            c.Days = new List<DayOfWeek>(Days);
            return c;
        }

        public static bool TryParseTime(string s, out TimeSpan t)
        {
            t = default;
            var parts = (s ?? "").Trim().Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return false;
            if (h < 0 || h > 23 || m < 0 || m > 59) return false;
            t = new TimeSpan(h, m, 0);
            return true;
        }

        /// <summary>Horas del día en las que toca ejecutar, ordenadas.</summary>
        public List<TimeSpan> SlotsOfDay()
        {
            var slots = new List<TimeSpan>();
            if (Mode == ScheduleMode.FixedTimes)
            {
                foreach (var s in Times) if (TryParseTime(s, out var t)) slots.Add(t);
                slots.Sort();
                return slots.Distinct().ToList();
            }
            if (!TryParseTime(WindowStart, out var start)) start = TimeSpan.Zero;
            if (!TryParseTime(WindowEnd, out var end) || end <= start) end = new TimeSpan(23, 59, 0);
            var step = TimeSpan.FromHours(Math.Max(MinIntervalHours, IntervalHours));
            for (var t = start; t <= end; t += step) slots.Add(t);
            return slots;
        }

        /// <summary>Próxima ejecución estrictamente posterior a <paramref name="after"/>.</summary>
        public DateTime? ComputeNext(DateTime after)
        {
            if (Days.Count == 0) return null;
            var slots = SlotsOfDay();
            if (slots.Count == 0) return null;
            for (int d = 0; d <= 8; d++)
            {
                var day = after.Date.AddDays(d);
                if (!Days.Contains(day.DayOfWeek)) continue;
                foreach (var s in slots)
                {
                    var cand = day + s;
                    if (cand > after) return cand;
                }
            }
            return null;
        }

        /// <summary>Devuelve un error legible o null si el horario es válido.</summary>
        public string? Validate()
        {
            if (string.IsNullOrWhiteSpace(Instruction)) return "Escribe la instrucción que debe ejecutar.";
            if (Days.Count == 0) return "Elige al menos un día.";
            if (Mode == ScheduleMode.Interval)
            {
                if (IntervalHours < MinIntervalHours) return "El intervalo mínimo es de 1 hora.";
                if (!TryParseTime(WindowStart, out _) || !TryParseTime(WindowEnd, out _)) return "Horario inválido (usa HH:mm).";
            }
            else
            {
                var slots = new List<TimeSpan>();
                foreach (var s in Times)
                {
                    if (!TryParseTime(s, out var t)) return $"Hora inválida: «{s}» (usa HH:mm).";
                    slots.Add(t);
                }
                if (slots.Count == 0) return "Añade al menos una hora.";
                slots.Sort();
                for (int i = 1; i < slots.Count; i++)
                    if (slots[i] - slots[i - 1] < TimeSpan.FromHours(MinIntervalHours))
                        return "Las horas deben estar separadas al menos 1 hora.";
            }
            return null;
        }

        public string Summary
        {
            get
            {
                var days = DaysLabel(Days);
                return Mode == ScheduleMode.Interval
                    ? $"Cada {IntervalHours} h de {WindowStart} a {WindowEnd} · {days}"
                    : $"A las {string.Join(", ", SlotsOfDay().Select(t => t.ToString(@"hh\:mm")))} · {days}";
            }
        }

        public static string DaysLabel(IReadOnlyCollection<DayOfWeek> days)
        {
            var all = Enum.GetValues<DayOfWeek>();
            if (days.Count == 7) return "todos los días";
            var weekdays = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
            if (days.Count == 5 && weekdays.All(days.Contains)) return "lun–vie";
            if (days.Count == 2 && days.Contains(DayOfWeek.Saturday) && days.Contains(DayOfWeek.Sunday)) return "fines de semana";
            string[] names = { "dom", "lun", "mar", "mié", "jue", "vie", "sáb" };
            return string.Join(" ", all.Where(days.Contains).OrderBy(d => ((int)d + 6) % 7).Select(d => names[(int)d]));
        }
    }

    // ------------------------------------------------------------------ tareas
    public enum AgentTaskStatus { Pending, Running, Done, Failed, Cancelled }

    /// <summary>Tono visual de una línea de la lista de tareas (solo icono + color).</summary>
    public enum TaskTone { Done, Failed, Cancelled, Running, Pending, StepDone, StepActive, StepPending, Schedule, Next, Bullet }

    /// <summary>Una línea de la lista lateral: tarea o paso de la tarea actual.</summary>
    public sealed class TaskLine
    {
        public string Text { get; init; } = "";
        public string Detail { get; init; } = "";
        public TaskTone Tone { get; init; }
        public bool IsStep => Tone is TaskTone.StepDone or TaskTone.StepActive or TaskTone.StepPending;
        public string Icon => Tone switch
        {
            TaskTone.Done or TaskTone.StepDone => "\uE73E",      // ✓
            TaskTone.Failed => "\uE783",                         // !
            TaskTone.Cancelled => "\uE711",                      // ×
            TaskTone.Running or TaskTone.StepActive => "\uE768", // ▶
            TaskTone.Schedule => "\uE823",                       // reloj
            TaskTone.Next => "\uE893",                           // siguiente
            TaskTone.Bullet => "\uEA3B",                         // punto
            _ => "\uEA3A"                                         // ○
        };
    }
    public enum StepStatus { Pending, InProgress, Completed }

    /// <summary>Un paso dentro de una tarea (viene de TodoWrite / todo_list del CLI).</summary>
    public class TaskStep
    {
        public string Text { get; set; } = "";
        public StepStatus Status { get; set; }
        [JsonIgnore] public string Icon => Status switch { StepStatus.Completed => "", StepStatus.InProgress => "", _ => "" };
    }

    /// <summary>Una tarea del agente: cada mensaje enviado (o ejecución programada) es una tarea.</summary>
    public class AgentTask : INotifyPropertyChanged
    {
        private AgentTaskStatus _status;
        private List<TaskStep> _steps = new();

        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Prompt { get; set; } = "";
        public DateTime Created { get; set; } = DateTime.Now;
        public DateTime? Finished { get; set; }
        public bool FromSchedule { get; set; }
        /// <summary>Resultado en una línea (primera línea de la respuesta final o el error).</summary>
        public string Summary { get; set; } = "";
        /// <summary>Resumen si existe; si no, lo que se pidió.</summary>
        [JsonIgnore] public string ResultOrTitle => string.IsNullOrWhiteSpace(Summary) ? Title : Summary;

        public AgentTaskStatus Status
        {
            get => _status;
            set { if (_status != value) { _status = value; OnChanged(); OnChanged(nameof(StatusIcon)); } }
        }

        public List<TaskStep> Steps
        {
            get => _steps;
            set { _steps = value ?? new(); OnChanged(); OnChanged(nameof(StepsSummary)); OnChanged(nameof(HasSteps)); }
        }

        [JsonIgnore] public string Title
        {
            get
            {
                var line = Prompt.Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim() != "")?.Trim() ?? "";
                return line.Length > 90 ? line[..90] + "…" : line;
            }
        }
        [JsonIgnore] public bool HasSteps => _steps.Count > 0;
        [JsonIgnore] public string StepsSummary => _steps.Count == 0 ? "" : $"{_steps.Count(s => s.Status == StepStatus.Completed)}/{_steps.Count} pasos";
        [JsonIgnore] public string StatusIcon => Status switch
        {
            AgentTaskStatus.Done => "",       // ✓
            AgentTaskStatus.Failed => "",     // !
            AgentTaskStatus.Cancelled => "",  // ×
            AgentTaskStatus.Running => "",    // ▶
            _ => ""                            // ○
        };
        [JsonIgnore] public string TimeLabel => (Finished ?? Created).ToString(Finished?.Date == DateTime.Today || Finished == null ? "HH:mm" : "dd MMM HH:mm");

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class Reminder
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Text { get; set; } = "";
        public DateTime DueAt { get; set; }
        public Recurrence Recurrence { get; set; } = Recurrence.None;
        public ReminderKind Kind { get; set; } = ReminderKind.Notify;
        public string? AgentId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [JsonIgnore] public string DueLabel => DueAt.ToString("ddd dd MMM, HH:mm");
        [JsonIgnore] public string KindLabel => Kind == ReminderKind.Notify ? "Aviso" : "Tarea del agente";
        [JsonIgnore] public string RecurrenceLabel => Recurrence switch
        {
            Recurrence.Hourly => "cada hora",
            Recurrence.Daily => "cada día",
            Recurrence.Weekdays => "lun–vie",
            Recurrence.Weekly => "cada semana",
            _ => "una vez"
        };

        public DateTime? NextOccurrence()
        {
            DateTime n = DueAt;
            DateTime now = DateTime.Now;
            do
            {
                switch (Recurrence)
                {
                    case Recurrence.Hourly: n = n.AddHours(1); break;
                    case Recurrence.Daily: n = n.AddDays(1); break;
                    case Recurrence.Weekly: n = n.AddDays(7); break;
                    case Recurrence.Weekdays:
                        n = n.AddDays(1);
                        while (n.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) n = n.AddDays(1);
                        break;
                    default: return null;
                }
            } while (n <= now);
            return n;
        }
    }

    public class AppSettings
    {
        public bool Sounds { get; set; } = true;
        public bool NotifyOnDone { get; set; } = true;
        public bool ExpandOnHover { get; set; } = true;
        /// <summary>
        /// Accesibilidad: true = el panel solo se cierra al hacer clic fuera (o Esc);
        /// false = se cierra al sacar el ratón / perder el foco.
        /// </summary>
        public bool CloseOnClickOutsideOnly { get; set; } = true;
        /// <summary>
        /// Accesibilidad: el notch plegado queda como una línea fina (crece un poco al acercar el ratón y se abre
        /// con un clic); los avisos de los agentes llegan como notificaciones de Windows.
        /// </summary>
        public bool HideNotch { get; set; }
        public string? SelectedAgentId { get; set; }
        /// <summary>Agente principal: se muestra grande en el notch.</summary>
        public string? DefaultAgentId { get; set; }
        /// <summary>Ya se creó el agente de código por defecto (para no recrearlo si el usuario lo borra).</summary>
        public bool SeededCodeAgent { get; set; }
        /// <summary>Última versión con la que se abrió: si al arrancar la versión es mayor, se muestran sus notas.</summary>
        public string? LastSeenVersion { get; set; }
        /// <summary>Ajustes de los plugins (claves propias de «Settings»; se conservan aunque falte el plugin).</summary>
        [JsonExtensionData]
        public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
    }

    public class AppConfig
    {
        public List<AgentProfile> Agents { get; set; } = new();
        public List<Reminder> Reminders { get; set; } = new();
        public AppSettings Settings { get; set; } = new();
    }

    /// <summary>Tool y Permission no salen en la conversación: van al panel de acciones bajo el personaje.</summary>
    public enum ChatRole { User, Agent, Tool, Notice, Error, Permission }

    public class ChatMessage : INotifyPropertyChanged
    {
        private string _text = "";
        public ChatRole Role { get; set; }
        public DateTime Time { get; set; } = DateTime.Now;
        public string Text
        {
            get => _text;
            set { if (_text != value) { _text = value; OnChanged(); } }
        }

        /// <summary>Solo en Permission: Allow, AllowAlways o Deny.</summary>
        public string? Decision { get; set; }

        private string? _action;
        /// <summary>Botón bajo un error (<see cref="ActionNewSession"/>); se quita al pulsarlo.</summary>
        public string? Action
        {
            get => _action;
            set { if (_action != value) { _action = value; OnChanged(); } }
        }
        /// <summary>Mensaje que se vuelve a enviar al pulsar el botón.</summary>
        public string? ActionPrompt { get; set; }
        public const string ActionNewSession = "NewSession";

        public ChatMessage() { }
        public ChatMessage(ChatRole role, string text) { Role = role; _text = text; }

        [JsonIgnore] public string TimeLabel => Time.ToString("HH:mm");

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class AgentHistory
    {
        public string? SessionId { get; set; }
        public List<ChatMessage> Messages { get; set; } = new();
        public List<AgentTask> Pending { get; set; } = new();
        public List<AgentTask> Completed { get; set; } = new();
    }
}
