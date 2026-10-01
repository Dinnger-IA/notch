using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CoucouWin.Controls;
using CoucouWin.Models;
using CoucouWin.Services;

namespace CoucouWin.Views
{
    /// <summary>
    /// El notch. Modos:
    ///   Collapsed → píldora con los personajes
    ///   Banner    → aviso temporal
    ///   Overview  → tarjeta grande del agente enfocado (izquierda) + el resto en chips (derecha)
    ///   Chat      → conversación; los agentes de código muestran sus workspaces como pestañas
    /// </summary>
    public partial class NotchWindow : Window
    {
        private enum Mode { Collapsed, Banner, Overview, Chat }

        private Mode _mode = Mode.Collapsed;
        private bool _pinned, _modalOpen, _autoScroll = true, _rebuildQueued;
        private AgentEntry? _focused;      // agente de la tarjeta grande
        private AgentEntry? _openEntry;    // agente abierto en el chat
        private AgentSession? _selected;   // pestaña / sesión abierta en el chat
        private AgentSession? _bannerSession;
        private string? _bannerKey;
        private RemindersWindow? _reminders;

        private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
        private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
        private readonly DispatcherTimer _bannerTimer = new() { Interval = TimeSpan.FromSeconds(5) };

        private static App AppRef => App.Current;
        private AgentEntry? Principal => AppRef.Agents.FirstOrDefault(a => a.IsDefault) ?? AppRef.Agents.FirstOrDefault();

        public NotchWindow()
        {
            InitializeComponent();

            foreach (var a in AppRef.Agents) a.PropertyChanged += Entry_PropertyChanged;
            AppRef.Agents.CollectionChanged += Agents_CollectionChanged;
            foreach (var s in AppRef.Sessions) s.PropertyChanged += Session_PropertyChanged;
            AppRef.Sessions.CollectionChanged += Sessions_CollectionChanged;

            SetFocused(Principal);
            RebuildPill();

            _hoverTimer.Tick += (_, _) =>
            {
                _hoverTimer.Stop();
                if (_mode is Mode.Collapsed or Mode.Banner && AppRef.Store.Config.Settings.ExpandOnHover && Shell.IsMouseOver)
                    ShowOverview();
            };
            _collapseTimer.Tick += (_, _) =>
            {
                _collapseTimer.Stop();
                if (_pinned || _modalOpen || Shell.IsMouseOver || _selected?.PendingApproval != null) return;
                // La vista general es solo informativa: se cierra al salir. El chat, solo si no tiene el foco.
                if (_mode == Mode.Overview || (_mode == Mode.Chat && !IsActive)) Collapse();
            };
            _bannerTimer.Tick += (_, _) =>
            {
                _bannerTimer.Stop();
                if (_mode == Mode.Banner && !Shell.IsMouseOver) Collapse();
            };

            ChatScroll.ScrollChanged += ChatScroll_ScrollChanged;
            Deactivated += (_, _) =>
            {
                if (_mode is Mode.Overview or Mode.Chat && !_pinned && !_modalOpen && !Shell.IsMouseOver
                    && _selected?.PendingApproval == null) Collapse();
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
                case Mode.Chat: return new Size(800, 560);
                case Mode.Banner: return new Size(450, 74);
                case Mode.Overview:
                    OverviewView.Measure(new Size(820, double.PositiveInfinity));
                    return new Size(840, Math.Clamp(OverviewView.DesiredSize.Height + 4, 140, 380));
                default:
                    CollapsedContent.Measure(new Size(double.PositiveInfinity, 44));
                    return new Size(Math.Max(110, CollapsedContent.DesiredSize.Width + 34), 44);
            }
        }

        private void SetMode(Mode m, bool animate = true)
        {
            var prev = _mode;
            _mode = m;
            if (prev != m || !animate)
            {
                ShowView(CollapsedView, m == Mode.Collapsed);
                ShowView(BannerView, m == Mode.Banner);
                ShowView(OverviewView, m == Mode.Overview);
                ShowView(ChatView, m == Mode.Chat);
            }
            var size = TargetSize(m);
            if (animate)
            {
                bool growing = size.Height > Shell.ActualHeight + 2;
                IEasingFunction ease = growing
                    ? new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut }
                    : new CubicEase { EasingMode = EasingMode.EaseOut };
                var dur = TimeSpan.FromMilliseconds(growing ? 330 : 230);
                Shell.BeginAnimation(WidthProperty, new DoubleAnimation(size.Width, dur) { EasingFunction = ease });
                Shell.BeginAnimation(HeightProperty, new DoubleAnimation(size.Height, dur) { EasingFunction = ease });
            }
            else
            {
                Shell.BeginAnimation(WidthProperty, null);
                Shell.BeginAnimation(HeightProperty, null);
                Shell.Width = size.Width; Shell.Height = size.Height;
            }
            Shell.CornerRadius = m is Mode.Chat or Mode.Overview ? new CornerRadius(0, 0, 28, 28) : new CornerRadius(0, 0, 22, 22);
            if (m == Mode.Collapsed) foreach (var a in AppRef.Agents) a.IsBox = false;
        }

        private void Resize()
        {
            if (_mode == Mode.Collapsed) { UpdateCollapsedSize(); return; }
            var size = TargetSize(_mode);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Shell.BeginAnimation(WidthProperty, new DoubleAnimation(size.Width, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            Shell.BeginAnimation(HeightProperty, new DoubleAnimation(size.Height, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        }

        private static void ShowView(FrameworkElement el, bool show)
        {
            if (show)
            {
                el.Visibility = Visibility.Visible;
                el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { BeginTime = TimeSpan.FromMilliseconds(80) });
            }
            else
            {
                el.BeginAnimation(OpacityProperty, null);
                el.Opacity = 0;
                el.Visibility = Visibility.Collapsed;
            }
        }

        private void ShowOverview()
        {
            RefreshOverview();
            if (_mode != Mode.Overview) SetMode(Mode.Overview);
            else Resize();
        }

        /// <summary>
        /// Desde la app (bandeja, atajo, notificaciones). Con una sesión concreta abre su chat;
        /// sin ella muestra la vista general.
        /// </summary>
        public void Expand(bool focusInput, string? sessionKey = null)
        {
            _bannerTimer.Stop();
            var s = sessionKey == null ? null
                : AppRef.Sessions.FirstOrDefault(x => x.Key == sessionKey)
                  ?? AppRef.Agents.FirstOrDefault(a => a.Profile.Id == sessionKey)?.ActiveTab;
            var entry = s != null ? AppRef.EntryOf(s) : null;
            if (entry != null && s != null)
            {
                if (entry.IsScheduled) { SetFocused(entry); ShowOverview(); }
                else Open(entry, s, focusInput);
                if (focusInput) Activate();
                return;
            }
            ShowOverview();
            if (focusInput) Activate();
        }

        /// <summary>Abre el chat de un agente (en la pestaña indicada o la activa).</summary>
        private void Open(AgentEntry entry, AgentSession? tab = null, bool focus = true)
        {
            if (entry.IsScheduled) { SetFocused(entry); ShowOverview(); return; }
            _bannerTimer.Stop();
            _openEntry = entry;
            TabStrip.ItemsSource = entry.IsCode ? entry.Tabs : null;
            TabsScroller.Visibility = entry.IsCode ? Visibility.Visible : Visibility.Collapsed;
            NewChatButton.Visibility = entry.IsCode ? Visibility.Collapsed : Visibility.Visible;
            SelectTab(tab ?? entry.ActiveTab);
            if (_mode != Mode.Chat) SetMode(Mode.Chat);
            if (focus) FocusInput();
        }

        private void SelectTab(AgentSession? s)
        {
            if (_openEntry != null && s != null) _openEntry.ActiveTab = s;
            Select(s);
            NoWorkspace.Visibility = _openEntry?.IsCode == true && _openEntry.Tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ChatMochi.DataContext = _openEntry;
            ChatMochi.SetBinding(MochiView.BodyColorProperty, new Binding(nameof(AgentEntry.Color)));
            ChatMochi.SetBinding(MochiView.StateProperty, new Binding(nameof(AgentEntry.State)));
            ChatMochi.SetBinding(MochiView.IsBoxProperty, new Binding(nameof(AgentEntry.IsBox)));
        }

        private void FocusInput()
        {
            Activate();
            if (_mode == Mode.Chat && _selected?.CanType == true)
                Dispatcher.BeginInvoke(() => { Input.Focus(); Keyboard.Focus(Input); }, DispatcherPriority.Input);
        }

        public void Collapse()
        {
            if (_mode == Mode.Collapsed) { UpdateCollapsedSize(); return; }
            SetMode(Mode.Collapsed);
            SetFocused(Principal); // al volver a abrir, el principal está a la izquierda
        }

        public bool IsLookingAt(AgentSession s) => _mode == Mode.Chat && _selected == s;

        public void ShowBanner(AgentSession s, string title, string body, bool sticky = false)
        {
            if (_mode is not (Mode.Collapsed or Mode.Banner)) return; // el usuario ya está mirando el panel
            ShowBannerRaw(s.Color, s.State == MochiState.Idle ? MochiState.Done : s.State, title, body, s.Key, sticky);
            _bannerSession = s;
        }

        public void ShowBannerRaw(Color color, MochiState state, string title, string body, string? sessionKey, bool sticky)
        {
            if (_mode is not (Mode.Collapsed or Mode.Banner)) return;
            _bannerSession = null;
            _bannerKey = sessionKey;
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
                busy.PendingApproval != null ? $"{busy.DisplayName} necesita permiso" : $"{busy.DisplayName}: {busy.Activity}";
            CollapsedStatus.Visibility = busy == null ? Visibility.Collapsed : Visibility.Visible;
            if (_mode != Mode.Collapsed) return;
            var size = TargetSize(Mode.Collapsed);
            if (Math.Abs(Shell.ActualWidth - size.Width) > 3)
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                Shell.BeginAnimation(WidthProperty, new DoubleAnimation(size.Width, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            }
        }

        // =============================================================== vista general
        private void SetFocused(AgentEntry? e)
        {
            if (_focused != null) _focused.PropertyChanged -= Focused_PropertyChanged;
            _focused = e;
            if (_focused != null) _focused.PropertyChanged += Focused_PropertyChanged;
            RefreshOverview();
        }

        private void Focused_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(AgentEntry.StatusChip) or nameof(AgentEntry.IsScheduled) or nameof(AgentEntry.Profile))
                UpdateFocusChrome();
        }

        /// <summary>El agente enfocado va a la tarjeta grande; el resto, a los chips de la derecha.</summary>
        private void RefreshOverview()
        {
            var agents = AppRef.Agents.ToList();
            if (_focused != null && !agents.Contains(_focused)) { SetFocused(Principal); return; }
            FocusCard.DataContext = _focused;
            FocusCard.Visibility = _focused == null ? Visibility.Hidden : Visibility.Visible;
            ChipList.ItemsSource = agents.Where(a => a != _focused).ToList();
            UpdateFocusChrome();
            if (_mode == Mode.Overview) Dispatcher.BeginInvoke(Resize, DispatcherPriority.Loaded);
        }

        private void UpdateFocusChrome()
        {
            var f = _focused;
            if (f == null) return;
            BackToPrincipal.Visibility = f != Principal ? Visibility.Visible : Visibility.Collapsed;
            OpenChatButton.Visibility = f.IsConversational ? Visibility.Visible : Visibility.Collapsed;
            ScheduledRun.Visibility = ScheduledPause.Visibility = f.IsScheduled ? Visibility.Visible : Visibility.Collapsed;
            if (f.IsScheduled)
            {
                ScheduledPause.Content = f.Profile.Schedule.Paused ? "" : "";
                ScheduledPause.ToolTip = f.Profile.Schedule.Paused ? "Reanudar programación" : "Pausar programación";
                ScheduledRun.Content = ""; // ejecutar ahora (sincronizar)
            }

            // Chip de estado y halo de la tarjeta (rojo si falló, como en coucou)
            var (chipBg, chipFg) = f.StatusChip switch
            {
                "Error" => ("#3A1A1E", "#FF8A8A"),
                "Permiso" => ("#3A2C10", "#FFB020"),
                "Trabajando" => ("#1A2640", "#8EB8FF"),
                _ => ("#1FFFFFFF", "#D6D6DE")
            };
            StatusChipBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(chipBg));
            StatusChipText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(chipFg));
            if (f.ChipIsError)
            {
                var g = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
                g.GradientStops.Add(new GradientStop(Color.FromRgb(0x2E, 0x14, 0x18), 0));
                g.GradientStops.Add(new GradientStop(Color.FromRgb(0x12, 0x12, 0x17), 0.75));
                FocusCard.Background = g;
            }
            else FocusCard.Background = (Brush)FindResource("PaneBg");
        }

        private void Chip_Click(object sender, MouseButtonEventArgs e)
        {
            // El agente del chip pasa a la tarjeta grande; el que estaba ahí baja a los chips
            if ((sender as FrameworkElement)?.DataContext is AgentEntry a) { SetFocused(a); e.Handled = true; }
        }

        private void Chip_RightClick(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is AgentEntry a) ShowMenu(BuildAgentMenu(a), (UIElement)sender, e);
        }

        private void FocusCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) != null) return;
            // Pulsar el agente que ya está a la izquierda lo expande al chat
            if (_focused?.IsConversational == true) Open(_focused);
        }

        private void FocusCard_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (_focused != null) ShowMenu(BuildAgentMenu(_focused), FocusCard, e);
        }

        private void OpenFocused_Click(object sender, RoutedEventArgs e) { if (_focused != null) Open(_focused); }
        private void BackToPrincipal_Click(object sender, RoutedEventArgs e) => SetFocused(Principal);

        private void AddChip_Click(object sender, MouseButtonEventArgs e)
        {
            var m = new ContextMenu { PlacementTarget = AddChip, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            void Add(string header, Action a) { var it = new MenuItem { Header = header }; it.Click += (_, _) => a(); m.Items.Add(it); }
            Add("Nuevo workspace de código (elegir carpeta)…", () => NewWorkspace(null));
            Add("Agente de chat…", () => OpenEditor(null, AgentKind.Interactive));
            Add("Agente programado…", () => OpenEditor(null, AgentKind.Scheduled));
            Add("Agente de código (otro CLI)…", () => OpenEditor(null, AgentKind.Code));
            m.Items.Add(new Separator());
            Add("Recordatorios…", OpenReminders);
            m.IsOpen = true;
        }

        private static void ShowMenu(ContextMenu menu, UIElement target, MouseButtonEventArgs e)
        {
            menu.PlacementTarget = target;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private ContextMenu BuildAgentMenu(AgentEntry a)
        {
            var m = new ContextMenu();
            void Add(string header, Action act, bool enabled = true)
            {
                var it = new MenuItem { Header = header, IsEnabled = enabled };
                it.Click += (_, _) => act();
                m.Items.Add(it);
            }
            if (a.IsConversational) Add("Abrir chat", () => Open(a));
            Add("Editar agente…", () => OpenEditor(a));
            Add(a.IsDefault ? "★ Es el principal" : "Establecer como principal", () => { AppRef.SetDefault(a); RebuildPill(); RefreshOverview(); }, !a.IsDefault);
            if (a.IsCode) Add("Nuevo workspace…", () => NewWorkspace(a));
            var single = a.IsCode ? null : a.Tabs.FirstOrDefault();
            if (single != null && a.IsConversational) Add("Nueva conversación", single.NewConversation);
            if (single?.CanResumeQueue == true) Add($"Reanudar cola ({single.PendingCount})", single.ResumeQueue);
            if (single?.PendingCount > 0) Add("Vaciar cola", single.ClearQueue);
            if (a.IsScheduled && single != null) Add("Ejecutar ahora", () => _ = single.RunScheduledAsync());
            if (!a.IsCode) Add("Duplicar", () => Duplicate(a));
            m.Items.Add(new Separator());
            Add("Eliminar", () => ConfirmRemove(a));
            return m;
        }

        // =============================================================== píldora
        private void QueueRebuild()
        {
            if (_rebuildQueued) return;
            _rebuildQueued = true;
            Dispatcher.BeginInvoke(() => { _rebuildQueued = false; RebuildPill(); RefreshOverview(); }, DispatcherPriority.Background);
        }

        private void RebuildPill()
        {
            CollapsedAgents.Children.Clear();
            var principal = Principal;
            if (principal != null)
            {
                var big = Mini(principal, 36);
                big.Margin = new Thickness(0, 0, 4, 0);
                CollapsedAgents.Children.Add(big);
            }
            var others = AppRef.Agents.Where(a => a != principal).ToList();
            foreach (var group in new[] { others.Where(a => a.IsCode).ToList(), others.Where(a => a.Profile.Kind == AgentKind.Interactive).ToList(), others.Where(a => a.IsScheduled).ToList() })
            {
                if (group.Count == 0) continue;
                CollapsedAgents.Children.Add(new Border { Width = 1, Height = 16, Margin = new Thickness(6, 0, 6, 0), Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) });
                int i = 0;
                foreach (var a in group)
                {
                    var mini = Mini(a, 22);
                    mini.Margin = new Thickness(i == 0 ? 0 : -8, 0, 0, 0);
                    Panel.SetZIndex(mini, group.Count - i);
                    CollapsedAgents.Children.Add(mini);
                    i++;
                }
            }
            UpdateCollapsedSize();
        }

        private static MochiView Mini(AgentEntry a, double size)
        {
            var m = new MochiView { Width = size, Height = size, Interactive = false, ToolTip = a.Name, DataContext = a };
            m.SetBinding(MochiView.BodyColorProperty, new Binding(nameof(AgentEntry.Color)) { Source = a });
            m.SetBinding(MochiView.StateProperty, new Binding(nameof(AgentEntry.State)) { Source = a });
            m.SetBinding(MochiView.IsBoxProperty, new Binding(nameof(AgentEntry.IsBox)) { Source = a });
            return m;
        }

        // =============================================================== selección / sesiones
        private void Select(AgentSession? s)
        {
            if (_selected != null) _selected.Messages.CollectionChanged -= Messages_CollectionChanged;
            _selected = s;
            ChatView.DataContext = s;
            if (s != null)
            {
                s.Messages.CollectionChanged += Messages_CollectionChanged;
                AppRef.Store.Config.Settings.SelectedAgentId = s.Key;
            }
            UpdateEmptyChat();
            _autoScroll = true;
            ScrollToEnd();
        }

        private void Agents_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null) foreach (AgentEntry a in e.NewItems) a.PropertyChanged += Entry_PropertyChanged;
            if (e.OldItems != null) foreach (AgentEntry a in e.OldItems) a.PropertyChanged -= Entry_PropertyChanged;
            if (_openEntry != null && !AppRef.Agents.Contains(_openEntry))
            {
                _openEntry = null;
                Select(null);
                if (_mode == Mode.Chat) ShowOverview();
            }
            QueueRebuild();
        }

        private void Entry_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(AgentEntry.IsDefault) or nameof(AgentEntry.Profile)) QueueRebuild();
        }

        private void Sessions_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null) foreach (AgentSession s in e.NewItems) s.PropertyChanged += Session_PropertyChanged;
            if (e.OldItems != null) foreach (AgentSession s in e.OldItems) s.PropertyChanged -= Session_PropertyChanged;
            // Si se cerró la pestaña abierta, pasamos a otra del mismo agente
            if (_selected != null && !AppRef.Sessions.Contains(_selected) && _openEntry != null) SelectTab(_openEntry.Tabs.FirstOrDefault());
            else if (_openEntry != null) NoWorkspace.Visibility = _openEntry.IsCode && _openEntry.Tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Session_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(AgentSession.Activity):
                case nameof(AgentSession.IsBusy):
                case nameof(AgentSession.PendingApproval):
                    UpdateCollapsedSize();
                    break;
            }
            if (sender == _bannerSession && e.PropertyName == nameof(AgentSession.State) && _mode == Mode.Banner)
                BannerMochi.State = _bannerSession!.State;
        }

        private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyChat();

        private void UpdateEmptyChat()
        {
            var s = _selected;
            bool show = s != null && s.Messages.Count == 0;
            EmptyChat.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (s == null) return;
            EmptyChat.Text = s.IsCode
                ? $"¿Qué hacemos en {s.FolderName}?\nSi le escribes mientras trabaja, se encola."
                : $"Habla con {s.Profile.Name}\n/recordar 10m algo · arrastra archivos sobre el notch";
        }

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
            if (_mode is Mode.Collapsed or Mode.Banner) _hoverTimer.Start();
        }

        private void Shell_MouseLeave(object sender, MouseEventArgs e)
        {
            _hoverTimer.Stop();
            if (_mode == Mode.Overview || (_mode == Mode.Chat && !IsActive)) _collapseTimer.Start();
            if (_mode == Mode.Banner && !_bannerTimer.IsEnabled) _bannerTimer.Start();
        }

        private void Collapsed_Click(object sender, MouseButtonEventArgs e)
        {
            _hoverTimer.Stop();
            ShowOverview();
            Activate();
        }

        private void Banner_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) != null) return;
            Expand(focusInput: true, sessionKey: _bannerKey);
        }

        private void BannerClose_Click(object sender, RoutedEventArgs e) => Collapse();
        private void BackToOverview_Click(object sender, RoutedEventArgs e) { if (_openEntry != null) SetFocused(_openEntry); ShowOverview(); }

        private void Tabs_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            var sv = (ScrollViewer)sender;
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta / 3.0);
            e.Handled = true;
        }

        // =============================================================== pestañas de workspace
        private void Tab_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) != null) return;
            if ((sender as FrameworkElement)?.DataContext is AgentSession s) { SelectTab(s); FocusInput(); }
        }

        private void Tab_RightClick(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not AgentSession s) return;
            var m = new ContextMenu();
            void Add(string header, Action act) { var it = new MenuItem { Header = header }; it.Click += (_, _) => act(); m.Items.Add(it); }
            Add("Nueva conversación en este workspace", s.NewConversation);
            Add("Abrir carpeta", () => OpenInExplorer(s.FolderPath));
            if (s.CanResumeQueue) Add($"Reanudar cola ({s.PendingCount})", s.ResumeQueue);
            if (s.PendingCount > 0) Add("Vaciar cola", s.ClearQueue);
            m.Items.Add(new Separator());
            Add("Cerrar workspace", () => ConfirmCloseTab(s));
            ShowMenu(m, (UIElement)sender, e);
        }

        private void CloseTab_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is AgentSession s) ConfirmCloseTab(s);
        }

        private void ConfirmCloseTab(AgentSession s)
        {
            if (s.IsBusy)
            {
                _modalOpen = true;
                var ok = MessageBox.Show(this, $"{s.FolderName} está trabajando. ¿Detenerlo y cerrar el workspace?", "Coucou",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                _modalOpen = false;
                Activate();
                if (!ok) return;
            }
            AppRef.CloseWorkspace(s);
        }

        private void NewWorkspace_Click(object sender, RoutedEventArgs e) => NewWorkspace(_openEntry?.IsCode == true ? _openEntry : null);

        private void NewWorkspace(AgentEntry? target)
        {
            var folder = PickFolder("Carpeta del nuevo workspace");
            if (folder == null) return;
            OpenWorkspace(AppRef.AddWorkspace(folder, target));
        }

        private void OpenWorkspace(AgentSession s)
        {
            var entry = AppRef.EntryOf(s);
            if (entry != null) Open(entry, s);
        }

        // =============================================================== chat
        private async System.Threading.Tasks.Task SendCurrent()
        {
            if (_selected == null || !_selected.CanType) return;
            var text = Input.Text;
            if (string.IsNullOrWhiteSpace(text)) return;
            Input.Clear();
            _autoScroll = true;
            await _selected.SendAsync(text); // si está ocupado, queda en la cola
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
        private void NewChat_Click(object sender, RoutedEventArgs e) => _selected?.NewConversation();

        private void CopyMessage_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ChatMessage m)
                try { Clipboard.SetText(m.Text); } catch { }
        }

        private void Attach_Click(object sender, RoutedEventArgs e)
        {
            _modalOpen = true;
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Adjuntar archivos para el agente" };
                if (_selected?.HasFolder == true) dlg.InitialDirectory = _selected.FolderPath;
                if (dlg.ShowDialog(this) == true) AppendFiles(dlg.FileNames);
            }
            finally { _modalOpen = false; }
        }

        private void AppendFiles(IReadOnlyCollection<string> files)
        {
            if (files.Count == 0 || _selected?.CanType != true || _mode != Mode.Chat) return;
            var block = "Archivos:\n" + string.Join("\n", files.Select(f => "- " + f));
            Input.Text = string.IsNullOrWhiteSpace(Input.Text) ? block + "\n\n" : Input.Text.TrimEnd() + "\n\n" + block + "\n\n";
            Input.CaretIndex = Input.Text.Length;
            Activate();
            Input.Focus();
        }

        // =============================================================== agentes programados
        private void RunNow_Click(object sender, RoutedEventArgs e)
        {
            var s = _focused?.IsScheduled == true ? _focused.Tabs.FirstOrDefault() : null;
            if (s == null || s.IsBusy) return;
            _ = s.RunScheduledAsync();
            AppRef.SaveAgents();
        }

        private void TogglePause_Click(object sender, RoutedEventArgs e)
        {
            var s = _focused?.IsScheduled == true ? _focused.Tabs.FirstOrDefault() : null;
            if (s == null) return;
            var sc = s.Profile.Schedule;
            sc.Paused = !sc.Paused;
            if (!sc.Paused) ScheduleService.Reschedule(s);
            s.RaiseProfileChanged();
            _focused!.RaiseAll();
            AppRef.SaveAgents();
            UpdateFocusChrome();
        }

        // =============================================================== aprobaciones
        private void Allow_Click(object sender, RoutedEventArgs e) => _selected?.Decide(ApprovalDecision.Allow);
        private void AllowAlways_Click(object sender, RoutedEventArgs e) => _selected?.Decide(ApprovalDecision.AllowAlways);
        private void Deny_Click(object sender, RoutedEventArgs e) => _selected?.Decide(ApprovalDecision.Deny);

        // =============================================================== arrastrar carpetas y archivos
        private static string[] DroppedPaths(DragEventArgs e) =>
            e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();

        private void Shell_DragEnter(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.None; return; }
            e.Effects = DragDropEffects.Copy;
            if (_mode is Mode.Collapsed or Mode.Banner) ShowOverview();
            var target = _mode == Mode.Chat ? _openEntry : _focused;
            if (target != null) target.IsBox = true;
        }

        private void Shell_DragLeave(object sender, DragEventArgs e)
        {
            if (!Shell.IsMouseOver) foreach (var a in AppRef.Agents) a.IsBox = false;
        }

        /// <summary>Carpetas → nuevos workspaces (pestañas) de código. Archivos → se adjuntan al chat abierto.</summary>
        private void Shell_Drop(object sender, DragEventArgs e)
        {
            foreach (var a in AppRef.Agents) a.IsBox = false;
            var paths = DroppedPaths(e);
            var dirs = paths.Where(Directory.Exists).ToList();
            var files = paths.Where(File.Exists).ToList();

            if (dirs.Count > 0)
            {
                var target = _mode == Mode.Chat && _openEntry?.IsCode == true ? _openEntry
                           : _focused?.IsCode == true ? _focused : null;
                AgentSession? last = null;
                foreach (var d in dirs) last = AppRef.AddWorkspace(d, target);
                if (last != null) OpenWorkspace(last);
            }
            if (files.Count > 0) AppendFiles(files);
        }

        // =============================================================== crear / editar / eliminar
        private string? PickFolder(string title)
        {
            _modalOpen = true;
            try
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title };
                return dlg.ShowDialog(this) == true ? dlg.FolderName : null;
            }
            finally { _modalOpen = false; Activate(); }
        }

        private void OpenFolder_Click(object sender, MouseButtonEventArgs e)
        {
            if (_selected?.HasFolder == true) OpenInExplorer(_selected.FolderPath);
        }

        private static void OpenInExplorer(string path)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("Abrir carpeta", ex); }
        }

        private void EditSelected_Click(object sender, RoutedEventArgs e) { if (_openEntry != null) OpenEditor(_openEntry); }

        private void ConfirmRemove(AgentEntry a)
        {
            _modalOpen = true;
            var what = a.IsCode && a.Tabs.Count > 0
                ? $"¿Eliminar {a.Name} y sus {a.Tabs.Count} workspace(s)?\nSe borran las conversaciones; las carpetas no se tocan."
                : $"¿Eliminar a {a.Name} y su conversación?";
            var ok = MessageBox.Show(this, what, "Coucou", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            _modalOpen = false;
            Activate();
            if (!ok) return;
            AppRef.RemoveAgent(a);
            if (_focused == a) SetFocused(Principal);
        }

        private void Duplicate(AgentEntry a)
        {
            var p = a.Profile.Clone();
            p.Id = Guid.NewGuid().ToString("N");
            p.Name += " 2";
            p.Schedule.NextRun = null;
            p.Schedule.LastRun = null;
            p.Workspaces.Clear();
            SetFocused(AppRef.AddAgent(p));
        }

        public void OpenEditor(AgentEntry? entry, AgentKind kindForNew = AgentKind.Interactive)
        {
            _modalOpen = true;
            try
            {
                var win = new AgentEditorWindow(entry?.Profile.Clone(), kindForNew, kindLocked: entry?.IsCode == true && entry.Tabs.Count > 0,
                                                entry?.IsDefault ?? false) { Owner = this };
                if (win.ShowDialog() != true || win.Result == null) return;
                var p = win.Result;
                if (entry == null)
                {
                    var ne = AppRef.AddAgent(p);
                    if (win.MakeDefault) AppRef.SetDefault(ne);
                    ne.Tabs.FirstOrDefault()?.AddNotice($"¡Hola! Soy {p.Name}. Funciono con {ne.ProviderLabel}.");
                    SetFocused(ne);
                    ShowOverview();
                }
                else
                {
                    var updated = AppRef.UpdateAgent(entry, p);
                    if (win.MakeDefault && !updated.IsDefault) AppRef.SetDefault(updated);
                    if (_openEntry == entry && updated != entry) { _openEntry = null; ShowOverview(); }
                    SetFocused(updated);
                }
                QueueRebuild();
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
                if (_mode is Mode.Overview or Mode.Chat && IsActive) { _pinned = false; UpdatePin(); Collapse(); }
                else Expand(focusInput: true);
            }
            return IntPtr.Zero;
        }

        private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T t) return t;
                d = d is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }
    }
}
