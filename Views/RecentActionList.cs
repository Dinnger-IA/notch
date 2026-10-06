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
    /// Acciones de un agente de código, la más reciente arriba: se ven las <see cref="Count"/> primeras, cada vez más
    /// difusas, y el resto con scroll. La nueva entra deslizándose desde arriba y las demás bajan a su sitio (se anima
    /// la diferencia de posición). Al mirar las anteriores (<c>sharp</c>) todas se ven nítidas.
    /// </summary>
    public static class RecentActionList
    {
        public const int Count = 5;
        /// <summary>Opacidad y desenfoque de cada puesto: la más reciente nítida y el resto cada vez más difuso.</summary>
        private static readonly double[] Opacity = { 1, 0.72, 0.5, 0.32, 0.18 };
        private static readonly double[] Blur = { 0, 0, 0.6, 1.1, 1.6 };
        private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(320);

        private static double OpacityAt(int i, bool sharp) => sharp ? 1 : Opacity[Math.Min(i, Opacity.Length - 1)];
        private static double BlurAt(int i, bool sharp) => sharp ? 0 : Blur[Math.Min(i, Blur.Length - 1)];

        /// <param name="items">Las acciones, de la más reciente a la más antigua.</param>
        public static void Update(Panel panel, IReadOnlyList<object> items, DataTemplate template, bool animate, bool sharp = false)
        {
            var current = panel.Children.OfType<ContentPresenter>().ToList();
            if (current.Select(c => c.Content).SequenceEqual(items)) return;

            // Posición (visible, con la animación en curso) de cada fila antes del cambio
            var byContent = current.ToDictionary(c => c.Content);
            var before = current.Take(Count * 2).ToDictionary(c => c.Content, c => c.TranslatePoint(new Point(), panel).Y);
            var rows = items.Select(m => byContent.TryGetValue(m, out var c) ? c
                                         : new ContentPresenter { Content = m, ContentTemplate = template, Opacity = animate ? 0 : 1 }).ToList();
            panel.Children.Clear();
            foreach (var r in rows) panel.Children.Add(r);
            panel.UpdateLayout();

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var move = r.RenderTransform as TranslateTransform ?? new TranslateTransform();
                r.RenderTransform = move;
                SetBlur(r, BlurAt(i, sharp));
                // Solo se animan las de arriba (las que se ven); las demás van directas a su sitio
                if (!animate || i >= Count * 2)
                {
                    move.BeginAnimation(TranslateTransform.YProperty, null);
                    r.BeginAnimation(UIElement.OpacityProperty, null);
                    r.Opacity = OpacityAt(i, sharp);
                    continue;
                }
                double now = r.TranslatePoint(new Point(), panel).Y - move.Y;
                double from = before.TryGetValue(r.Content, out var y) ? y - now : -14; // nueva: entra desde arriba
                move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from, 0, Duration) { EasingFunction = ease });
                r.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(OpacityAt(i, sharp), Duration) { EasingFunction = ease });
            }
        }

        /// <summary>Nítidas todas (al mirar las anteriores) o difuminadas según su antigüedad.</summary>
        public static void SetSharp(Panel panel, bool sharp)
        {
            int i = 0;
            foreach (var r in panel.Children.OfType<ContentPresenter>())
            {
                SetBlur(r, BlurAt(i, sharp));
                r.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(OpacityAt(i, sharp), TimeSpan.FromMilliseconds(200)));
                i++;
            }
        }

        /// <summary>Alto de las <see cref="Count"/> primeras filas: lo que se ve sin desplazarse.</summary>
        public static double VisibleHeight(Panel panel)
        {
            var rows = panel.Children.OfType<FrameworkElement>().Take(Count).ToList();
            return rows.Count < Count ? double.PositiveInfinity
                : rows.Sum(r => r.ActualHeight + r.Margin.Top + r.Margin.Bottom) + 4; // + relleno del scroll
        }

        private static void SetBlur(UIElement r, double radius) => r.Effect = radius > 0 ? new BlurEffect { Radius = radius } : null;
    }
}
