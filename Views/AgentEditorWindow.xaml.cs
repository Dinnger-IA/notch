using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CoucouWin.Controls;
using CoucouWin.Models;
using CoucouWin.Providers;
using CoucouWin.Services;

namespace CoucouWin.Views
{
    public partial class AgentEditorWindow : Window
    {
        public AgentProfile? Result { get; private set; }
        private readonly AgentProfile _p;

        private static readonly string[] Palette =
        {
            "#FF8A65", "#FFB74D", "#FFD54F", "#AED581", "#4DD0A1", "#4DD0E1",
            "#64B5F6", "#7986CB", "#9C8CFF", "#BA68C8", "#F06292", "#E57373",
            "#A1887F", "#B0BEC5", "#F5F5F5", "#455A64"
        };

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

        public AgentEditorWindow(AgentProfile? profile)
        {
            InitializeComponent();
            bool isNew = profile == null;
            _p = profile ?? new AgentProfile { Name = "Nuevo agente", ColorHex = Palette[new Random().Next(Palette.Length)] };
            Title = isNew ? "Nuevo agente" : $"Editar · {_p.Name}";
            Icon = NotificationService.WindowIcon;

            foreach (var hex in Palette)
            {
                var c = AgentSession.ParseColor(hex);
                var sw = new Border
                {
                    Width = 26, Height = 26, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 6, 6),
                    Background = new SolidColorBrush(c), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = hex,
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1)
                };
                sw.MouseLeftButtonUp += (_, _) => ColorBox.Text = hex;
                Swatches.Children.Add(sw);
            }

            foreach (var t in Presets)
            {
                var b = new Button { Content = t.Name, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 4, 10, 4) };
                b.Click += (_, _) => ApplyTemplate(t);
                Templates.Children.Add(b);
            }

            foreach (ProviderKind k in Enum.GetValues<ProviderKind>())
                ProviderBox.Items.Add(new ComboBoxItem { Content = ProviderFactory.Label(k), Tag = k });
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
            ColorBox.Text = _p.ColorHex;
            ProviderBox.SelectedItem = ProviderBox.Items.Cast<ComboBoxItem>().First(i => (ProviderKind)i.Tag == _p.Provider);
            ApprovalBox.SelectedItem = ApprovalBox.Items.Cast<ComboBoxItem>().First(i => (ApprovalMode)i.Tag == _p.Approval);
            ModelBox.Text = _p.Model;
            DirBox.Text = _p.WorkingDirectory;
            PromptBox.Text = _p.SystemPrompt;
            ExtraBox.Text = _p.ExtraArgs;
            CustomBox.Text = _p.CustomCommand;
            Loaded += (_, _) => NameBox.Focus();
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
            ColorBox.Text = t.Color;
            ProviderBox.SelectedItem = ProviderBox.Items.Cast<ComboBoxItem>().First(i => (ProviderKind)i.Tag == t.Provider);
            ApprovalBox.SelectedItem = ApprovalBox.Items.Cast<ComboBoxItem>().First(i => (ApprovalMode)i.Tag == t.Approval);
            PromptBox.Text = t.Prompt;
            if (t.Provider == ProviderKind.Custom && string.IsNullOrWhiteSpace(CustomBox.Text)) CustomBox.Text = "ollama run llama3.2";
            Preview.Celebrate();
        }

        private ProviderKind SelectedProvider => ProviderBox.SelectedItem is ComboBoxItem i ? (ProviderKind)i.Tag : ProviderKind.ClaudeCode;

        private void Provider_Changed(object sender, SelectionChangedEventArgs e)
        {
            var k = SelectedProvider;
            CustomPanel.Visibility = k == ProviderKind.Custom ? Visibility.Visible : Visibility.Collapsed;
            var current = ModelBox.Text;
            ModelBox.Items.Clear();
            foreach (var m in k switch
                     {
                         ProviderKind.ClaudeCode => new[] { "", "opus", "sonnet", "haiku" },
                         ProviderKind.Codex => new[] { "" },
                         ProviderKind.Gemini => new[] { "", "gemini-2.5-pro", "gemini-2.5-flash" },
                         _ => new[] { "" }
                     })
                ModelBox.Items.Add(m);
            ModelBox.Text = current;

            if (k == ProviderKind.Custom) { ProviderStatus.Text = "Se ejecuta el comando que escribas abajo."; return; }
            var exe = ProviderFactory.Executable(k);
            var found = CliResolver.Find(exe);
            ProviderStatus.Text = found != null ? $"✓ Encontrado: {found}" : $"⚠ No se encontró «{exe}» en el PATH";
        }

        private void Name_Changed(object sender, TextChangedEventArgs e) { }

        private void ColorBox_Changed(object sender, TextChangedEventArgs e)
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(ColorBox.Text.Trim());
                Preview.BodyColor = c;
            }
            catch { /* mientras escribe */ }
        }

        private void PickColor_Click(object sender, RoutedEventArgs e)
        {
            var cur = Preview.BodyColor;
            using var dlg = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true,
                Color = System.Drawing.Color.FromArgb(cur.R, cur.G, cur.B)
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                ColorBox.Text = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
        }

        private void PreviewState_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (PreviewState.SelectedItem is ComboBoxItem i) Preview.State = (MochiState)i.Tag;
        }

        private void Celebrate_Click(object sender, RoutedEventArgs e) => Preview.Celebrate();
        private void Box_Click(object sender, RoutedEventArgs e) => Preview.IsBox = !Preview.IsBox;

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Carpeta de trabajo del agente" };
            if (!string.IsNullOrWhiteSpace(DirBox.Text) && System.IO.Directory.Exists(DirBox.Text)) dlg.InitialDirectory = DirBox.Text;
            if (dlg.ShowDialog(this) == true) DirBox.Text = dlg.FolderName;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var name = NameBox.Text.Trim();
            if (name == "") { NameBox.Focus(); return; }
            string hex;
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(ColorBox.Text.Trim());
                hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            }
            catch { ColorBox.Focus(); return; }
            if (SelectedProvider == ProviderKind.Custom && string.IsNullOrWhiteSpace(CustomBox.Text)) { CustomBox.Focus(); return; }

            _p.Name = name;
            _p.ColorHex = hex;
            _p.Provider = SelectedProvider;
            _p.Approval = ApprovalBox.SelectedItem is ComboBoxItem a ? (ApprovalMode)a.Tag : ApprovalMode.Ask;
            _p.Model = ModelBox.Text.Trim();
            _p.WorkingDirectory = DirBox.Text.Trim();
            _p.SystemPrompt = PromptBox.Text.Trim();
            _p.ExtraArgs = ExtraBox.Text.Trim();
            _p.CustomCommand = CustomBox.Text.Trim();
            Result = _p;
            DialogResult = true;
        }
    }
}
