using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CoucouWin.Controls;
using CoucouWin.Models;
using CoucouWin.Services;

namespace CoucouWin.Views
{
    public partial class NotchWindow : Window
    {
        private enum Mode { Collapsed, Banner, Expanded }

        private Mode _mode = Mode.Collapsed;
        private bool _pinned, _modalOpen, _autoScroll = true;
        private AgentSession? _selected;
        private AgentSession? _bannerSession;
        private string? _bannerAgentId;
        private RemindersWindow? _reminders;

        private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
        private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
        private readonly DispatcherTimer _bannerTimer = new() { Interval = TimeSpan.FromSeconds(5) };

        private static App AppRef => App.Current;

        public NotchWindow()
        {
            InitializeComponent();

            MiniAgents.ItemsSource = AppRef.Sessions;
            AgentTabs.ItemsSource = AppRef.Sessions;
            foreach (var s in AppRef.Sessions) s.PropertyChanged += Session_PropertyChanged;
            AppRef.Sessions.CollectionChanged += Sessions_CollectionChanged;

            var initial = AppRef.Sessions.FirstOrDefault(s => s.Profile.Id == AppRef.Store.Config.Settings.SelectedAgentId)
                          ?? AppRef.Sessions.FirstOrDefault();
            if (initial != null) Select(initial);

            _hoverTimer.Tick += (_, _) =>
            {
                _hoverTimer.Stop();
                if (_mode != Mode.Expanded && AppRef.Store.Config.Settings.ExpandOnHover && Shell.IsMouseOver) Expand(false);
            };
            _collapseTimer.Tick += (_, _) =>
            {
                _collapseTimer.Stop();
                if (_mode == Mode.Expanded && !_pinned && !_modalOpen && !IsActive && !Shell.IsMouseOver && _selected?.PendingApproval == null) Collapse();
            };
            _bannerTimer.Tick += (_, _) =>
            {
                _bannerTimer.Stop();
                if (_mode == Mode.Banner && !Shell.IsMouseOver) Collapse();
            };

            ChatScroll.ScrollChanged += ChatScroll_ScrollChanged;
            Deactivated += (_, _) =>
            {
                // Con un permiso pendiente el panel se queda abierto: el usuario tiene que decidir
                if (_mode == Mode.Expanded && !_pinned && !_modalOpen && !Shell.IsMouseOver && _selected?.PendingApproval == null) Collapse();
            };
            SourceInitialized += OnSourceInitialized;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.Invoke(PositionOnScreen);
            Loaded += (_, _) => { PositionOnScreen(); SetMode(Mode.Collapsed, animate: false); };
        }

        // =============================================================== modos y tamaños
        private void PositionOnScreen()
        {
            Left = SystemParameters.PrimaryScreenWidth / 2 - Width / 2;
            Top = 0;
        }

        private Size TargetSize(Mode m)
        {
            switch (m)
            {
                case Mode.Expanded: return new Size(600, 640);
                case Mode.Banner: return new Size(450, 74);
                default:
                    // Se mide el contenido directamente: CollapsedView puede estar oculto en este momento
                    CollapsedContent.Measure(new Size(double.PositiveInfinity, 40));
                    return new Size(Math.Max(110, CollapsedContent.DesiredSize.Width + 34), 40);
            }
        }

        private void SetMode(Mode m, bool animate = true)
        {
            var prev = _mode;
            _mode = m;
            var size = TargetSize(m);

            if (animate)
            {
                bool growing = m == Mode.Expanded || (m == Mode.Banner && prev == Mode.Collapsed);
                IEasingFunction ease = growing
                    ? new BackEase { Amplitude = 0.28, EasingMode = EasingMode.EaseOut }
                    : new CubicEase { EasingMode = EasingMode.EaseOut };
                var dur = TimeSpan.FromMilliseconds(growing ? 340 : 230);
                Shell.BeginAnimation(WidthProperty, new DoubleAnimation(size.Width, dur) { EasingFunction = ease });
                Shell.BeginAnimation(HeightProperty, new DoubleAnimation(size.Height, dur) { EasingFunction = ease });
            }
            else
            {
                Shell.BeginAnimation(WidthProperty, null);
                Shell.BeginAnimation(HeightProperty, null);
                Shell.Width = size.Width; Shell.Height = size.Height;
            }
            Shell.CornerRadius = m == Mode.Expanded ? new CornerRadius(0, 0, 26, 26) : new CornerRadius(0, 0, 22, 22);

            ShowView(CollapsedView, m == Mode.Collapsed);
            ShowView(BannerView, m == Mode.Banner);
            ShowView(ExpandedView, m == Mode.Expanded);
        }

        private static void ShowView(FrameworkElement el, bool show)
        {
            if (show)
            {
                el.Visibility = Visibility.Visible;
                el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { BeginTime = TimeSpan.FromMilliseconds(90) });
            }
            else
            {
                el.BeginAnimation(OpacityProperty, null);
                el.Opacity = 0;
                el.Visibility = Visibility.Collapsed;
            }
        }

        public void Expand(bool focusInput, string? agentId = null)
        {
            _bannerTimer.Stop();
            if (agentId != null)
            {
                var s = AppRef.Sessions.FirstOrDefault(x => x.Profile.Id == agentId);
                if (s != null) Select(s);
            }
            if (_mode != Mode.Expanded) SetMode(Mode.Expanded);
            if (focusInput)
            {
                Activate();
                Dispatcher.BeginInvoke(() => { Input.Focus(); Keyboard.Focus(Input); }, DispatcherPriority.Input);
            }
            ScrollToEnd();
        }

        public void Collapse()
        {
            if (_mode == Mode.Collapsed) { UpdateCollapsedSize(); return; }
            foreach (var s in AppRef.Sessions) s.IsBox = false;
            SetMode(Mode.Collapsed);
        }

        public bool IsLookingAt(AgentSession s) => _mode == Mode.Expanded && _selected == s;

        public void ShowBanner(AgentSession s, string title, string body, bool sticky = false)
        {
            if (_mode == Mode.Expanded) return; // el usuario ya está mirando el panel
            ShowBannerRaw(s.Color, s.State == MochiState.Idle ? MochiState.Done : s.State, title, body, s.Profile.Id, sticky);
            _bannerSession = s;
        }

        /// <summary>Aviso en el notch con un personaje de cualquier color (también para eventos externos).</summary>
        public void ShowBannerRaw(System.Windows.Media.Color color, MochiState state, string title, string body, string? agentId, bool sticky)
        {
            if (_mode == Mode.Expanded) return; // panel abierto: basta con el toast y la animación del personaje
            _bannerSession = null;
            _bannerAgentId = agentId;
            BannerMochi.BodyColor = color;
            BannerMochi.State = state;
            BannerTitle.Text = title;
            BannerBody.Text = body;
            if (_mode != Mode.Banner) SetMode(Mode.Banner);
            else if (BannerMochi.State == MochiState.Done) BannerMochi.Celebrate();
            _bannerTimer.Stop();
            _bannerTimer.Interval = TimeSpan.FromSeconds(sticky ? 15 : 6);
            _bannerTimer.Start();
        }

        private void UpdateCollapsedSize()
        {
            var busy = AppRef.Sessions.FirstOrDefault(s => s.PendingApproval != null)
                       ?? AppRef.Sessions.FirstOrDefault(s => s.IsBusy);
            CollapsedStatus.Text = busy == null ? "" :
                busy.PendingApproval != null ? $"{busy.Profile.Name} necesita permiso" : $"{busy.Profile.Name}: {busy.Activity}";
            CollapsedStatus.Visibility = busy == null ? Visibility.Collapsed : Visibility.Visible;
            if (_mode != Mode.Collapsed) return;
            var size = TargetSize(Mode.Collapsed);
            if (Math.Abs(Shell.ActualWidth - size.Width) > 3)
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                Shell.BeginAnimation(WidthProperty, new DoubleAnimation(size.Width, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            }
        }

        // =============================================================== selección / sesiones
        private void Select(AgentSession s)
        {
            if (_selected != null)
            {
                _selected.IsSelected = false;
                _selected.Messages.CollectionChanged -= Messages_CollectionChanged;
            }
            _selected = s;
            s.IsSelected = true;
            s.Messages.CollectionChanged += Messages_CollectionChanged;
            ExpandedView.DataContext = s;
            UpdateEmptyChat();
            _autoScroll = true;
            ScrollToEnd();
            AppRef.Store.Config.Settings.SelectedAgentId = s.Profile.Id;
        }

        private void Sessions_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null) foreach (AgentSession s in e.NewItems) s.PropertyChanged += Session_PropertyChanged;
            if (e.OldItems != null) foreach (AgentSession s in e.OldItems) s.PropertyChanged -= Session_PropertyChanged;
            Dispatcher.BeginInvoke(UpdateCollapsedSize, DispatcherPriority.Loaded);
        }

        private void Session_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(AgentSession.Activity) or nameof(AgentSession.IsBusy) or nameof(AgentSession.PendingApproval))
                UpdateCollapsedSize();
            if (sender == _bannerSession && e.PropertyName == nameof(AgentSession.State) && _mode == Mode.Banner)
                BannerMochi.State = _bannerSession!.State;
        }

        private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyChat();

        private void UpdateEmptyChat() =>
            EmptyChat.Visibility = _selected == null || _selected.Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void ChatScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.ExtentHeightChange == 0) _autoScroll = ChatScroll.VerticalOffset >= ChatScroll.ScrollableHeight - 4;
            else if (_autoScroll) ChatScroll.ScrollToVerticalOffset(ChatScroll.ExtentHeight);
        }

        private void ScrollToEnd() => Dispatcher.BeginInvoke(() => ChatScroll.ScrollToEnd(), DispatcherPriority.Background);

        // =============================================================== ratón / hover
        private void Shell_MouseEnter(object sender, MouseEventArgs e)
        {
            _collapseTimer.Stop();
            if (_mode != Mode.Expanded) _hoverTimer.Start();
        }

        private void Shell_MouseLeave(object sender, MouseEventArgs e)
        {
            _hoverTimer.Stop();
            if (_mode == Mode.Expanded && !_pinned && !IsActive) _collapseTimer.Start();
            if (_mode == Mode.Banner && !_bannerTimer.IsEnabled) _bannerTimer.Start();
        }

        private void Collapsed_Click(object sender, MouseButtonEventArgs e)
        {
            // Clic en un personaje mini → abre su chat
            if (e.OriginalSource is DependencyObject d)
            {
                var fe = FindAncestor<MochiView>(d);
                if (fe?.DataContext is AgentSession s) Select(s);
            }
            Expand(focusInput: true);
        }

        private void Banner_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) != null) return;
            Expand(focusInput: true, agentId: _bannerAgentId);
        }

        private void BannerClose_Click(object sender, RoutedEventArgs e) => Collapse();

        private void AgentTab_Click(object sender, MouseButtonEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is AgentSession s)
            {
                Select(s);
                Activate();
                Input.Focus();
            }
        }

        private void Tabs_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            var sv = (ScrollViewer)sender;
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta / 3.0);
            e.Handled = true;
        }

        // =============================================================== chat
        private async void Send_Click(object sender, RoutedEventArgs e) => await SendCurrent();

        private async System.Threading.Tasks.Task SendCurrent()
        {
            if (_selected == null) return;
            var text = Input.Text;
            if (string.IsNullOrWhiteSpace(text) || _selected.IsBusy) return;
            Input.Clear();
            _autoScroll = true;
            await _selected.SendAsync(text);
        }

        private async void Input_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                await SendCurrent();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                _pinned = false; UpdatePin();
                Collapse();
            }
        }

        private void Input_TextChanged(object sender, TextChangedEventArgs e) =>
            Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void Stop_Click(object sender, RoutedEventArgs e) => _selected?.Cancel();

        private void CopyMessage_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ChatMessage m)
                try { Clipboard.SetText(m.Text); } catch { }
        }
        private void NewChat_Click(object sender, RoutedEventArgs e) => _selected?.NewConversation();

        private void Attach_Click(object sender, RoutedEventArgs e)
        {
            _modalOpen = true;
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Adjuntar archivos para el agente" };
                if (dlg.ShowDialog(this) == true) AppendFiles(dlg.FileNames);
            }
            finally { _modalOpen = false; }
        }

        private void AppendFiles(string[] files)
        {
            if (files.Length == 0) return;
            var block = "Archivos:\n" + string.Join("\n", files.Select(f => "- " + f));
            Input.Text = string.IsNullOrWhiteSpace(Input.Text) ? block + "\n\n" : Input.Text.TrimEnd() + "\n\n" + block + "\n\n";
            Input.CaretIndex = Input.Text.Length;
            Activate();
            Input.Focus();
        }

        // =============================================================== aprobaciones
        private void Allow_Click(object sender, RoutedEventArgs e) => _selected?.Decide(ApprovalDecision.Allow);
        private void AllowAlways_Click(object sender, RoutedEventArgs e) => _selected?.Decide(ApprovalDecision.AllowAlways);
        private void Deny_Click(object sender, RoutedEventArgs e) => _selected?.Decide(ApprovalDecision.Deny);

        // =============================================================== arrastrar archivos
        private void Shell_DragEnter(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.None; return; }
            e.Effects = DragDropEffects.Copy;
            if (_mode != Mode.Expanded) Expand(false);
            if (_selected != null) _selected.IsBox = true;
        }

        private void Shell_DragLeave(object sender, DragEventArgs e)
        {
            if (!Shell.IsMouseOver) foreach (var s in AppRef.Sessions) s.IsBox = false;
        }

        private void Shell_Drop(object sender, DragEventArgs e)
        {
            foreach (var s in AppRef.Sessions) s.IsBox = false;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AppendFiles(files);
        }

        private void AgentTab_DragEnter(object sender, DragEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is AgentSession s && s != _selected)
            {
                if (_selected != null) _selected.IsBox = false;
                Select(s);
                s.IsBox = true;
            }
        }

        private void AgentTab_Drop(object sender, DragEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is AgentSession s) Select(s);
        }

        // =============================================================== agentes
        private void AddAgent_Click(object sender, RoutedEventArgs e) => OpenEditor(null);
        private void EditSelected_Click(object sender, RoutedEventArgs e) { if (_selected != null) OpenEditor(_selected); }

        private static AgentSession? MenuSession(object sender) => (sender as FrameworkElement)?.DataContext as AgentSession;
        private void MenuEdit_Click(object sender, RoutedEventArgs e) { if (MenuSession(sender) is { } s) OpenEditor(s); }
        private void MenuNewChat_Click(object sender, RoutedEventArgs e) => MenuSession(sender)?.NewConversation();

        private void MenuDuplicate_Click(object sender, RoutedEventArgs e)
        {
            if (MenuSession(sender) is not { } s) return;
            var p = s.Profile.Clone();
            p.Id = Guid.NewGuid().ToString("N");
            p.Name += " 2";
            var ns = AppRef.CreateSession(p);
            AppRef.Sessions.Add(ns);
            AppRef.SaveAgents();
            Select(ns);
        }

        private void MenuDelete_Click(object sender, RoutedEventArgs e)
        {
            if (MenuSession(sender) is not { } s) return;
            if (AppRef.Sessions.Count <= 1)
            {
                s.AddNotice("Necesitas al menos un agente.", true);
                return;
            }
            _modalOpen = true;
            var ok = MessageBox.Show(this, $"¿Eliminar a {s.Profile.Name} y su conversación?", "Coucou",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            _modalOpen = false;
            if (!ok) return;
            s.Cancel();
            AppRef.Sessions.Remove(s);
            AppRef.Store.DeleteHistory(s.Profile.Id);
            AppRef.SaveAgents();
            if (_selected == s) Select(AppRef.Sessions.First());
        }

        public void OpenEditor(AgentSession? session)
        {
            _modalOpen = true;
            try
            {
                if (_mode != Mode.Expanded) SetMode(Mode.Expanded);
                var win = new AgentEditorWindow(session?.Profile.Clone()) { Owner = this };
                if (win.ShowDialog() != true || win.Result == null) return;
                var p = win.Result;
                if (session == null)
                {
                    var ns = AppRef.CreateSession(p);
                    AppRef.Sessions.Add(ns);
                    Select(ns);
                    ns.AddNotice($"¡Hola! Soy {p.Name}. Funciono con {ns.ProviderLabel}.");
                }
                else
                {
                    bool providerChanged = session.Profile.Provider != p.Provider;
                    session.UpdateProfile(p);
                    if (providerChanged) session.NewConversation();
                }
                AppRef.SaveAgents();
            }
            finally
            {
                _modalOpen = false;
                Activate();
            }
        }

        public void OpenReminders()
        {
            if (_reminders == null || !_reminders.IsLoaded)
            {
                _reminders = new RemindersWindow();
                _reminders.Closed += (_, _) => _reminders = null;
                _reminders.Show();
            }
            _reminders.Activate();
        }

        private void Reminders_Click(object sender, RoutedEventArgs e) => OpenReminders();

        private void Pin_Click(object sender, RoutedEventArgs e)
        {
            _pinned = !_pinned;
            UpdatePin();
        }

        private void UpdatePin()
        {
            PinButton.Content = _pinned ? "" : "";
            PinButton.ToolTip = _pinned ? "Desfijar panel" : "Fijar panel abierto";
        }

        // =============================================================== Win32: tool window + atajo global
        private const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
        private const int WM_HOTKEY = 0x0312, HOTKEY_ID = 0xC0C0;
        private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000, VK_SPACE = 0x20;

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr h, int id);

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE, (GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);
            if (!RegisterHotKey(hwnd, HOTKEY_ID, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_SPACE))
                Log.Info("No se pudo registrar Ctrl+Alt+Espacio (¿lo usa otra app?)");
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            Closed += (_, _) => UnregisterHotKey(hwnd, HOTKEY_ID);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                handled = true;
                if (_mode == Mode.Expanded && IsActive) { _pinned = false; UpdatePin(); Collapse(); }
                else Expand(focusInput: true);
            }
            return IntPtr.Zero;
        }

        private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T t) return t;
                d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }
    }
}
