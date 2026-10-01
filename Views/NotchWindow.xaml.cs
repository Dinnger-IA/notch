using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
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
        private bool _pinned, _modalOpen, _autoScroll = true, _rebuildQueued;
        private AgentSession? _selected;
        private AgentSession? _bannerSession;
        private string? _bannerAgentId;
        private RemindersWindow? _reminders;
        private string _layoutSignature = "";

        private readonly AgentGroupView _codeGroup = new() { Title = "Código", ShowAddButton = true, AddTooltip = "Nuevo agente de código (elige o arrastra una carpeta)" };
        private readonly AgentGroupView _chatGroup = new() { Title = "Chat" };
        private readonly AgentGroupView _schedGroup = new() { Title = "Programados" };

        private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
        private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
        private readonly DispatcherTimer _bannerTimer = new() { Interval = TimeSpan.FromSeconds(5) };

        private static App AppRef => App.Current;

        public NotchWindow()
        {
            InitializeComponent();

            foreach (var g in new[] { _codeGroup, _chatGroup, _schedGroup })
            {
                g.AgentClicked += s => { Select(s); FocusInput(); };
                g.MenuFactory = BuildAgentMenu;
                GroupsHost.Children.Add(g);
            }
            _codeGroup.AddClicked += PickFolderAndCreateCodeAgent;

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
                case Mode.Expanded: return new Size(800, 660);
                case Mode.Banner: return new Size(450, 74);
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
            if (focusInput) FocusInput();
            ScrollToEnd();
        }

        private void FocusInput()
        {
            Activate();
            if (_selected?.CanType == true)
                Dispatcher.BeginInvoke(() => { Input.Focus(); Keyboard.Focus(Input); }, DispatcherPriority.Input);
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

        // =============================================================== agentes: principal + grupos
        private void QueueRebuild()
        {
            if (_rebuildQueued) return;
            _rebuildQueued = true;
            Dispatcher.BeginInvoke(() => { _rebuildQueued = false; RebuildAgents(); }, DispatcherPriority.Background);
        }

        /// <summary>Reconstruye la cabecera y la píldora: principal grande, el resto pequeños por grupo.</summary>
        private void RebuildAgents(bool force = false)
        {
            var sessions = AppRef.Sessions.ToList();
            var sig = string.Join("|", sessions.Select(s => $"{s.Profile.Id}:{s.Profile.Kind}:{s.IsDefault}"));
            if (!force && sig == _layoutSignature) { UpdateCollapsedSize(); return; }
            _layoutSignature = sig;

            var primary = sessions.FirstOrDefault(s => s.IsDefault);
            var others = sessions.Where(s => s != primary).ToList();
            var code = others.Where(s => s.IsCode).ToList();
            var chat = others.Where(s => s.Profile.Kind == AgentKind.Interactive).ToList();
            var sched = others.Where(s => s.IsScheduled).ToList();

            // --- cabecera del panel
            PrimaryHost.Child = primary == null ? null : BuildPrimaryTab(primary);
            _codeGroup.SetItems(code);
            _chatGroup.SetItems(chat);
            _chatGroup.Visibility = chat.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _schedGroup.SetItems(sched);
            _schedGroup.Visibility = sched.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            // --- píldora: principal grande + grupos pequeños superpuestos
            CollapsedAgents.Children.Clear();
            if (primary != null)
            {
                var big = new MochiView { Width = 36, Height = 36, Margin = new Thickness(0, 0, 4, 0), ToolTip = primary.Profile.Name };
                AgentGroupView.Bind(big, primary);
                CollapsedAgents.Children.Add(big);
            }
            foreach (var group in new[] { code, chat, sched })
            {
                if (group.Count == 0) continue;
                CollapsedAgents.Children.Add(new Border { Width = 1, Height = 16, Margin = new Thickness(6, 0, 6, 0), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) });
                int i = 0;
                foreach (var s in group)
                {
                    var mini = new MochiView { Width = 22, Height = 22, Margin = new Thickness(i == 0 ? 0 : -8, 0, 0, 0), ToolTip = $"{s.Profile.Name} · {s.SubLabel}" };
                    AgentGroupView.Bind(mini, s);
                    Panel.SetZIndex(mini, group.Count - i);
                    CollapsedAgents.Children.Add(mini);
                    i++;
                }
            }

            NoAgents.Visibility = sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_selected == null || !sessions.Contains(_selected)) Select(primary ?? sessions.FirstOrDefault());
            UpdateCollapsedSize();
        }

        private FrameworkElement BuildPrimaryTab(AgentSession s)
        {
            var tab = _chatGroup.BuildTab(s, 60, 104);
            tab.Margin = new Thickness(0, 0, 4, 0);
            var star = new TextBlock
            {
                Text = "", FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFont"), FontSize = 10,
                Foreground = (System.Windows.Media.Brush)FindResource("AlertBorder"),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 6, 0),
                ToolTip = "Agente principal"
            };
            var g = new Grid();
            g.Children.Add(tab);
            g.Children.Add(star);
            return g;
        }

        private ContextMenu BuildAgentMenu(AgentSession s)
        {
            var m = new ContextMenu();
            void Add(string header, Action a, bool enabled = true)
            {
                var it = new MenuItem { Header = header, IsEnabled = enabled };
                it.Click += (_, _) => a();
                m.Items.Add(it);
            }
            Add("Editar agente…", () => OpenEditor(s));
            Add(s.IsDefault ? "★ Es el principal" : "Establecer como principal", () => { AppRef.SetDefault(s); QueueRebuild(); }, !s.IsDefault);
            if (s.IsConversational) Add("Nueva conversación", s.NewConversation);
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
            ExpandedView.DataContext = s;
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
                    if (sender == _selected) { UpdateEmptyChat(); UpdatePauseButton(); }
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
            if (s.IsScheduled)
            {
                EmptyTitle.Text = "Aquí verás el resultado de cada ejecución";
                EmptyHint.Text = "Usa «Ejecutar ahora» para probar la instrucción.";
            }
            else if (s.IsCode)
            {
                EmptyTitle.Text = $"¿Qué hacemos en {s.FolderName}?";
                EmptyHint.Text = "Si le pides algo mientras trabaja, se añade a Pendientes · arrastra archivos para adjuntarlos";
            }
            else
            {
                EmptyTitle.Text = $"Habla con {s.Profile.Name}";
                EmptyHint.Text = "Arrastra archivos sobre el personaje · /recordar 10m algo · /tarea 9:00 revisa el correo";
            }
        }

        private void UpdatePauseButton()
        {
            if (_selected?.IsScheduled != true) return;
            PauseButton.Content = _selected.IsPaused ? "Reanudar" : "Pausar";
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
            // Clic en un personaje de la píldora → abre su panel
            if (e.OriginalSource is DependencyObject d && FindAncestor<MochiView>(d)?.DataContext is AgentSession s) Select(s);
            Expand(focusInput: true);
        }

        private void Banner_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) != null) return;
            Expand(focusInput: true, agentId: _bannerAgentId);
        }

        private void BannerClose_Click(object sender, RoutedEventArgs e) => Collapse();

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
            var block = "Archivos:\n" + string.Join("\n", files.Select(f => "- " + f));
            Input.Text = string.IsNullOrWhiteSpace(Input.Text) ? block + "\n\n" : Input.Text.TrimEnd() + "\n\n" + block + "\n\n";
            Input.CaretIndex = Input.Text.Length;
            Activate();
            Input.Focus();
        }

        // =============================================================== barra lateral de tareas
        private void TaskInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || _selected == null) return;
            e.Handled = true;
            var text = TaskInput.Text.Trim();
            if (text == "") return;
            TaskInput.Clear();
            _ = _selected.SendAsync(text); // libre → empieza ya; ocupado → a la cola
        }

        private void TaskInput_TextChanged(object sender, TextChangedEventArgs e) =>
            TaskPlaceholder.Visibility = TaskInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void RemoveQueued_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is AgentTask t) _selected?.RemoveQueued(t);
        }

        private void ResumeQueue_Click(object sender, RoutedEventArgs e) => _selected?.ResumeQueue();
        private void ClearCompleted_Click(object sender, RoutedEventArgs e) => _selected?.ClearCompleted();

        // =============================================================== agentes programados
        private void RunNow_Click(object sender, RoutedEventArgs e)
        {
            if (_selected?.IsScheduled != true) return;
            if (_selected.IsBusy) { _selected.AddNotice("Ya se está ejecutando.", true); return; }
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
            if (_mode != Mode.Expanded) Expand(false);
            if (_selected != null) _selected.IsBox = true;
        }

        private void Shell_DragLeave(object sender, DragEventArgs e)
        {
            if (!Shell.IsMouseOver) foreach (var s in AppRef.Sessions) s.IsBox = false;
        }

        /// <summary>
        /// Carpetas: si el agente de código seleccionado aún no tiene carpeta, se le asigna;
        /// cualquier otra carpeta crea un agente de código nuevo. Archivos: se adjuntan al mensaje.
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
                    if (_selected?.NeedsFolder == true && last == null)
                    {
                        if (_selected.TrySetFolder(d, out var err)) { AppRef.SaveAgents(); last = _selected; QueueRebuild(); }
                        else _selected.AddNotice(err ?? "No se pudo usar esa carpeta", true);
                        continue;
                    }
                    last = AppRef.AddCodeAgent(d);
                }
                if (last != null) { Select(last); FocusInput(); }
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
            var s = AppRef.AddCodeAgent(folder);
            Select(s);
            FocusInput();
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
            AppRef.RemoveAgent(s);
        }

        private void Duplicate(AgentSession s)
        {
            var p = s.Profile.Clone();
            p.Id = Guid.NewGuid().ToString("N");
            p.Name += " 2";
            p.Schedule.NextRun = null;
            p.Schedule.LastRun = null;
            Select(AppRef.AddAgent(p));
        }

        public void OpenEditor(AgentSession? session, AgentKind kindForNew = AgentKind.Interactive)
        {
            _modalOpen = true;
            try
            {
                if (_mode != Mode.Expanded) SetMode(Mode.Expanded);
                var win = new AgentEditorWindow(session?.Profile.Clone(), kindForNew, session?.HasFolder == true,
                                                session?.IsDefault ?? false) { Owner = this };
                if (win.ShowDialog() != true || win.Result == null) return;
                var p = win.Result;
                if (session == null)
                {
                    var ns = AppRef.AddAgent(p);
                    if (win.MakeDefault) AppRef.SetDefault(ns);
                    Select(ns);
                    ns.AddNotice($"¡Hola! Soy {p.Name} ({ns.KindLabel.ToLowerInvariant()}). Funciono con {ns.ProviderLabel}.");
                }
                else
                {
                    bool contextChanged = session.Profile.Provider != p.Provider || session.Profile.Kind != p.Kind;
                    bool scheduleChanged = p.Kind == AgentKind.Scheduled;
                    session.UpdateProfile(p);
                    if (contextChanged) session.NewConversation();
                    if (scheduleChanged) ScheduleService.Reschedule(session);
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
                if (_mode == Mode.Expanded && IsActive) { _pinned = false; UpdatePin(); Collapse(); }
                else Expand(focusInput: true);
            }
            return IntPtr.Zero;
        }

        private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
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
