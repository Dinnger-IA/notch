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
    ///   Overview  → al pasar el ratón / primer clic: solo información de los agentes
    ///   Chat      → conversación de un agente interactivo (lista de tareas a la izquierda)
    ///   Info      → barra informativa de un agente no interactivo (programado)
    /// </summary>
    public partial class NotchWindow : Window
    {
        private enum Mode { Collapsed, Banner, Overview, Chat, Info }

        private Mode _mode = Mode.Collapsed;
        private bool _pinned, _modalOpen, _autoScroll = true, _rebuildQueued, _codeExpanded;
        private AgentSession? _selected;
        private AgentSession? _bannerSession;
        private string? _bannerAgentId;
        private RemindersWindow? _reminders;
        private string _layoutSignature = "";
        private List<AgentSession> _codeSiblings = new();

        private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
        private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
        private readonly DispatcherTimer _bannerTimer = new() { Interval = TimeSpan.FromSeconds(5) };

        private static App AppRef => App.Current;
        private bool InAgentView => _mode is Mode.Chat or Mode.Info;

        public NotchWindow()
        {
            InitializeComponent();

            foreach (var s in AppRef.Sessions) s.PropertyChanged += Session_PropertyChanged;
            AppRef.Sessions.CollectionChanged += Sessions_CollectionChanged;

            var initial = AppRef.Sessions.FirstOrDefault(s => s.Profile.Id == AppRef.Store.Config.Settings.SelectedAgentId)
                          ?? AppRef.Sessions.FirstOrDefault(s => s.IsDefault)
                          ?? AppRef.Sessions.FirstOrDefault();
            Select(initial);
            RebuildAgents(force: true);

            _hoverTimer.Tick += (_, _) =>
            {
                _hoverTimer.Stop();
                if (_mode is Mode.Collapsed or Mode.Banner && AppRef.Store.Config.Settings.ExpandOnHover && Shell.IsMouseOver)
                    SetMode(Mode.Overview);
            };
            _collapseTimer.Tick += (_, _) =>
            {
                _collapseTimer.Stop();
                if (_pinned || _modalOpen || Shell.IsMouseOver || _selected?.PendingApproval != null) return;
                // La vista general es solo informativa: se cierra al salir. El chat, solo si no tiene el foco.
                if (_mode == Mode.Overview || (InAgentView && !IsActive)) Collapse();
            };
            _bannerTimer.Tick += (_, _) =>
            {
                _bannerTimer.Stop();
                if (_mode == Mode.Banner && !Shell.IsMouseOver) Collapse();
            };

            ChatScroll.ScrollChanged += ChatScroll_ScrollChanged;
            Deactivated += (_, _) =>
            {
                if (_mode != Mode.Collapsed && _mode != Mode.Banner && !_pinned && !_modalOpen && !Shell.IsMouseOver
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
                case Mode.Chat: return new Size(780, 560);
                case Mode.Info: return new Size(640, 118);
                case Mode.Banner: return new Size(450, 74);
                case Mode.Overview:
                    OverviewContent.Measure(new Size(476, double.PositiveInfinity));
                    return new Size(500, Math.Clamp(OverviewContent.DesiredSize.Height + 20, 120, 620));
                default:
                    // Se mide el contenido directamente: CollapsedView puede estar oculto en este momento
                    CollapsedContent.Measure(new Size(double.PositiveInfinity, 44));
                    return new Size(Math.Max(110, CollapsedContent.DesiredSize.Width + 34), 44);
            }
        }

        private void SetMode(Mode m, bool animate = true)
        {
            var prev = _mode;
            _mode = m;
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
            Shell.CornerRadius = m is Mode.Chat or Mode.Overview ? new CornerRadius(0, 0, 26, 26) : new CornerRadius(0, 0, 22, 22);

            if (prev != m || !animate)
            {
                ShowView(CollapsedView, m == Mode.Collapsed);
                ShowView(BannerView, m == Mode.Banner);
                ShowView(OverviewView, m == Mode.Overview);
                ShowView(ChatView, m == Mode.Chat);
                ShowView(InfoView, m == Mode.Info);
            }
            if (m == Mode.Collapsed) foreach (var s in AppRef.Sessions) s.IsBox = false;
        }

        /// <summary>Recalcula el tamaño del modo actual (p. ej. al desglosar los agentes de código).</summary>
        private void Resize()
        {
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

        /// <summary>
        /// Usado por la app (bandeja, atajo, notificaciones). Con un agente concreto abre su vista;
        /// sin agente muestra la vista general.
        /// </summary>
        public void Expand(bool focusInput, string? agentId = null)
        {
            _bannerTimer.Stop();
            var s = agentId == null ? null : AppRef.Sessions.FirstOrDefault(x => x.Profile.Id == agentId);
            if (s != null) { Open(s, focusInput); return; }
            if (_mode != Mode.Overview) SetMode(Mode.Overview);
            if (focusInput) Activate();
        }

        /// <summary>Abre el chat (interactivos) o la barra informativa (programados) de un agente.</summary>
        private void Open(AgentSession s, bool focus = true)
        {
            _bannerTimer.Stop();
            Select(s);
            var target = s.IsScheduled ? Mode.Info : Mode.Chat;
            if (_mode != target) SetMode(target);
            if (focus) FocusInput();
            ScrollToEnd();
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
            _codeExpanded = false;
            ApplyCodeExpanded();
            SetMode(Mode.Collapsed);
        }

        public bool IsLookingAt(AgentSession s) => InAgentView && _selected == s;

        public void ShowBanner(AgentSession s, string title, string body, bool sticky = false)
        {
            if (_mode != Mode.Collapsed && _mode != Mode.Banner) return; // el usuario ya está mirando el panel
            ShowBannerRaw(s.Color, s.State == MochiState.Idle ? MochiState.Done : s.State, title, body, s.Profile.Id, sticky);
            _bannerSession = s;
        }

        /// <summary>Aviso en el notch con un personaje de cualquier color (también para eventos externos).</summary>
        public void ShowBannerRaw(Color color, MochiState state, string title, string body, string? agentId, bool sticky)
        {
            if (_mode != Mode.Collapsed && _mode != Mode.Banner) return; // panel abierto: basta con el toast
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

        // =============================================================== agentes: principal + código agrupados
        private void QueueRebuild()
        {
            if (_rebuildQueued) return;
            _rebuildQueued = true;
            Dispatcher.BeginInvoke(() => { _rebuildQueued = false; RebuildAgents(); }, DispatcherPriority.Background);
        }

        /// <summary>
        /// El principal y todos los agentes de código forman una sola pila. El resto (chat y programados)
        /// va debajo, cada uno en su fila.
        /// </summary>
        private void RebuildAgents(bool force = false)
        {
            var sessions = AppRef.Sessions.ToList();
            var sig = string.Join("|", sessions.Select(s => $"{s.Profile.Id}:{s.Profile.Kind}:{s.IsDefault}"));
            if (!force && sig == _layoutSignature) { UpdateCollapsedSize(); return; }
            _layoutSignature = sig;

            var primary = sessions.FirstOrDefault(s => s.IsDefault);
            _codeSiblings = sessions.Where(s => s != primary && s.IsCode).ToList();
            var chat = sessions.Where(s => s != primary && s.Profile.Kind == AgentKind.Interactive).ToList();
            var sched = sessions.Where(s => s != primary && s.IsScheduled).ToList();

            // --- vista general
            PrincipalCard.Visibility = primary == null ? Visibility.Collapsed : Visibility.Visible;
            PrincipalCard.DataContext = primary;
            PeekList.ItemsSource = _codeSiblings;
            PeekHost.Visibility = _codeSiblings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            CodeList.ItemsSource = primary == null ? _codeSiblings : new[] { primary }.Concat(_codeSiblings).ToList();
            OthersList.ItemsSource = chat.Concat(sched).ToList();
            NoAgents.Visibility = sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_codeSiblings.Count == 0) _codeExpanded = false;
            ApplyCodeExpanded();

            // --- píldora: principal grande con los de código asomando detrás; luego los demás
            CollapsedAgents.Children.Clear();
            if (primary != null)
            {
                var stack = new Grid { Margin = new Thickness(0, 0, 4, 0) };
                var peeks = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(26, 0, 0, 0) };
                int i = 0;
                foreach (var s in _codeSiblings.Take(4))
                {
                    var mini = Mini(s, 24);
                    mini.Margin = new Thickness(i == 0 ? 0 : -14, 0, 0, 0);
                    Panel.SetZIndex(mini, 10 - i);
                    peeks.Children.Add(mini);
                    i++;
                }
                stack.Children.Add(peeks);
                var big = Mini(primary, 36);
                big.HorizontalAlignment = HorizontalAlignment.Left;
                Panel.SetZIndex(big, 20);
                stack.Children.Add(big);
                CollapsedAgents.Children.Add(stack);
            }
            foreach (var group in new[] { chat, sched })
            {
                if (group.Count == 0) continue;
                CollapsedAgents.Children.Add(new Border { Width = 1, Height = 16, Margin = new Thickness(6, 0, 6, 0), Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) });
                int i = 0;
                foreach (var s in group)
                {
                    var mini = Mini(s, 22);
                    mini.Margin = new Thickness(i == 0 ? 0 : -8, 0, 0, 0);
                    Panel.SetZIndex(mini, group.Count - i);
                    CollapsedAgents.Children.Add(mini);
                    i++;
                }
            }

            if (_selected == null || !sessions.Contains(_selected)) Select(primary ?? sessions.FirstOrDefault());
            if (_mode == Mode.Overview) Resize();
            else if (InAgentView && _selected == null) SetMode(Mode.Overview);
            UpdateCollapsedSize();
        }

        private static MochiView Mini(AgentSession s, double size)
        {
            var m = new MochiView { Width = size, Height = size, Interactive = false, ToolTip = $"{s.Profile.Name} · {s.SubLabel}", DataContext = s };
            m.SetBinding(MochiView.BodyColorProperty, new Binding(nameof(AgentSession.Color)) { Source = s });
            m.SetBinding(MochiView.StateProperty, new Binding(nameof(AgentSession.State)) { Source = s });
            m.SetBinding(MochiView.IsBoxProperty, new Binding(nameof(AgentSession.IsBox)) { Source = s });
            return m;
        }

        private void ApplyCodeExpanded()
        {
            CodeList.Visibility = _codeExpanded ? Visibility.Visible : Visibility.Collapsed;
            Chevron.Text = _codeExpanded ? "" : "";
            PrincipalCard.ToolTip = _codeSiblings.Count > 0
                ? (_codeExpanded ? "Ocultar agentes de código" : $"Ver los {_codeSiblings.Count + 1} agentes de código")
                : "Abrir";
        }

        private void Principal_Click(object sender, MouseButtonEventArgs e)
        {
            if (PrincipalCard.DataContext is not AgentSession p) return;
            if (_codeSiblings.Count == 0) { Open(p); return; }
            // Con más agentes de código, el principal se desglosa para elegir
            _codeExpanded = !_codeExpanded;
            ApplyCodeExpanded();
            Resize();
        }

        private void Row_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is AgentSession s) { Open(s); e.Handled = true; }
        }

        private void Row_RightClick(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not AgentSession s) return;
            var menu = BuildAgentMenu(s);
            menu.PlacementTarget = (UIElement)sender;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void BackToOverview_Click(object sender, RoutedEventArgs e) => SetMode(Mode.Overview);

        private ContextMenu BuildAgentMenu(AgentSession s)
        {
            var m = new ContextMenu();
            void Add(string header, Action a, bool enabled = true)
            {
                var it = new MenuItem { Header = header, IsEnabled = enabled };
                it.Click += (_, _) => a();
                m.Items.Add(it);
            }
            Add("Abrir", () => Open(s));
            Add("Editar agente…", () => OpenEditor(s));
            Add(s.IsDefault ? "★ Es el principal" : "Establecer como principal", () => { AppRef.SetDefault(s); QueueRebuild(); }, !s.IsDefault);
            if (s.IsConversational) Add("Nueva conversación", s.NewConversation);
            if (s.CanResumeQueue) Add($"Reanudar cola ({s.PendingCount})", s.ResumeQueue);
            if (s.PendingCount > 0) Add("Vaciar cola", s.ClearQueue);
            if (s.IsScheduled) Add("Ejecutar ahora", () => _ = s.RunScheduledAsync());
            if (s.HasFolder) Add("Abrir carpeta", () => OpenInExplorer(s.FolderPath));
            if (!s.IsCode) Add("Duplicar", () => Duplicate(s));
            m.Items.Add(new Separator());
            Add(s.IsCode ? "Cerrar agente de código" : "Eliminar", () => ConfirmRemove(s));
            return m;
        }

        // =============================================================== selección / sesiones
        private void Select(AgentSession? s)
        {
            if (_selected != null)
            {
                _selected.IsSelected = false;
                _selected.Messages.CollectionChanged -= Messages_CollectionChanged;
            }
            _selected = s;
            ChatView.DataContext = s;
            InfoView.DataContext = s;
            if (s != null)
            {
                s.IsSelected = true;
                s.Messages.CollectionChanged += Messages_CollectionChanged;
                AppRef.Store.Config.Settings.SelectedAgentId = s.Profile.Id;
            }
            UpdateEmptyChat();
            UpdatePauseButton();
            _autoScroll = true;
            ScrollToEnd();
        }

        private void Sessions_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null) foreach (AgentSession s in e.NewItems) s.PropertyChanged += Session_PropertyChanged;
            if (e.OldItems != null) foreach (AgentSession s in e.OldItems) s.PropertyChanged -= Session_PropertyChanged;
            QueueRebuild();
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
                case nameof(AgentSession.IsDefault):
                case nameof(AgentSession.Profile):
                    QueueRebuild();
                    if (sender == _selected)
                    {
                        UpdateEmptyChat();
                        UpdatePauseButton();
                        // Si cambió el tipo, cambia también la vista
                        if (InAgentView && _selected != null) Open(_selected, focus: false);
                    }
                    break;
                case nameof(AgentSession.NeedsFolder):
                    if (sender == _selected) UpdateEmptyChat();
                    break;
            }
            if (sender == _bannerSession && e.PropertyName == nameof(AgentSession.State) && _mode == Mode.Banner)
                BannerMochi.State = _bannerSession!.State;
        }

        private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyChat();

        private void UpdateEmptyChat()
        {
            var s = _selected;
            bool show = s != null && s.Messages.Count == 0 && !s.NeedsFolder;
            EmptyChat.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (s == null) return;
            EmptyChat.Text = s.IsCode
                ? $"¿Qué hacemos en {s.FolderName}?\nSi le escribes mientras trabaja, se encola."
                : $"Habla con {s.Profile.Name}\n/recordar 10m algo · arrastra archivos sobre el notch";
        }

        private void UpdatePauseButton()
        {
            if (_selected?.IsScheduled != true) return;
            PauseButton.Content = _selected.IsPaused ? "" : "";
            PauseButton.ToolTip = _selected.IsPaused ? "Reanudar programación" : "Pausar programación";
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
            if (_mode == Mode.Overview || (InAgentView && !IsActive)) _collapseTimer.Start();
            if (_mode == Mode.Banner && !_bannerTimer.IsEnabled) _bannerTimer.Start();
        }

        private void Collapsed_Click(object sender, MouseButtonEventArgs e)
        {
            // El primer clic solo muestra la vista general
            _hoverTimer.Stop();
            SetMode(Mode.Overview);
            Activate();
        }

        private void Banner_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) != null) return;
            Expand(focusInput: true, agentId: _bannerAgentId);
        }

        private void BannerClose_Click(object sender, RoutedEventArgs e) => Collapse();

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
            if (files.Count == 0 || _selected?.CanType != true) return;
            if (_mode != Mode.Chat) Open(_selected);
            var block = "Archivos:\n" + string.Join("\n", files.Select(f => "- " + f));
            Input.Text = string.IsNullOrWhiteSpace(Input.Text) ? block + "\n\n" : Input.Text.TrimEnd() + "\n\n" + block + "\n\n";
            Input.CaretIndex = Input.Text.Length;
            Activate();
            Input.Focus();
        }

        // =============================================================== agentes programados
        private void RunNow_Click(object sender, RoutedEventArgs e)
        {
            if (_selected?.IsScheduled != true) return;
            if (_selected.IsBusy) return;
            _ = _selected.RunScheduledAsync();
            AppRef.SaveAgents();
        }

        private void TogglePause_Click(object sender, RoutedEventArgs e)
        {
            if (_selected?.IsScheduled != true) return;
            var sc = _selected.Profile.Schedule;
            sc.Paused = !sc.Paused;
            if (!sc.Paused) ScheduleService.Reschedule(_selected);
            _selected.RaiseProfileChanged();
            AppRef.SaveAgents();
            UpdatePauseButton();
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
            if (_mode is Mode.Collapsed or Mode.Banner) SetMode(Mode.Overview);
            if (_selected != null) _selected.IsBox = true;
        }

        private void Shell_DragLeave(object sender, DragEventArgs e)
        {
            if (!Shell.IsMouseOver) foreach (var s in AppRef.Sessions) s.IsBox = false;
        }

        /// <summary>
        /// Carpetas: si el agente de código abierto aún no tiene carpeta, se le asigna; cualquier otra
        /// carpeta crea un agente de código nuevo. Archivos: se adjuntan al mensaje del agente abierto.
        /// </summary>
        private void Shell_Drop(object sender, DragEventArgs e)
        {
            foreach (var s in AppRef.Sessions) s.IsBox = false;
            var paths = DroppedPaths(e);
            var dirs = paths.Where(Directory.Exists).ToList();
            var files = paths.Where(File.Exists).ToList();

            if (dirs.Count > 0)
            {
                AgentSession? last = null;
                foreach (var d in dirs)
                {
                    var target = _selected?.NeedsFolder == true && last == null ? _selected
                               : last == null ? AppRef.Sessions.FirstOrDefault(x => x.NeedsFolder) : null;
                    if (target != null)
                    {
                        if (target.TrySetFolder(d, out var err)) { AppRef.SaveAgents(); last = target; QueueRebuild(); }
                        else target.AddNotice(err ?? "No se pudo usar esa carpeta", true);
                        continue;
                    }
                    last = AppRef.AddCodeAgent(d);
                }
                if (last != null) Open(last);
            }
            if (files.Count > 0) AppendFiles(files);
        }

        // =============================================================== crear / editar / cerrar
        private void AddAgentMenu_Click(object sender, RoutedEventArgs e)
        {
            var m = new ContextMenu { PlacementTarget = AddButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            void Add(string header, Action a) { var it = new MenuItem { Header = header }; it.Click += (_, _) => a(); m.Items.Add(it); }
            Add("Agente de código (elegir carpeta)…", PickFolderAndCreateCodeAgent);
            Add("Agente de chat…", () => OpenEditor(null, AgentKind.Interactive));
            Add("Agente programado…", () => OpenEditor(null, AgentKind.Scheduled));
            m.IsOpen = true;
        }

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

        private void PickFolderAndCreateCodeAgent()
        {
            var folder = PickFolder("Carpeta del nuevo agente de código");
            if (folder == null) return;
            Open(AppRef.AddCodeAgent(folder));
        }

        private void PickFolderForSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_selected?.NeedsFolder != true) return;
            var folder = PickFolder($"Carpeta de trabajo de {_selected.Profile.Name}");
            if (folder == null) return;
            if (_selected.TrySetFolder(folder, out var err)) { AppRef.SaveAgents(); QueueRebuild(); FocusInput(); }
            else _selected.AddNotice(err ?? "No se pudo usar esa carpeta", true);
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

        private void EditSelected_Click(object sender, RoutedEventArgs e) { if (_selected != null) OpenEditor(_selected); }

        private void CloseAgent_Click(object sender, RoutedEventArgs e) { if (_selected != null) ConfirmRemove(_selected); }

        private void ConfirmRemove(AgentSession s)
        {
            _modalOpen = true;
            var what = s.IsCode ? $"¿Cerrar el agente de código {s.Profile.Name} ({s.FolderName})?\nSu conversación se borrará; la carpeta no se toca."
                                : $"¿Eliminar a {s.Profile.Name} y su conversación?";
            var ok = MessageBox.Show(this, what, "Coucou", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            _modalOpen = false;
            Activate();
            if (!ok) return;
            bool wasOpen = _selected == s;
            AppRef.RemoveAgent(s);
            if (wasOpen) SetMode(Mode.Overview);
        }

        private void Duplicate(AgentSession s)
        {
            var p = s.Profile.Clone();
            p.Id = Guid.NewGuid().ToString("N");
            p.Name += " 2";
            p.Schedule.NextRun = null;
            p.Schedule.LastRun = null;
            Open(AppRef.AddAgent(p));
        }

        public void OpenEditor(AgentSession? session, AgentKind kindForNew = AgentKind.Interactive)
        {
            _modalOpen = true;
            try
            {
                var win = new AgentEditorWindow(session?.Profile.Clone(), kindForNew, session?.HasFolder == true,
                                                session?.IsDefault ?? false) { Owner = this };
                if (win.ShowDialog() != true || win.Result == null) return;
                var p = win.Result;
                if (session == null)
                {
                    var ns = AppRef.AddAgent(p);
                    if (win.MakeDefault) AppRef.SetDefault(ns);
                    ns.AddNotice($"¡Hola! Soy {p.Name} ({ns.KindLabel.ToLowerInvariant()}). Funciono con {ns.ProviderLabel}.");
                    Open(ns, focus: false);
                }
                else
                {
                    bool contextChanged = session.Profile.Provider != p.Provider || session.Profile.Kind != p.Kind;
                    session.UpdateProfile(p);
                    if (contextChanged) session.NewConversation();
                    if (p.Kind == AgentKind.Scheduled) ScheduleService.Reschedule(session);
                    if (win.MakeDefault && !session.IsDefault) AppRef.SetDefault(session);
                    AppRef.SaveAgents();
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
                if (_mode != Mode.Collapsed && _mode != Mode.Banner && IsActive) { _pinned = false; UpdatePin(); Collapse(); }
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
