using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using CoucouWin.Controls;
using CoucouWin.Models;
using CoucouWin.Providers;

namespace CoucouWin.Services
{
    /// <summary>
    /// Un agente tal como se ve en la interfaz. Los agentes de código tienen varias pestañas (workspaces),
    /// cada una con su carpeta y su sesión del CLI; el resto tiene exactamente una.
    /// </summary>
    public class AgentEntry : INotifyPropertyChanged
    {
        public AgentProfile Profile { get; private set; }
        public ObservableCollection<AgentSession> Tabs { get; } = new();

        public AgentEntry(AgentProfile profile)
        {
            Profile = profile;
            Tabs.CollectionChanged += Tabs_CollectionChanged;
        }

        private AgentSession? _active;
        /// <summary>Pestaña abierta en el chat.</summary>
        public AgentSession? ActiveTab
        {
            get => _active ?? Tabs.FirstOrDefault();
            set
            {
                if (_active == value) return;
                _active = value;
                foreach (var t in Tabs) t.IsSelected = t == value;
                OnChanged();
            }
        }

        private bool _isDefault;
        public bool IsDefault { get => _isDefault; set => Set(ref _isDefault, value); }

        private bool _isBox;
        public bool IsBox { get => _isBox; set => Set(ref _isBox, value); }

        public bool IsCode => Profile.Kind == AgentKind.Code;
        public bool IsScheduled => Profile.Kind == AgentKind.Scheduled;
        public bool IsConversational => !IsScheduled;
        public bool HasNoTabs => IsCode && Tabs.Count == 0;
        public Color Color => AgentSession.ParseColor(Profile.ColorHex);
        public string ProviderLabel => ProviderFactory.Label(Profile.Provider);
        public string Name => Profile.Name;

        /// <summary>Estado del personaje: el más llamativo de sus pestañas.</summary>
        public MochiState State
        {
            get
            {
                if (Tabs.Any(t => t.PendingApproval != null)) return MochiState.Waiting;
                var busy = Tabs.FirstOrDefault(t => t.IsBusy);
                if (busy != null) return busy.State;
                var alarm = Tabs.FirstOrDefault(t => t.State is MochiState.Alarm or MochiState.Done or MochiState.Error);
                return alarm?.State ?? MochiState.Idle;
            }
        }

        public int ActiveTabs => Tabs.Count(t => t.IsBusy || t.PendingApproval != null);

        /// <summary>Contador de la tarjeta: pestañas activas/total (código) o pasos (resto).</summary>
        public string Counter
        {
            get
            {
                if (IsCode) return Tabs.Count == 0 ? "" : $"{ActiveTabs}/{Tabs.Count}";
                var t = Tabs.FirstOrDefault();
                return t?.CurrentTask is { HasSteps: true } c ? c.StepsSummary.Replace(" pasos", "") : "";
            }
        }

        /// <summary>Chip de estado junto al nombre ("Error", "Permiso", "Trabajando", "En pausa").</summary>
        public string StatusChip
        {
            get
            {
                if (Tabs.Any(t => t.PendingApproval != null)) return "Permiso";
                if (Tabs.Any(t => t.IsBusy)) return "Trabajando";
                if (IsScheduled && Profile.Schedule.Paused) return "En pausa";
                if (HasError) return "Error";
                return "";
            }
        }
        public bool ChipIsError => StatusChip is "Error";
        public bool ChipIsAlert => StatusChip is "Permiso";

        /// <summary>Insignia roja en el chip pequeño.</summary>
        public bool HasError => Tabs.Any(t => t.LastFailed);
        public bool NeedsApproval => Tabs.Any(t => t.PendingApproval != null);
        public bool IsBusy => Tabs.Any(t => t.IsBusy);

        /// <summary>Texto bajo el nombre (CLI, nº de workspaces o próxima ejecución).</summary>
        public string SubLabel => IsCode ? $"{ProviderLabel} · {Tabs.Count} workspace{(Tabs.Count == 1 ? "" : "s")}"
            : IsScheduled ? Tabs.FirstOrDefault()?.ScheduleSummary ?? ""
            : ProviderLabel;

        /// <summary>Líneas de detalle de la tarjeta grande (máximo 3).</summary>
        public ObservableCollection<TaskLine> Lines { get; } = new();

        public void RebuildLines()
        {
            Lines.Clear();
            if (IsCode)
            {
                if (Tabs.Count == 0)
                {
                    Lines.Add(new TaskLine { Text = "Arrastra una carpeta para abrir un workspace", Tone = TaskTone.Pending });
                    return;
                }
                foreach (var t in Tabs.OrderByDescending(x => x.IsBusy || x.PendingApproval != null).ThenByDescending(x => x.LastActivity).Take(3))
                {
                    string text;
                    TaskTone tone;
                    if (t.PendingApproval != null || t.IsBusy) { text = $"{t.FolderName} · {t.CurrentLine}"; tone = TaskTone.Running; }
                    else if (t.LastTaskStatus == AgentTaskStatus.Failed) { text = $"{t.FolderName} · falló: {t.CompletedTasks[0].ResultOrTitle}"; tone = TaskTone.Failed; }
                    else if (t.LastTaskStatus is { } st) { text = $"{t.FolderName} · {t.CompletedTasks[0].ResultOrTitle}"; tone = st == AgentTaskStatus.Cancelled ? TaskTone.Cancelled : TaskTone.Done; }
                    else { text = $"{t.FolderName} · listo para empezar"; tone = TaskTone.Pending; }
                    Lines.Add(new TaskLine { Text = text, Detail = t.FolderPath, Tone = tone });
                }
                return;
            }

            var s = Tabs.FirstOrDefault();
            if (s == null) return;
            if (IsScheduled)
            {
                Lines.Add(new TaskLine
                {
                    Text = s.IsBusy ? s.InfoLine : s.LastOutputText, Detail = s.LastOutputText,
                    Tone = s.IsBusy ? TaskTone.Running : s.LastFailed ? TaskTone.Failed : TaskTone.Done
                });
                var when = string.Join("   ", new[] { s.LastOutputTimeLabel, s.NextRunLabel }.Where(x => !string.IsNullOrEmpty(x)));
                Lines.Add(new TaskLine { Text = when, Tone = TaskTone.Pending });
                return;
            }

            // Interactivo: última tarea terminada + lo que hace ahora (o lo último que dijo)
            var last = s.CompletedTasks.FirstOrDefault();
            if (last != null)
                Lines.Add(new TaskLine
                {
                    Text = last.ResultOrTitle, Detail = last.Prompt,
                    Tone = last.Status switch { AgentTaskStatus.Failed => TaskTone.Failed, AgentTaskStatus.Cancelled => TaskTone.Cancelled, _ => TaskTone.Done }
                });
            if (s.IsBusy || s.PendingApproval != null)
                Lines.Add(new TaskLine { Text = s.CurrentLine, Tone = TaskTone.Running });
            else if (s.Messages.Any(m => m.Role == ChatRole.Agent))
                Lines.Add(new TaskLine { Text = s.LastOutputText, Detail = s.LastOutputText, Tone = TaskTone.Pending });
            if (Lines.Count == 0) Lines.Add(new TaskLine { Text = "Sin actividad todavía", Tone = TaskTone.Pending });
            if (s.PendingCount > 0) Lines.Add(new TaskLine { Text = $"{s.PendingCount} en cola", Tone = TaskTone.Pending });
        }

        // ------------------------------------------------------------ propagación de cambios
        private void Tabs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null) foreach (AgentSession s in e.NewItems) s.PropertyChanged += Tab_PropertyChanged;
            if (e.OldItems != null) foreach (AgentSession s in e.OldItems) s.PropertyChanged -= Tab_PropertyChanged;
            if (_active != null && !Tabs.Contains(_active)) _active = null;
            RaiseAll();
        }

        private static readonly HashSet<string> Relevant = new()
        {
            nameof(AgentSession.State), nameof(AgentSession.IsBusy), nameof(AgentSession.PendingApproval),
            nameof(AgentSession.Activity), nameof(AgentSession.CurrentTask), nameof(AgentSession.LastActivity),
            nameof(AgentSession.LastOutputText), nameof(AgentSession.PendingCount), nameof(AgentSession.Profile),
            nameof(AgentSession.NextRunLabel), nameof(AgentSession.CurrentLine)
        };

        private void Tab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != null && Relevant.Contains(e.PropertyName)) RaiseAll();
        }

        public void UpdateProfile(AgentProfile p)
        {
            Profile = p;
            foreach (var t in Tabs) t.UpdateProfile(p);
            RaiseAll();
            OnChanged(nameof(Profile));
        }

        public void RaiseAll()
        {
            RebuildLines();
            foreach (var n in new[] { nameof(State), nameof(ActiveTabs), nameof(Counter), nameof(StatusChip), nameof(ChipIsError),
                         nameof(ChipIsAlert), nameof(HasError), nameof(NeedsApproval), nameof(IsBusy), nameof(SubLabel),
                         nameof(HasNoTabs), nameof(Color), nameof(Name), nameof(ActiveTab) })
                OnChanged(n);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        private bool Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value; OnChanged(n); return true;
        }
    }
}
