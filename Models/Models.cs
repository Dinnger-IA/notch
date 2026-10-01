using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace CoucouWin.Models
{
    /// <summary>CLI que ejecuta al agente por debajo.</summary>
    public enum ProviderKind { ClaudeCode, Codex, Gemini, Custom }

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

    public class AgentProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Mochi";
        public string ColorHex { get; set; } = "#FF8A65";
        public string SystemPrompt { get; set; } = "";
        public ProviderKind Provider { get; set; } = ProviderKind.ClaudeCode;
        public string Model { get; set; } = "";
        public string WorkingDirectory { get; set; } = "";
        public ApprovalMode Approval { get; set; } = ApprovalMode.Ask;
        public string ExtraArgs { get; set; } = "";
        /// <summary>Para Provider=Custom. Marcadores: {prompt} {system} {model}. Si no hay {prompt}, se envía por stdin.</summary>
        public string CustomCommand { get; set; } = "";

        public AgentProfile Clone() => (AgentProfile)MemberwiseClone();
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
        public string? SelectedAgentId { get; set; }
    }

    public class AppConfig
    {
        public List<AgentProfile> Agents { get; set; } = new();
        public List<Reminder> Reminders { get; set; } = new();
        public AppSettings Settings { get; set; } = new();
    }

    public enum ChatRole { User, Agent, Tool, Notice, Error }

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
    }
}
