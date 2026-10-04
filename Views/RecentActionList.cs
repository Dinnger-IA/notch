using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace AgentManagerNotch.Views
{
    /// <summary>
    /// Lista corta de las últimas acciones de un agente de código, la más reciente arriba. La nueva entra deslizándose
    /// desde arriba y las demás bajan a su sitio (se anima la diferencia de posición), cada vez más difusas.
    /// </summary>
    public static class RecentActionList
    {
        public const int Count = 5;
        /// <summary>Opacidad y desenfoque de cada puesto: la más reciente nítida y el resto cada vez más difuso.</summary>
        private static readonly double[] Opacity = { 1, 0.72, 0.5, 0.32, 0.18 };
        private static readonly double[] Blur = { 0, 0, 0.6, 1.1, 1.6 };

        /// <param name="latest">Las acciones a mostrar, de la más reciente a la más antigua (como mucho <see cref="Count"/>).</param>
        public static void Update(Panel panel, IReadOnlyList<object> latest, DataTemplate template, bool animate)
        {
            var current = panel.Children.OfType<ContentPresenter>().ToList();
            if (current.Select(c => c.Content).SequenceEqual(latest)) return;

            // Posición (visible, con la animación en curso) de cada fila antes del cambio
            var before = current.ToDictionary(c => c.Content, c => c.TranslatePoint(new Point(), panel).Y);
            var rows = latest.Select(m => current.FirstOrDefault(c => c.Content == m)
                                          ?? new ContentPresenter { Content = m, ContentTemplate = template, Opacity = animate ? 0 : 1 }).ToList();
            panel.Children.Clear();
            foreach (var r in rows) panel.Children.Add(r);
            panel.UpdateLayout();

            var dur = TimeSpan.FromMilliseconds(320);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var move = r.RenderTransform as TranslateTransform ?? new TranslateTransform();
                r.RenderTransform = move;
                r.Effect = Blur[i] > 0 ? new BlurEffect { Radius = Blur[i] } : null;
                if (!animate)
                {
                    move.BeginAnimation(TranslateTransform.YProperty, null);
                    r.BeginAnimation(UIElement.OpacityProperty, null);
                    r.Opacity = Opacity[i];
                    continue;
                }
                double now = r.TranslatePoint(new Point(), panel).Y - move.Y;
                double from = before.TryGetValue(r.Content, out var y) ? y - now : -14; // nueva: entra desde arriba
                move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from, 0, dur) { EasingFunction = ease });
                r.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(Opacity[i], dur) { EasingFunction = ease });
            }
        }
    }
}
