using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CoucouWin.Services;

namespace CoucouWin.Controls
{
    /// <summary>
    /// Grupo de agentes secundarios. En reposo se ven pequeños y superpuestos; al pasar el ratón
    /// el grupo se despliega y muestra cada agente completo con su nombre y su carpeta / próxima ejecución.
    /// </summary>
    public class AgentGroupView : Border
    {
        private readonly StackPanel _compact = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel _expanded = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Opacity = 0 };
        private readonly TextBlock _label = new()
        {
            FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x99)),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0)
        };
        private readonly DispatcherTimer _leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(260) };
        private IReadOnlyList<AgentSession> _items = Array.Empty<AgentSession>();
        private bool _isExpanded;

        public event Action<AgentSession>? AgentClicked;
        public event Action? AddClicked;
        public Func<AgentSession, ContextMenu>? MenuFactory { get; set; }
        public string Title { get; set; } = "";
        public bool ShowAddButton { get; set; }
        public string AddTooltip { get; set; } = "Añadir";
        /// <summary>Se dispara cuando el grupo cambia de tamaño (para recalcular el notch).</summary>
        public event Action? ExpandedChanged;

        public AgentGroupView()
        {
            CornerRadius = new CornerRadius(14);
            Padding = new Thickness(8, 4, 8, 4);
            Margin = new Thickness(4, 0, 4, 0);
            Background = new SolidColorBrush(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF));
            VerticalAlignment = VerticalAlignment.Center;
            var root = new Grid();
            var compactHost = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            compactHost.Children.Add(_compact);
            compactHost.Children.Add(_label);
            root.Children.Add(compactHost);
            root.Children.Add(_expanded);
            Child = root;
            _compactHost = compactHost;

            MouseEnter += (_, _) => { _leaveTimer.Stop(); SetExpanded(true); };
            MouseLeave += (_, _) => _leaveTimer.Start();
            _leaveTimer.Tick += (_, _) => { _leaveTimer.Stop(); if (!IsMouseOver && !IsContextMenuOpenInside()) SetExpanded(false); };
        }

        private readonly StackPanel _compactHost;

        private bool IsContextMenuOpenInside() =>
            _expanded.Children.OfType<FrameworkElement>().Any(c => c.ContextMenu?.IsOpen == true);

        public void SetItems(IReadOnlyList<AgentSession> items)
        {
            _items = items;
            Rebuild();
        }

        private void SetExpanded(bool on)
        {
            if (_isExpanded == on || _items.Count == 0) return;
            _isExpanded = on;
            _compactHost.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            _expanded.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on) _expanded.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            ExpandedChanged?.Invoke();
        }

        private void Rebuild()
        {
            _compact.Children.Clear();
            _expanded.Children.Clear();
            _label.Text = _items.Count == 0 ? Title : $"{Title} · {_items.Count}";

            int i = 0;
            foreach (var s in _items)
            {
                var mini = new MochiView { Width = 30, Height = 30, Interactive = false, Margin = new Thickness(i == 0 ? 0 : -11, 0, 0, 0) };
                Bind(mini, s);
                Panel.SetZIndex(mini, _items.Count - i);
                _compact.Children.Add(mini);
                _expanded.Children.Add(BuildTab(s, 40, 84));
                i++;
            }
            if (ShowAddButton)
            {
                var add = new Button
                {
                    Content = "", ToolTip = AddTooltip, Width = 30, Height = 30, Margin = new Thickness(_items.Count == 0 ? 0 : 4, 0, 0, 0),
                    Style = (Style)Application.Current.FindResource("IconButton"), VerticalAlignment = VerticalAlignment.Center
                };
                add.Click += (_, _) => AddClicked?.Invoke();
                var add2 = new Button
                {
                    Content = "", ToolTip = AddTooltip, Width = 36, Height = 36, Margin = new Thickness(4, 0, 0, 0),
                    Style = (Style)Application.Current.FindResource("IconButton"), VerticalAlignment = VerticalAlignment.Center
                };
                add2.Click += (_, _) => AddClicked?.Invoke();
                _compact.Children.Add(add);
                _expanded.Children.Add(add2);
            }
            if (_items.Count == 0) { _isExpanded = false; _compactHost.Visibility = Visibility.Visible; _expanded.Visibility = Visibility.Collapsed; }
        }

        public static void Bind(MochiView m, AgentSession s)
        {
            m.SetBinding(MochiView.BodyColorProperty, new Binding(nameof(AgentSession.Color)) { Source = s });
            m.SetBinding(MochiView.StateProperty, new Binding(nameof(AgentSession.State)) { Source = s });
            m.SetBinding(MochiView.IsBoxProperty, new Binding(nameof(AgentSession.IsBox)) { Source = s });
            m.DataContext = s;
        }

        /// <summary>Pestaña completa: personaje + nombre + subtítulo (carpeta, próxima ejecución o CLI).</summary>
        public FrameworkElement BuildTab(AgentSession s, double size, double width)
        {
            var tab = new Border
            {
                Width = width, CornerRadius = new CornerRadius(12), Padding = new Thickness(2, 2, 2, 5),
                Margin = new Thickness(1, 0, 1, 0), Cursor = Cursors.Hand, DataContext = s, ToolTip = TooltipFor(s)
            };
            tab.SetBinding(BackgroundProperty, new Binding(nameof(AgentSession.IsSelected)) { Source = s, Converter = SelectedBrush.Instance });
            var sp = new StackPanel();
            var m = new MochiView { Width = size, Height = size, Interactive = true, HorizontalAlignment = HorizontalAlignment.Center };
            Bind(m, s);
            sp.Children.Add(m);
            var name = new TextBlock
            {
                FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xF2)), TextTrimming = TextTrimming.CharacterEllipsis
            };
            name.SetBinding(TextBlock.TextProperty, new Binding("Profile.Name") { Source = s });
            sp.Children.Add(name);
            var sub = new TextBlock
            {
                FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x99))
            };
            sub.SetBinding(TextBlock.TextProperty, new Binding(nameof(AgentSession.SubLabel)) { Source = s });
            sp.Children.Add(sub);
            tab.Child = sp;
            tab.MouseLeftButtonUp += (_, e) => { AgentClicked?.Invoke(s); e.Handled = true; };
            if (MenuFactory != null) tab.ContextMenu = MenuFactory(s);
            return tab;
        }

        private static string TooltipFor(AgentSession s) =>
            s.IsCode ? $"{s.Profile.Name} · {(s.HasFolder ? s.FolderPath : "sin carpeta")}"
            : s.IsScheduled ? $"{s.Profile.Name} · {s.ScheduleSummary}"
            : $"{s.Profile.Name} · {s.ProviderLabel}";

        private sealed class SelectedBrush : IValueConverter
        {
            public static readonly SelectedBrush Instance = new();
            private static readonly Brush On = Freeze(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)));
            private static readonly Brush Off = Brushes.Transparent;
            private static Brush Freeze(Brush b) { b.Freeze(); return b; }
            public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? On : Off;
            public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
        }
    }
}
