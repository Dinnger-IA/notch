using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AgentManagerNotch.Models;
using AgentManagerNotch.Plugins;
using AgentManagerNotch.Services;

namespace AgentManagerNotch.Views
{
    /// <summary>
    /// Lo que el notch monta de los plugins (ver <see cref="NotchHost"/>): puertas de acceso, tarjetas de
    /// configuración y extensiones del chat. Sin plugins no añade nada.
    /// </summary>
    public partial class NotchWindow : IComposerView
    {
        private static NotchHost? PluginHostRef => PluginHost.Host;
        /// <summary>La primera puerta cerrada (null si todas están abiertas o no hay ninguna).</summary>
        private static IAccessGate? ClosedGate => PluginHostRef?.Gates.FirstOrDefault(g => !g.IsOpen);
        /// <summary>Se pueden ver y usar los agentes (ninguna puerta de acceso cerrada).</summary>
        private static bool Unlocked => ClosedGate == null;
        private IAccessGate? _shownGate;
        private string _defaultPlaceholder = "";

        private void InitPlugins()
        {
            _defaultPlaceholder = Placeholder.Text;
            var host = PluginHostRef;
            if (host == null) return;
            foreach (var g in host.Gates) g.Changed += () => Dispatcher.BeginInvoke(OnGateChanged);
            foreach (var (card, column, _) in host.SettingsCards)
                (column == SettingsColumn.Left ? PluginSettingsLeft : PluginSettingsRight).Children.Add(card);
            foreach (var b in host.Composer.InputButtons) InputExtras.Children.Add(b);
            host.Composer.View = this;
        }

        // =============================================================== puertas de acceso
        private void ShowGate()
        {
            var gate = ClosedGate;
            if (gate == null) { ShowOverview(); return; }
            _hoverTimer.Stop();
            _bannerTimer.Stop();
            if (_shownGate != gate)
            {
                _shownGate = gate;
                GateView.Content = gate.View;
            }
            PluginHost.Safe("Gate.OnShow", gate.OnShow);
            if (_mode != Mode.Gate) SetMode(Mode.Gate);
            else Resize();
        }

        /// <summary>Una puerta de acceso se abrió o se cerró.</summary>
        private void OnGateChanged()
        {
            RebuildPill();
            if (Unlocked)
            {
                if (_mode == Mode.Gate) { SetFocused(Principal); ShowOverview(); Activate(); }
                return;
            }
            _openEntry = null;
            _reminders?.Close();
            if (IsPanel(_mode)) ShowGate();
            else if (_mode == Mode.Banner) Collapse();
        }

        /// <summary>Píldora con la puerta cerrada: candado y su texto en lugar de los personajes.</summary>
        private void BuildLockedPill(IAccessGate gate)
        {
            var locked = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            locked.Children.Add(new TextBlock
            {
                Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12,
                Foreground = (Brush)FindResource("TextSecondary"), VerticalAlignment = VerticalAlignment.Center
            });
            locked.Children.Add(new TextBlock
            {
                Text = gate.LockedLabel, Margin = new Thickness(8, 0, 0, 0), FontSize = 12,
                Foreground = (Brush)FindResource("TextSecondary"), VerticalAlignment = VerticalAlignment.Center
            });
            CollapsedAgents.Children.Add(locked);
        }

        /// <summary>Con la puerta cerrada, ⚙ solo ofrece salir.</summary>
        private void ShowLockedMenu()
        {
            var menu = new ContextMenu { PlacementTarget = SettingsButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            var exit = new MenuItem { Header = "Salir de Agent Manager Notch" };
            exit.Click += (_, _) => AppRef.ExitApp();
            menu.Items.Add(exit);
            OpenMenu(menu);
        }

        /// <summary>Pregunta Sí/No sin que el panel se cierre mientras tanto.</summary>
        public bool Confirm(string message)
        {
            _modalOpen = true;
            var ok = MessageBox.Show(this, message, "Agent Manager Notch", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
            _modalOpen = false;
            Activate();
            return ok;
        }

        // =============================================================== configuración
        private void RefreshPluginCards()
        {
            var host = PluginHostRef;
            if (host == null) return;
            foreach (var (_, _, refresh) in host.SettingsCards)
                if (refresh != null) PluginHost.Safe("SettingsCard", () => refresh(_settingsTarget));
        }

        /// <summary>Un plugin cambió algo de su tarjeta: se vuelve a cargar la configuración si está abierta.</summary>
        public void RefreshPluginSettings()
        {
            RebuildPill();
            if (!Unlocked) { if (IsPanel(_mode)) ShowGate(); return; }
            if (_mode == Mode.Settings) { RefreshSettings(); Resize(); }
        }

        // =============================================================== chat (IComposerView)
        AgentSession? IComposerView.Selected => _mode == Mode.Chat ? _selected : null;
        string IComposerView.Text { get => Input.Text; set => Input.Text = value; }
        Task IComposerView.SendAsync() => SendCurrent();
        void IComposerView.FocusInput() => FocusInput();

        private UIElement? _overlay;
        void IComposerView.SetOverlay(UIElement? overlay)
        {
            _overlay = overlay;
            InputOverlay.Content = overlay;
            InputOverlay.Visibility = overlay != null ? Visibility.Visible : Visibility.Collapsed;
            Input.Visibility = overlay != null ? Visibility.Hidden : Visibility.Visible;
            UpdatePlaceholder();
        }

        void IComposerView.SetPlaceholder(string? text) => Placeholder.Text = text ?? _defaultPlaceholder;
        void IComposerView.SetHighlight(Brush? brush) => InputBar.BorderBrush = brush ?? Brushes.Transparent;

        private void UpdatePlaceholder() =>
            Placeholder.Visibility = Input.Text.Length == 0 && _overlay == null ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Teclas que atienden los plugins (antes que Intro y Esc).</summary>
        private bool HandlePluginKey(Key key)
        {
            var host = PluginHostRef;
            if (host == null) return false;
            foreach (var h in host.Composer.KeyHandlers)
            {
                bool handled = false;
                PluginHost.Safe("KeyHandler", () => handled = h(key));
                if (handled) return true;
            }
            return false;
        }

        /// <summary>Añade al menú de un mensaje las acciones de los plugins (una vez por menú).</summary>
        private void MessageMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is not ContextMenu menu || menu.Tag is "plugins" || PluginHostRef is not { } host) return;
            menu.Tag = "plugins";
            foreach (var (header, run) in host.Composer.MessageActions)
            {
                var item = new MenuItem { Header = header };
                item.Click += (s, _) =>
                {
                    if ((s as FrameworkElement)?.DataContext is ChatMessage m && _selected is { } session)
                        PluginHost.Safe(header, () => run(session, m));
                };
                menu.Items.Add(item);
            }
        }
    }
}
