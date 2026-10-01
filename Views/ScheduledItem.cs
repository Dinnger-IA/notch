using System;
using System.Windows.Media;
using AgentManagerNotch.Services;

namespace AgentManagerNotch.Views
{
    /// <summary>Elemento de la vista de tareas programadas: un recordatorio/tarea o un agente programado.</summary>
    public sealed class ScheduledItem
    {
        public string Id { get; init; } = "";
        public Color Color { get; init; } = Color.FromRgb(0x8C, 0x8C, 0x99);
        public string Title { get; init; } = "";
        public string When { get; init; } = "";
        public string Meta { get; init; } = "";
        public string Tooltip { get; init; } = "";
        public bool IsReminder { get; init; }
        public AgentEntry? Entry { get; init; }
        public DateTime SortKey { get; init; }

        public static string FormatWhen(DateTime t)
        {
            var today = DateTime.Today;
            if (t.Date == today) return "Hoy " + t.ToString("HH:mm");
            if (t.Date == today.AddDays(1)) return "Mañana " + t.ToString("HH:mm");
            if (t.Date == today.AddDays(-1)) return "Ayer " + t.ToString("HH:mm");
            return t.ToString("ddd dd MMM, HH:mm");
        }
    }
}
