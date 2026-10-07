using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentManagerNotch.Controls;
using AgentManagerNotch.Models;
using AgentManagerNotch.Plugins;
using AgentManagerNotch.Providers;
using AgentManagerNotch.Services;

namespace AgentManagerNotch.Views
{
    public partial class AgentEditorWindow : Window
    {
        public AgentProfile? Result { get; private set; }
        /// <summary>El usuario marcó «Usar como agente principal».</summary>
        public bool MakeDefault { get; private set; }
        /// <summary>El usuario pulsó «Eliminar agente» y lo confirmó.</summary>
        public bool DeleteRequested { get; private set; }
        /// <summary>Agente de la franja superior al que quiere pasar (los cambios del actual ya se guardaron o descartaron).</summary>
        public string? SwitchToId { get; private set; }
        /// <summary>Agente para la franja superior.</summary>
        public record AgentChip(string Id, string Name, Color Color);
        private string _snapshot = "";
        private readonly AgentProfile _p;
        private readonly bool _isNew, _folderLocked;
        private readonly List<CheckBox> _dayChecks = new();
        private bool _loading = true;

        private static readonly DayOfWeek[] WeekOrder =
        {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
            DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
        };
        private static readonly string[] DayShort = { "Lun", "Mar", "Mié", "Jue", "Vie", "Sáb", "Dom" };

        private static readonly string[] Palette =
        {
            "#FF8A65", "#FFB74D", "#FFD54F", "#AED581", "#4DD0A1", "#4DD0E1",
            "#64B5F6", "#7986CB", "#9C8CFF", "#BA68C8", "#F06292", "#E57373",
            "#A1887F", "#B0BEC5", "#F5F5F5"
        };

        /// <summary>Color elegido (hex). Un agente antiguo puede tener uno que no está en la paleta: se conserva.</summary>
        private string _colorHex = "";

        private record Preset(string Name, string Color, ProviderKind Provider, ApprovalMode Approval, string Prompt);
        private static readonly Preset[] Presets =
        {
            new Preset("Revisor", "#64B5F6", ProviderKind.ClaudeCode, ApprovalMode.ReadOnly,
                "Eres un revisor de código meticuloso. Buscas bugs, problemas de seguridad y código confuso. Das hallazgos concretos con archivo y línea, ordenados por gravedad. No modificas archivos."),
            new Preset("Ingeniero", "#4DD0A1", ProviderKind.Codex, ApprovalMode.AcceptEdits,
                "Eres un ingeniero de software senior. Implementas cambios pequeños y verificables, ejecutas las pruebas y resumes lo que hiciste en 3 líneas."),
            new Preset("Investigador", "#BA68C8", ProviderKind.ClaudeCode, ApprovalMode.ReadOnly,
                "Eres un investigador. Buscas en la web y en los archivos, contrastas fuentes y respondes con un resumen claro y enlaces."),
            new Preset("Asistente", "#FFD54F", ProviderKind.ClaudeCode, ApprovalMode.ReadOnly,
                "Eres un asistente personal. Ayudas a planificar el día, redactar mensajes y crear recordatorios. Respondes breve y amable."),
            new Preset("Escritor", "#F06292", ProviderKind.ClaudeCode, ApprovalMode.ReadOnly,
                "Eres un escritor y editor. Mejoras textos manteniendo la voz del autor, corriges ortografía y propones títulos."),
            new Preset("Local", "#B0BEC5", ProviderKind.Custom, ApprovalMode.ReadOnly,
                "Eres un asistente útil y conciso.")
        };

        /// <param name="profile">null para crear uno nuevo.</param>
        /// <param name="kindForNew">Tipo inicial cuando se crea uno nuevo.</param>
        /// <param name="folderLocked">Agente de código que ya tiene carpeta: no se puede cambiar.</param>
        /// <param name="kindLocked">El tipo no se puede cambiar (p. ej. agente de código con workspaces abiertos).</param>
        public AgentEditorWindow(AgentProfile? profile, AgentKind kindForNew = AgentKind.Interactive, bool kindLocked = false, bool isDefault = false,
                                 IReadOnlyList<AgentChip>? agents = null)
        {
            InitializeComponent();
            _isNew = profile == null;
            _folderLocked = false;
            bool isNew = _isNew;
            _p = profile ?? new AgentProfile
            {
                Name = "Nuevo agente", ColorHex = Palette[new Random().Next(Palette.Length)], Kind = kindForNew,
                Approval = kindForNew == AgentKind.Scheduled ? ApprovalMode.ReadOnly : ApprovalMode.Ask,
                SystemPrompt = kindForNew == AgentKind.Code ? ConfigStore.CodePrompt : ""
            };
            Title = isNew ? "Nuevo agente" : $"Editar · {_p.Name}";
            DefaultCheck.IsChecked = isDefault;
            DeleteButton.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;

            foreach (var (k, label) in new[]
                     {
                         (AgentKind.Interactive, "Chat — conversacional, con lista de tareas"),
                         (AgentKind.Code, "Código — conversacional, trabaja en una carpeta fija"),
                         (AgentKind.Scheduled, "Programado — ejecuta una instrucción según un horario")
                     })
                KindBox.Items.Add(new ComboBoxItem { Content = label, Tag = k });
            KindBox.IsEnabled = !kindLocked;

            for (int i = 0; i < 7; i++)
            {
                var cb = new CheckBox { Content = DayShort[i], Tag = WeekOrder[i], Margin = new Thickness(0, 0, 10, 4), MinWidth = 0 };
                cb.Checked += Schedule_Changed;
                cb.Unchecked += Schedule_Changed;
                _dayChecks.Add(cb);
                DaysPanel.Children.Add(cb);
            }
            for (int h = 1; h <= 24; h++) IntervalBox.Items.Add(new ComboBoxItem { Content = $"{h} h", Tag = h });
            Icon = NotificationService.WindowIcon;

            foreach (var hex in Palette)
            {
                var c = AgentSession.ParseColor(hex);
                var sw = new Border
                {
                    Width = 26, Height = 26, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 6, 6),
                    Background = new SolidColorBrush(c), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = hex, Tag = hex,
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1)
                };
                sw.MouseLeftButtonUp += (_, _) => SetColor(hex);
                Swatches.Children.Add(sw);
            }

            foreach (var t in Presets)
            {
                var b = new Button { Content = t.Name, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 4, 10, 4) };
                b.Click += (_, _) => ApplyTemplate(t);
                Templates.Children.Add(b);
            }

            foreach (ProviderKind k in Enum.GetValues<ProviderKind>())
                if (k != ProviderKind.Plugin)
                    ProviderBox.Items.Add(new ComboBoxItem { Content = ProviderFactory.Label(k), Tag = new ProviderChoice(k) });
            // Proveedores de los plugins: los disponibles y, siempre, el que ya usa este agente
            var current = ChoiceOf(_p.Provider, _p.PluginProvider);
            foreach (var pp in PluginHost.Providers)
            {
                var choice = new ProviderChoice(ProviderKind.Plugin, pp.Id);
                if (choice == current || pp.IsAvailable()) ProviderBox.Items.Add(new ComboBoxItem { Content = pp.Label, Tag = choice });
            }
            if (!ProviderBox.Items.Cast<ComboBoxItem>().Any(i => (ProviderChoice)i.Tag == current))
                ProviderBox.Items.Add(new ComboBoxItem { Content = $"{_p.PluginProvider} (no disponible)", Tag = current });
            foreach (var (mode, label) in new[]
                     {
                         (ApprovalMode.Ask, "Preguntar en el notch antes de cambiar algo (recomendado)"),
                         (ApprovalMode.AcceptEdits, "Editar archivos solo, preguntar para comandos"),
                         (ApprovalMode.ReadOnly, "Solo lectura (no puede modificar nada)"),
                         (ApprovalMode.Auto, "Automático: sin preguntas (¡cuidado!)")
                     })
                ApprovalBox.Items.Add(new ComboBoxItem { Content = label, Tag = mode });
            foreach (MochiState s in Enum.GetValues<MochiState>())
                PreviewState.Items.Add(new ComboBoxItem { Content = StateLabel(s), Tag = s });
            PreviewState.SelectedIndex = 0;

            NameBox.Text = _p.Name;
            SetColor(_p.ColorHex);
            ProviderBox.SelectedItem = ProviderBox.Items.Cast<ComboBoxItem>().First(i => (ProviderChoice)i.Tag == current);
            ApprovalBox.SelectedItem = ApprovalBox.Items.Cast<ComboBoxItem>().First(i => (ApprovalMode)i.Tag == _p.Approval);
            ModelBox.Text = _p.Model;
            DirBox.Text = _p.WorkingDirectory;
            PromptBox.Text = _p.SystemPrompt;
            ExtraBox.Text = _p.ExtraArgs;
            CustomBox.Text = _p.CustomCommand;

            var sc = _p.Schedule;
            InstructionBox.Text = sc.Instruction;
            (sc.Mode == ScheduleMode.FixedTimes ? ModeFixed : ModeInterval).IsChecked = true;
            IntervalBox.SelectedIndex = Math.Clamp(sc.IntervalHours, 1, 24) - 1;
            FromBox.Text = sc.WindowStart;
            ToBox.Text = sc.WindowEnd;
            TimesBox.Text = string.Join(", ", sc.Times);
            foreach (var cb in _dayChecks) cb.IsChecked = sc.Days.Contains((DayOfWeek)cb.Tag);
            KeepContextCheck.IsChecked = sc.KeepContext;
            PausedCheck.IsChecked = sc.Paused;

            KindBox.SelectedItem = KindBox.Items.Cast<ComboBoxItem>().First(i => (AgentKind)i.Tag == _p.Kind);
            _loading = false;
            UpdateKindUi();
            UpdateSchedulePreview();
            BuildAgentStrip(agents ?? Array.Empty<AgentChip>(), isNew ? null : _p.Id);
            Loaded += (_, _) => { NameBox.Focus(); _snapshot = Snapshot(); };
        }

        private AgentKind SelectedKind => KindBox.SelectedItem is ComboBoxItem i ? (AgentKind)i.Tag : AgentKind.Interactive;

        private void Kind_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            // Un programado no puede pedir permisos (nadie responde): proponemos solo lectura
            if (SelectedKind == AgentKind.Scheduled && ApprovalBox.SelectedItem is ComboBoxItem a && (ApprovalMode)a.Tag == ApprovalMode.Ask)
                ApprovalBox.SelectedItem = ApprovalBox.Items.Cast<ComboBoxItem>().First(i => (ApprovalMode)i.Tag == ApprovalMode.ReadOnly);
            if (SelectedKind == AgentKind.Code && string.IsNullOrWhiteSpace(PromptBox.Text)) PromptBox.Text = ConfigStore.CodePrompt;
            UpdateKindUi();
        }

        private void UpdateKindUi()
        {
            var k = SelectedKind;
            SchedulePanel.Visibility = k == AgentKind.Scheduled ? Visibility.Visible : Visibility.Collapsed;
            KindHint.Text = k switch
            {
                AgentKind.Code => "Chat con pestañas de workspace: cada pestaña es una carpeta donde se abre el CLI. Se crean desde el notch (arrastrando una carpeta o con «Nuevo workspace»).",
                AgentKind.Scheduled => "No tiene chat: ejecuta la instrucción en los días y horas que elijas (mínimo cada 1 hora) y te avisa al terminar. Como nadie puede aprobar permisos, usa Solo lectura o Automático.",
                _ => "Conversacional: chat con barra lateral de tarea actual, pendientes y completadas."
            };
            // Los agentes de código no tienen una carpeta: cada workspace (pestaña) tiene la suya
            FolderSection.Visibility = k == AgentKind.Code ? Visibility.Collapsed : Visibility.Visible;
            FolderLabel.Text = "Carpeta de trabajo (opcional)";
            DirBox.IsReadOnly = _folderLocked;
            BrowseButton.IsEnabled = !_folderLocked;
            FolderNote.Text = _folderLocked
                ? "🔒 La carpeta no se puede cambiar. Para otra carpeta, cierra este agente y abre uno nuevo."
                : k == AgentKind.Code
                    ? "Puedes dejarla vacía y arrastrar la carpeta sobre el notch después. Una vez elegida, queda fija."
                    : "Vacía = una carpeta propia de Agent Manager Notch.";
        }

        private ScheduleSpec ReadSchedule()
        {
            var sc = _p.Schedule.Clone();
            sc.Instruction = InstructionBox.Text.Trim();
            sc.Mode = ModeFixed.IsChecked == true ? ScheduleMode.FixedTimes : ScheduleMode.Interval;
            sc.IntervalHours = IntervalBox.SelectedItem is ComboBoxItem i ? (int)i.Tag : 1;
            sc.WindowStart = FromBox.Text.Trim();
            sc.WindowEnd = ToBox.Text.Trim();
            sc.Times = TimesBox.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
            sc.Days = _dayChecks.Where(c => c.IsChecked == true).Select(c => (DayOfWeek)c.Tag).ToList();
            sc.KeepContext = KeepContextCheck.IsChecked == true;
            sc.Paused = PausedCheck.IsChecked == true;
            return sc;
        }

        private void Schedule_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            UpdateSchedulePreview();
        }

        private void UpdateSchedulePreview()
        {
            if (IntervalPanel == null) return;
            bool fixedMode = ModeFixed.IsChecked == true;
            IntervalPanel.Visibility = fixedMode ? Visibility.Collapsed : Visibility.Visible;
            FixedPanel.Visibility = fixedMode ? Visibility.Visible : Visibility.Collapsed;

            var sc = ReadSchedule();
            var err = sc.Validate();
            if (err != null && !(string.IsNullOrWhiteSpace(sc.Instruction) && err.StartsWith("Escribe")))
            {
                SchedulePreview.Text = "⚠ " + err;
                SchedulePreview.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x8A));
                return;
            }
            var next = new List<DateTime>();
            var t = DateTime.Now;
            for (int i = 0; i < 3; i++)
            {
                var n = sc.ComputeNext(t);
                if (n == null) break;
                next.Add(n.Value);
                t = n.Value;
            }
            SchedulePreview.Foreground = new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x99));
            SchedulePreview.Text = sc.Summary + (next.Count > 0
                ? "\nPróximas: " + string.Join("  ·  ", next.Select(x => x.ToString("ddd dd HH:mm")))
                : "") + (sc.Paused ? "\n(en pausa)" : "");
        }

        private void SetDays(params DayOfWeek[] days)
        {
            foreach (var cb in _dayChecks) cb.IsChecked = days.Contains((DayOfWeek)cb.Tag);
        }
        private void DaysWeekdays_Click(object sender, RoutedEventArgs e) => SetDays(WeekOrder.Take(5).ToArray());
        private void DaysAll_Click(object sender, RoutedEventArgs e) => SetDays(WeekOrder);
        private void DaysWeekend_Click(object sender, RoutedEventArgs e) => SetDays(DayOfWeek.Saturday, DayOfWeek.Sunday);

        private void DirBox_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = !_folderLocked && e.Data.GetData(DataFormats.FileDrop) is string[] p && p.Length > 0 && Directory.Exists(p[0])
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void DirBox_Drop(object sender, DragEventArgs e)
        {
            if (!_folderLocked && e.Data.GetData(DataFormats.FileDrop) is string[] p && p.Length > 0 && Directory.Exists(p[0]))
                DirBox.Text = p[0];
            e.Handled = true;
        }

        private static string StateLabel(MochiState s) => s switch
        {
            MochiState.Idle => "En reposo",
            MochiState.Thinking => "Pensando",
            MochiState.Working => "Trabajando",
            MochiState.Waiting => "Esperando permiso",
            MochiState.Done => "¡Listo!",
            MochiState.Error => "Error",
            MochiState.Sleeping => "Durmiendo",
            MochiState.Alarm => "Recordatorio",
            _ => s.ToString()
        };

        private void ApplyTemplate(Preset t)
        {
            if (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text == "Nuevo agente") NameBox.Text = t.Name;
            SetColor(t.Color);
            ProviderBox.SelectedItem = ProviderBox.Items.Cast<ComboBoxItem>().First(i => (ProviderChoice)i.Tag == new ProviderChoice(t.Provider));
            ApprovalBox.SelectedItem = ApprovalBox.Items.Cast<ComboBoxItem>().First(i => (ApprovalMode)i.Tag == t.Approval);
            PromptBox.Text = t.Prompt;
            if (t.Provider == ProviderKind.Custom && string.IsNullOrWhiteSpace(CustomBox.Text)) CustomBox.Text = "ollama run llama3.2";
            Preview.Celebrate();
        }

        /// <summary>Un CLI, o el proveedor de un plugin (Kind=Plugin con su id).</summary>
        private sealed record ProviderChoice(ProviderKind Kind, string? PluginId = null);
        private static ProviderChoice ChoiceOf(ProviderKind kind, string? pluginId) =>
            new(kind, kind == ProviderKind.Plugin ? pluginId : null);

        private ProviderChoice SelectedChoice => ProviderBox.SelectedItem is ComboBoxItem i ? (ProviderChoice)i.Tag : new ProviderChoice(ProviderKind.ClaudeCode);
        private ProviderKind SelectedProvider => SelectedChoice.Kind;

        private void Provider_Changed(object sender, SelectionChangedEventArgs e)
        {
            var k = SelectedProvider;
            var plugin = k == ProviderKind.Plugin ? PluginHost.Provider(SelectedChoice.PluginId) : null;
            CustomPanel.Visibility = k == ProviderKind.Custom ? Visibility.Visible : Visibility.Collapsed;
            var current = ModelBox.Text;
            ModelBox.Items.Clear();
            foreach (var m in k switch
                     {
                         ProviderKind.ClaudeCode => new[] { "", "opus", "sonnet", "haiku" },
                         ProviderKind.Codex => new[] { "" },
                         ProviderKind.Gemini => new[] { "", "gemini-2.5-pro", "gemini-2.5-flash" },
                         ProviderKind.Plugin => plugin?.Models ?? new[] { "" },
                         _ => new[] { "" }
                     })
                ModelBox.Items.Add(m);
            ModelBox.Text = current;

            if (k == ProviderKind.Custom) { ProviderStatus.Text = "Se ejecuta el comando que escribas abajo."; return; }
            if (k == ProviderKind.Plugin)
            {
                ProviderStatus.Text = plugin == null ? "⚠ Este proveedor no está disponible en esta versión." : plugin.StatusText;
                return;
            }
            var exe = ProviderFactory.Executable(k);
            var found = CliResolver.Find(exe);
            ProviderStatus.Text = found != null ? $"✓ Encontrado: {found}" : $"⚠ No se encontró «{exe}» en el PATH";
        }

        private void Name_Changed(object sender, TextChangedEventArgs e) { }

        /// <summary>Elige un color: lo aplica a la vista previa y resalta su muestra.</summary>
        private void SetColor(string hex)
        {
            var c = AgentSession.ParseColor(hex);
            _colorHex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            Preview.BodyColor = c;
            foreach (var sw in Swatches.Children.OfType<Border>())
            {
                bool sel = string.Equals((string)sw.Tag, _colorHex, StringComparison.OrdinalIgnoreCase);
                sw.BorderBrush = new SolidColorBrush(sel ? Colors.White : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
                sw.BorderThickness = new Thickness(sel ? 2 : 1);
            }
        }

        private void PreviewState_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (PreviewState.SelectedItem is ComboBoxItem i) Preview.State = (MochiState)i.Tag;
        }

        private void Celebrate_Click(object sender, RoutedEventArgs e) => Preview.Celebrate();
        private void Box_Click(object sender, RoutedEventArgs e) => Preview.IsBox = !Preview.IsBox;

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            if (_folderLocked) return;
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Carpeta de trabajo del agente" };
            if (!string.IsNullOrWhiteSpace(DirBox.Text) && System.IO.Directory.Exists(DirBox.Text)) dlg.InitialDirectory = DirBox.Text;
            if (dlg.ShowDialog(this) == true) DirBox.Text = dlg.FolderName;
        }

        // ---------------- franja de agentes ----------------
        private void BuildAgentStrip(IReadOnlyList<AgentChip> agents, string? currentId)
        {
            foreach (var a in agents)
            {
                bool current = a.Id == currentId;
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new MochiView { Width = 24, Height = 24, BodyColor = a.Color, Interactive = false, IsHitTestVisible = false });
                row.Children.Add(new TextBlock
                {
                    Text = a.Name, Margin = new Thickness(6, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5,
                    FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = new SolidColorBrush(current ? Colors.White : Color.FromRgb(0xB4, 0xB4, 0xC0))
                });
                var chip = new Border
                {
                    Child = row, Padding = new Thickness(8, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0), CornerRadius = new CornerRadius(16),
                    Background = new SolidColorBrush(current ? Color.FromArgb(0x40, a.Color.R, a.Color.G, a.Color.B) : Color.FromRgb(0x1A, 0x1A, 0x21)),
                    BorderBrush = new SolidColorBrush(current ? a.Color : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
                    Cursor = current ? null : System.Windows.Input.Cursors.Hand,
                    ToolTip = current ? "Editando" : $"Editar {a.Name}"
                };
                if (!current) chip.MouseLeftButtonUp += (_, _) => SwitchTo(a);
                AgentStrip.Children.Add(chip);
            }
        }

        /// <summary>Estado del formulario, para saber si hay cambios sin guardar.</summary>
        private string Snapshot() => string.Join("\u001F", new[]
        {
            NameBox.Text, _colorHex, ModelBox.Text, DirBox.Text, PromptBox.Text, ExtraBox.Text, CustomBox.Text,
            SelectedKind.ToString(), SelectedChoice.ToString(), (ApprovalBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "",
            DefaultCheck.IsChecked.ToString(), InstructionBox.Text, ModeFixed.IsChecked.ToString(),
            (IntervalBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "", FromBox.Text, ToBox.Text, TimesBox.Text,
            string.Join(",", _dayChecks.Select(c => c.IsChecked)), KeepContextCheck.IsChecked.ToString(), PausedCheck.IsChecked.ToString()
        });

        private void SwitchTo(AgentChip target)
        {
            if (Snapshot() != _snapshot)
            {
                var r = MessageBox.Show(this, $"¿Guardar los cambios de {(NameBox.Text.Trim() is { Length: > 0 } n ? n : _p.Name)} antes de pasar a {target.Name}?",
                    "Agente", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
                if (r == MessageBoxResult.Cancel) return;
                if (r == MessageBoxResult.Yes)
                {
                    if (!TrySave()) return;
                    SwitchToId = target.Id;
                    DialogResult = true;
                    return;
                }
            }
            SwitchToId = target.Id;
            DialogResult = false;
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            var what = _p.Workspaces.Count > 0
                ? $"¿Eliminar a {_p.Name} y sus {_p.Workspaces.Count} workspace(s)?\nSe borran las conversaciones; las carpetas no se tocan."
                : $"¿Eliminar a {_p.Name} y su conversación?";
            var ok = MessageBox.Show(this, what + "\n\nEsta acción no se puede deshacer.", "Eliminar agente",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            if (!ok) return;
            DeleteRequested = true;
            DialogResult = true;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (TrySave()) DialogResult = true;
        }

        /// <summary>Valida el formulario y deja el perfil en <see cref="Result"/>. false si falta algo.</summary>
        private bool TrySave()
        {
            var name = NameBox.Text.Trim();
            if (name == "") { NameBox.Focus(); return false; }
            if (SelectedProvider == ProviderKind.Custom && string.IsNullOrWhiteSpace(CustomBox.Text)) { CustomBox.Focus(); return false; }

            var kind = SelectedKind;
            var dir = DirBox.Text.Trim();
            if (!_folderLocked && dir != "" && !Directory.Exists(dir))
            {
                MessageBox.Show(this, "Esa carpeta no existe.", "Agent Manager Notch", MessageBoxButton.OK, MessageBoxImage.Warning);
                DirBox.Focus();
                return false;
            }
            if (kind == AgentKind.Scheduled)
            {
                var sc = ReadSchedule();
                var err = sc.Validate();
                if (err != null)
                {
                    MessageBox.Show(this, err, "Horario", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                _p.Schedule = sc;
            }

            _p.Kind = kind;
            MakeDefault = DefaultCheck.IsChecked == true;
            _p.Name = name;
            _p.ColorHex = _colorHex;
            _p.Provider = SelectedProvider;
            _p.PluginProvider = SelectedChoice.PluginId;
            _p.Approval = ApprovalBox.SelectedItem is ComboBoxItem a ? (ApprovalMode)a.Tag : ApprovalMode.Ask;
            _p.Model = ModelBox.Text.Trim();
            if (kind == AgentKind.Code) _p.WorkingDirectory = "";
            else _p.WorkingDirectory = dir == "" ? "" : Path.GetFullPath(dir);
            _p.SystemPrompt = PromptBox.Text.Trim();
            _p.ExtraArgs = ExtraBox.Text.Trim();
            _p.CustomCommand = CustomBox.Text.Trim();
            Result = _p;
            return true;
        }
    }
}
