using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using AgentManagerNotch.Models;

namespace AgentManagerNotch.Services
{
    /// <summary>
    /// Ejecuta los agentes programados cuando les toca. Si el equipo estuvo apagado, recupera
    /// una sola ejecución perdida en las últimas 12 horas; las más antiguas se saltan.
    /// </summary>
    public sealed class ScheduleService
    {
        private static readonly TimeSpan CatchUpWindow = TimeSpan.FromHours(12);
        private readonly Func<IEnumerable<AgentSession>> _sessions;
        private readonly Action _save;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(15) };

        public ScheduleService(Func<IEnumerable<AgentSession>> sessions, Action save)
        {
            _sessions = sessions;
            _save = save;
            _timer.Tick += (_, _) => Check();
        }

        public void Start() { Check(); _timer.Start(); }

        /// <summary>Recalcula la próxima ejecución (tras editar el horario o reanudar).</summary>
        public static void Reschedule(AgentSession s)
        {
            if (!s.IsScheduled) return;
            s.Profile.Schedule.NextRun = s.Profile.Schedule.ComputeNext(DateTime.Now);
            s.RaiseProfileChanged();
        }

        private void Check()
        {
            var now = DateTime.Now;
            bool changed = false;
            foreach (var s in _sessions().Where(x => x.IsScheduled).ToList())
            {
                var sc = s.Profile.Schedule;
                if (sc.Paused) continue;
                if (sc.NextRun == null)
                {
                    sc.NextRun = sc.ComputeNext(now);
                    s.RaiseProfileChanged();
                    changed = true;
                    continue;
                }
                if (now < sc.NextRun) continue;
                if (now - sc.NextRun > CatchUpWindow)
                {
                    sc.NextRun = sc.ComputeNext(now);
                    s.RaiseProfileChanged();
                    changed = true;
                    continue;
                }
                if (s.IsBusy) continue; // lo intentamos en el siguiente tick

                sc.NextRun = sc.ComputeNext(now);
                changed = true;
                _ = s.RunScheduledAsync();
            }
            if (changed) _save();
        }
    }
}
