using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AgentManagerNotch.Plugins.Archivos
{
    /// <summary>
    /// Archivos del equipo para los agentes que no tienen herramientas propias (los del LLM de un servidor, que no ve
    /// el disco): leer, buscar, crear y editar archivos de texto, Excel y Word, abrir documentos y, por defecto, los
    /// reportes como dashboard HTML (<see cref="LocalFiles"/>, <see cref="OfficeFiles"/>). Se activa o desactiva en
    /// ⚙ Configuración → *Archivos del equipo* (<c>Settings.UseLocalFiles</c>, activo por defecto); desactivado, el
    /// agente no recibe las instrucciones ni se ejecuta ningún bloque. Los agentes con CLI (Claude Code, Codex,
    /// Gemini) usan las herramientas de su CLI y no dependen de este plugin.
    /// </summary>
    public sealed class FilesPlugin : INotchPlugin
    {
        public const string UseKey = "UseLocalFiles";

        public string Id => "archivos";
        public string Name => "Archivos del equipo";

        /// <summary>Activo (por defecto sí). Solo tiene sentido en la sesión normal del notch.</summary>
        public static bool Enabled => PluginHost.Host?.Get(UseKey, true) ?? true;

        public int? RunCommandLine(string[] args) => args.Length > 0 && args[0] == "--probar-archivos" ? LocalFiles.SelfCheck() : null;

        public void Start(NotchHost host)
        {
            T Styled<T>(T el, string style) where T : FrameworkElement { el.SetResourceReference(FrameworkElement.StyleProperty, style); return el; }
            var toggle = Styled(new CheckBox { HorizontalAlignment = HorizontalAlignment.Right }, "Switch");
            toggle.Click += (_, _) => host.Set(UseKey, toggle.IsChecked == true);
            var row = Styled(new Grid(), "SettingsRow");
            row.Children.Add(Styled(new TextBlock { Text = "Leer y editar archivos de este equipo" }, "SettingsLabel"));
            row.Children.Add(toggle);
            var panel = new StackPanel();
            panel.Children.Add(Styled(new TextBlock { Text = "ARCHIVOS DEL EQUIPO" }, "SettingsTitle"));
            panel.Children.Add(row);
            panel.Children.Add(new TextBlock
            {
                Text = "Los agentes de chat leen, buscan, crean y editan archivos de texto, Excel y Word, y hacen los reportes " +
                       "como dashboard HTML. Antes de cada cambio te piden permiso en el notch.",
                FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x7C)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
            });
            host.AddSettingsCard(Styled(new Border { Child = panel }, "SettingsCard"), SettingsColumn.Left, _ => toggle.IsChecked = host.Get(UseKey, true));
        }
    }
}
