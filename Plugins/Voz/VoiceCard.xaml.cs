using System.Windows;
using System.Windows.Controls;
using AgentManagerNotch.Services;

namespace AgentManagerNotch.Plugins.Voz
{
    /// <summary>⚙ Configuración → *Voz*: cómo se usa el micrófono y *Probar la voz* con el agente elegido.</summary>
    public partial class VoiceCard : Border
    {
        private readonly VoicePlugin _plugin;
        private AgentEntry? _target;

        public VoiceCard(VoicePlugin plugin)
        {
            InitializeComponent();
            _plugin = plugin;
        }

        public void Refresh(AgentEntry? target) => _target = target;

        private void Test_Click(object sender, RoutedEventArgs e)
        {
            var who = _target ?? _plugin.Host?.Principal;
            if (who?.ActiveTab is { } s) _plugin.Speak(s, $"Hola, soy {who.Name}. Así sueno cuando te cuento lo que he investigado.");
        }
    }
}
