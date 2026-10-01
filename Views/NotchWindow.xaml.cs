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
        private enum Mode { Collapsed, Banner, Overview, Chat, Schedule }

        private Mode _mode = Mode.Collapsed;
        private bool _pinned, _modalOpen, _autoScroll = true, _rebuildQueued, _scheduledExpanded;
        private AgentEntry? _focused;      // agente de la tarjeta grande
        private AgentEntry? _openEntry;    // agente abierto en el chat
        private AgentSession? _selected;   // pestaña / sesión abierta en el chat
        private AgentSession? _bannerSession;
        private string? _bannerKey;
        private RemindersWindow? _reminders;

        private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
        private readonly DispatcherTimer _bannerTimer = new() { Interval = TimeSpan.FromSeconds(5) };
        private readonly DispatcherTimer _leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
        private static bool ClickOutsideOnly => App.Current.Store.Config.Settings.CloseOnClickOutsideOnly;

        private static App AppRef => App.Current;
        private bool IsPanel(Mode m) => m is Mode.Overview or Mode.Chat or Mode.Schedule;
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
            _bannerTimer.Tick += (_, _) =>
            {
                _bannerTimer.Stop();
                if (_mode == Mode.Banner && !Shell.IsMouseOver) Collapse();
            };

            ChatScroll.ScrollChanged += ChatScroll_ScrollChanged;
            TaskScroll.ScrollChanged += TaskScroll_ScrollChanged;
            // Por defecto el panel solo se cierra con un clic fuera (hook de ratón), Esc o el atajo.
            // Opción de accesibilidad: cerrarlo al sacar el ratón / al perder el foco.
            Closed += (_, _) => SetOutsideClickHook(false);
            _leaveTimer.Tick += (_, _) =>
            {
                _leaveTimer.Stop();
                if (ClickOutsideOnly || _pinned || _modalOpen || Shell.IsMouseOver || _selected?.PendingApproval != null) return;
                if (_mode == Mode.Overview || (_mode is Mode.Chat or Mode.Schedule && !IsActive)) Collapse();
            };
            Deactivated += (_, _) =>
            {
                if (ClickOutsideOnly) return;
                if (IsPanel(_mode) && !_pinned && !_modalOpen && !Shell.IsMouseOver && _selected?.PendingApproval == null) Collapse();
            };
            SourceInitialized += OnSourceInitialized;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.Invoke(PositionOnScreen);
            Loaded += (_, _) => { PositionOnScreen(); SetMode(Mode.Collapsed, animate: false); };
            AppRef.Reminders.Changed += () => { if (_mode == Mode.Schedule) { RefreshSchedule(); Resize(); } };
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
                case Mode.Chat: return new Size(800, 610);
                case Mode.Schedule:
                    ScheduleContent.Measure(new Size(590, double.PositiveInfinity));
                    return new Size(640, Math.Clamp(ScheduleContent.DesiredSize.Height + 40 + 36, 200, 520));
                case Mode.Banner: return new Size(450, 74);
                case Mode.Overview:
                    OverviewView.Measure(new Size(820, double.PositiveInfinity));
                    return new Size(840, Math.Clamp(OverviewView.DesiredSize.Height + 4 + 40, 180, 560));
                default:
                    CollapsedContent.Measure(new Size(double.PositiveInfinity, 34));
                    return new Size(Math.Max(96, CollapsedContent.DesiredSize.Width + 30), 34);
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
                if (IsPanel(m) != IsPanel(prev) || !animate) ShowView(PanelHost, IsPanel(m));
                ShowView(OverviewView, m == Mode.Overview);
                ShowView(ChatView, m == Mode.Chat);
                ShowView(ScheduleView, m == Mode.Schedule);
                // La barra superior marca dónde estás
                HomePill.Background = new SolidColorBrush(m == Mode.Overview ? Color.FromRgb(0x2C, 0x2C, 0x33) : Colors.Transparent);
                SchedulePill.Background = new SolidColorBrush(m == Mode.Schedule ? Color.FromRgb(0x2C, 0x2C, 0x33) : Colors.Transparent);
                TopTitle.Text = m == Mode.Schedule ? "Tareas programadas" : "";
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
            Shell.CornerRadius = IsPanel(m) ? new CornerRadius(0, 0, 16, 16) : new CornerRadius(0, 0, 10, 10);
            if (m == Mode.Collapsed) foreach (var a in AppRef.Agents) a.IsBox = false;
            SetOutsideClickHook(IsPanel(m) && ClickOutsideOnly);
        }

        // =============================================================== cerrar con clic fuera
        // Hook de ratón de bajo nivel, activo solo con el panel abierto: detecta clics fuera del recuadro
        // aunque la ventana no tenga el foco (p. ej. si se abrió al pasar el ratón).
        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc fn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? name);
        private const int WH_MOUSE_LL = 14, WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207;

        private IntPtr _mouseHook = IntPtr.Zero;
        private LowLevelMouseProc? _mouseProc; // referencia viva para que el GC no la recoja

        private void SetOutsideClickHook(bool on)
        {
            if (on && _mouseHook == IntPtr.Zero)
            {
                _mouseProc ??= MouseHookProc;
                _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);
                if (_mouseHook == IntPtr.Zero) Log.Info("No se pudo instalar el hook de ratón: el panel se cerrará con Esc");
            }
            else if (!on && _mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
        }

        private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
                {
                    var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    var pt = new Point(info.pt.X, info.pt.Y);
                    // Se evalúa fuera del hook para no bloquear el ratón del sistema
                    Dispatcher.BeginInvoke(() => OnGlobalClick(pt));
                }
            }
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private void OnGlobalClick(Point screenPt)
        {
            if (!ClickOutsideOnly || !IsPanel(_mode) || _pinned || _modalOpen) return;
            if (OwnedWindows.Count > 0 && OwnedWindows.Cast<Window>().Any(w => w.IsVisible)) return;
            if (IsInsideShell(screenPt)) return;
            Collapse();
        }

        /// <summary>¿El punto (píxeles de pantalla) cae dentro del recuadro visible del notch?</summary>
        private bool IsInsideShell(Point screenPt)
        {
            if (PresentationSource.FromVisual(Shell) == null) return false;
            var topLeft = Shell.PointToScreen(new Point(0, 0));
            var bottomRight = Shell.PointToScreen(new Point(Shell.ActualWidth, Shell.ActualHeight));
            return screenPt.X >= topLeft.X && screenPt.X <= bottomRight.X && screenPt.Y >= topLeft.Y && screenPt.Y <= bottomRight.Y;
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
            ChatTitle.Text = entry.Name;
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
            if (e != _focused) _scheduledExpanded = false;
            if (_focused != null) _focused.PropertyChanged -= Focused_PropertyChanged;
            _focused = e;
            if (_focused != null) _focused.PropertyChanged += Focused_PropertyChanged;
            RefreshOverview();
        }

        private void Focused_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(AgentEntry.StatusChip) or nameof(AgentEntry.IsScheduled) or nameof(AgentEntry.Profile)
                or nameof(AgentEntry.Headline))
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
            UpdateScheduledDetails();
        }

        /// <summary>Detalle extra de un programado (al pulsar su tarjeta grande).</summary>
        private void UpdateScheduledDetails()
        {
            var f = _focused;
            bool show = _scheduledExpanded && f?.IsScheduled == true;
            ScheduledDetails.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show || f == null) return;
            var s = f.Tabs.FirstOrDefault();
            var sc = f.Profile.Schedule;
            DetailLastOutput.Text = s == null ? "" : s.Messages.LastOrDefault(m => m.Role == ChatRole.Agent)?.Text.Replace("**", "").Trim() ?? "Aún no se ha ejecutado.";
            DetailInstruction.Text = sc.Instruction;
            var next = new List<string>();
            var t = DateTime.Now;
            for (int i = 0; i < 3 && !sc.Paused; i++)
            {
                if (sc.ComputeNext(t) is not { } n) break;
                next.Add(ScheduledItem.FormatWhen(n));
                t = n;
            }
            DetailNextRuns.Text = sc.Paused ? "En pausa" : next.Count > 0 ? string.Join("   ·   ", next) : "Sin próximas ejecuciones";
            var approval = f.Profile.Approval switch
            {
                ApprovalMode.ReadOnly => "solo lectura", ApprovalMode.Auto => "automático",
                ApprovalMode.AcceptEdits => "edita solo", _ => "pregunta"
            };
            var last = sc.LastRun is { } lr ? $"Última: {ScheduledItem.FormatWhen(lr)}{(s?.LastFailed == true ? " (falló)" : "")} · " : "";
            DetailMeta.Text = $"{last}{sc.Summary} · {f.ProviderLabel} · permisos: {approval}" +
                              (sc.KeepContext ? " · recuerda ejecuciones anteriores" : "");
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
            // Pulsar el agente que ya está a la izquierda: chat (interactivos) o más detalle (programados)
            if (_focused?.IsConversational == true) Open(_focused);
            else if (_focused?.IsScheduled == true)
            {
                _scheduledExpanded = !_scheduledExpanded;
                UpdateScheduledDetails();
                Dispatcher.BeginInvoke(Resize, DispatcherPriority.Loaded);
            }
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
            OpenMenu(m);
        }

        private void ShowMenu(ContextMenu menu, UIElement target, MouseButtonEventArgs e)
        {
            menu.PlacementTarget = target;
            OpenMenu(menu);
            e.Handled = true;
        }

        /// <summary>
        /// Abre un menú con la fuente normal (si no, hereda la de iconos del botón) y evita que el panel
        /// se recoja mientras está abierto.
        /// </summary>
        private void OpenMenu(ContextMenu m)
        {
            m.FontFamily = (FontFamily)FindResource("UiFont");
            m.FontSize = 13;
            _modalOpen = true;
            m.Closed += (_, _) => _modalOpen = false;
            m.IsOpen = true;
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

        /// <summary>
        /// Píldora en dos lados: a la izquierda el principal; a la derecha el resto agrupado en un bloque
        /// 2×2 (máximo 4 visibles) del mismo alto que el principal.
        /// </summary>
        private void RebuildPill()
        {
            CollapsedAgents.Children.Clear();
            var principal = Principal;
            if (principal != null)
            {
                var big = Mini(principal, 28);
                CollapsedAgents.Children.Add(big);
            }
            var others = AppRef.Agents.Where(a => a != principal)
                // primero los que necesitan atención o trabajan, luego por tipo
                .OrderByDescending(a => a.NeedsApproval).ThenByDescending(a => a.IsBusy).ThenByDescending(a => a.HasError)
                .ThenBy(a => a.IsCode ? 0 : a.IsScheduled ? 2 : 1)
                .ToList();
            if (others.Count > 0)
            {
                CollapsedAgents.Children.Add(new Border
                {
                    Width = 1, Height = 14, Margin = new Thickness(14, 0, 14, 0),
                    Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF))
                });
                var grid = new System.Windows.Controls.Primitives.UniformGrid
                {
                    Rows = 2, Columns = others.Count == 1 ? 1 : 2, Width = others.Count == 1 ? 14 : 28, Height = 28,
                    VerticalAlignment = VerticalAlignment.Center
                };
                foreach (var a in others.Take(4)) grid.Children.Add(Mini(a, 14));
                var host = new Grid { ToolTip = string.Join(", ", others.Select(a => a.Name)) };
                host.Children.Add(grid);
                if (others.Count > 4)
                {
                    // Indicador de que hay más agentes que los 4 visibles
                    host.Children.Add(new Border
                    {
                        Width = 14, Height = 12, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x33)),
                        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -8, -2),
                        Child = new TextBlock { Text = $"+{others.Count - 4}", FontSize = 8, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center }
                    });
                }
                CollapsedAgents.Children.Add(host);
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
            _taskAutoScroll = true;
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

        private void ScrollToEnd() => Dispatcher.BeginInvoke(() => { ChatScroll.ScrollToEnd(); TaskScroll.ScrollToEnd(); }, DispatcherPriority.Background);

        private bool _taskAutoScroll = true;

        /// <summary>
        /// La lista de tareas se queda siempre al final (lo más reciente) y difumina los bordes que tienen
        /// contenido oculto: arriba las tareas antiguas, abajo las nuevas si el usuario subió.
        /// </summary>
        private void TaskScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.ExtentHeightChange == 0) _taskAutoScroll = TaskScroll.VerticalOffset >= TaskScroll.ScrollableHeight - 2;
            else if (_taskAutoScroll) TaskScroll.ScrollToVerticalOffset(TaskScroll.ExtentHeight);

            bool hiddenAbove = TaskScroll.VerticalOffset > 1;
            bool hiddenBelow = TaskScroll.VerticalOffset < TaskScroll.ScrollableHeight - 1;
            if (!hiddenAbove && !hiddenBelow) { TaskScroll.OpacityMask = null; return; }
            var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            mask.GradientStops.Add(new GradientStop(hiddenAbove ? Colors.Transparent : Colors.Black, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, hiddenAbove ? 0.28 : 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, hiddenBelow ? 0.8 : 1));
            mask.GradientStops.Add(new GradientStop(hiddenBelow ? Colors.Transparent : Colors.Black, 1));
            TaskScroll.OpacityMask = mask;
        }

        // =============================================================== ratón / hover
        private void Shell_MouseEnter(object sender, MouseEventArgs e)
        {
            _leaveTimer.Stop();
            if (_mode is Mode.Collapsed or Mode.Banner) _hoverTimer.Start();
        }

        private void Shell_MouseLeave(object sender, MouseEventArgs e)
        {
            _hoverTimer.Stop();
            if (!ClickOutsideOnly && (_mode == Mode.Overview || (_mode is Mode.Chat or Mode.Schedule && !IsActive))) _leaveTimer.Start();
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

        /// <summary>Inicio: desde el chat o las tareas vuelve a la vista general; en ella, vuelve al principal.</summary>
        private void Home_Click(object sender, RoutedEventArgs e)
        {
            if (_mode == Mode.Overview) { SetFocused(Principal); return; }
            if (_mode == Mode.Chat && _openEntry != null) SetFocused(_openEntry);
            ShowOverview();
        }

        private void ScheduleView_Click(object sender, RoutedEventArgs e)
        {
            if (_mode == Mode.Schedule) { ShowOverview(); return; }
            RefreshSchedule();
            SetMode(Mode.Schedule);
            Activate();
        }

        /// <summary>Recordatorios/tareas (incluidas las que crean los agentes) y agentes programados.</summary>
        private void RefreshSchedule()
        {
            var cfg = AppRef.Store.Config;
            var reminders = cfg.Reminders.OrderBy(r => r.DueAt).Select(r =>
            {
                var a = AppRef.Agents.FirstOrDefault(x => x.Profile.Id == r.AgentId);
                return new ScheduledItem
                {
                    Id = r.Id, IsReminder = true, Entry = a,
                    Color = a?.Color ?? Color.FromRgb(0x8C, 0x8C, 0x99),
                    Title = r.Text,
                    When = ScheduledItem.FormatWhen(r.DueAt),
                    Meta = string.Join(" · ", new[] { a?.Name ?? "Coucou", r.KindLabel, r.RecurrenceLabel }),
                    Tooltip = $"{r.Text}\n{r.DueLabel} · {r.RecurrenceLabel} · {r.KindLabel}",
                    SortKey = r.DueAt
                };
            }).ToList();
            ReminderList.ItemsSource = reminders;
            NoReminders.Visibility = reminders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var agents = AppRef.Agents.Where(a => a.IsScheduled).Select(a =>
            {
                var sc = a.Profile.Schedule;
                var instr = sc.Instruction.Replace("\r", "").Replace("\n", " ");
                return new ScheduledItem
                {
                    Id = a.Profile.Id, Entry = a, Color = a.Color,
                    Title = $"{a.Name} · {instr}",
                    When = sc.Paused ? "En pausa" : sc.NextRun is { } n ? ScheduledItem.FormatWhen(n) : "Sin próximas",
                    Meta = sc.Summary,
                    Tooltip = sc.Instruction,
                    SortKey = sc.NextRun ?? DateTime.MaxValue
                };
            }).OrderBy(x => x.SortKey).ToList();
            ScheduledAgentList.ItemsSource = agents;
            NoScheduledAgents.Visibility = agents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ScheduledItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) != null) return;
            if ((sender as FrameworkElement)?.DataContext is not ScheduledItem it || it.Entry == null) return;
            // Agente programado → su tarjeta; tarea de un agente interactivo → su chat
            if (it.Entry.IsConversational && it.IsReminder) Open(it.Entry);
            else { SetFocused(it.Entry); ShowOverview(); }
        }

        private void DeleteReminder_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ScheduledItem { IsReminder: true } it) AppRef.Reminders.Remove(it.Id);
        }

        private void NewReminder_Click(object sender, RoutedEventArgs e) => OpenReminders();

        /// <summary>Configuración: ajustes del agente y opciones generales de Coucou.</summary>
        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            var st = AppRef.Store.Config.Settings;
            var m = new ContextMenu { PlacementTarget = SettingsButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            void Add(string header, Action act, bool enabled = true)
            {
                var it = new MenuItem { Header = header, IsEnabled = enabled };
                it.Click += (_, _) => act();
                m.Items.Add(it);
            }
            void Toggle(string header, bool value, Action<bool> set)
            {
                var it = new MenuItem { Header = header, IsCheckable = true, IsChecked = value };
                it.Click += (_, _) => { set(it.IsChecked); AppRef.Store.Save(); };
                m.Items.Add(it);
            }
            var target = _mode == Mode.Chat ? _openEntry : _focused;
            if (target != null)
            {
                Add($"Ajustes de {target.Name}…", () => OpenEditor(target));
                if (!target.IsDefault) Add($"Hacer principal a {target.Name}", () => { AppRef.SetDefault(target); RebuildPill(); RefreshOverview(); });
                m.Items.Add(new Separator());
            }
            Add("Nuevo agente de chat…", () => OpenEditor(null, AgentKind.Interactive));
            Add("Nuevo agente programado…", () => OpenEditor(null, AgentKind.Scheduled));
            Add("Nuevo workspace de código…", () => NewWorkspace(null));
            m.Items.Add(new Separator());
            Toggle("Sonidos", st.Sounds, v => st.Sounds = v);
            Toggle("Avisar cuando un agente termina", st.NotifyOnDone, v => st.NotifyOnDone = v);
            Toggle("Avisos de mis sesiones de Claude Code", ControlChannel.IsClaudeIntegrationInstalled(), v =>
            {
                try { ControlChannel.SetClaudeIntegration(v); } catch (Exception ex) { Log.Error("Integración Claude", ex); }
            });
            Toggle("Iniciar con Windows", NotificationService.IsStartupEnabled(), NotificationService.SetStartup);

            // Accesibilidad: cómo se abre y se cierra el panel
            var access = new MenuItem { Header = "Accesibilidad" };
            MenuItem Radio(string header, bool value, Action set)
            {
                var it = new MenuItem { Header = header, IsCheckable = true, IsChecked = value };
                it.Click += (_, _) => { set(); AppRef.Store.Save(); };
                return it;
            }
            access.Items.Add(Radio("Cerrar solo al hacer clic fuera", st.CloseOnClickOutsideOnly, () =>
            {
                st.CloseOnClickOutsideOnly = true;
                SetOutsideClickHook(IsPanel(_mode));
            }));
            access.Items.Add(Radio("Cerrar al sacar el ratón", !st.CloseOnClickOutsideOnly, () =>
            {
                st.CloseOnClickOutsideOnly = false;
                SetOutsideClickHook(false);
            }));
            access.Items.Add(new Separator());
            var hover = new MenuItem { Header = "Abrir al pasar el ratón", IsCheckable = true, IsChecked = st.ExpandOnHover };
            hover.Click += (_, _) => { st.ExpandOnHover = hover.IsChecked; AppRef.Store.Save(); };
            access.Items.Add(hover);
            m.Items.Add(access);
            m.Items.Add(new Separator());
            Add("Abrir carpeta de datos", () => System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", ConfigStore.Root) { UseShellExecute = true }));
            Add("Salir de Coucou", AppRef.ExitApp);
            OpenMenu(m);
        }
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
                if (IsPanel(_mode) && IsActive) { _pinned = false; UpdatePin(); Collapse(); }
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
