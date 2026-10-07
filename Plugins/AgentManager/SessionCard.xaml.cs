using System;
using System.Windows;
using System.Windows.Controls;

namespace AgentManagerNotch.Plugins.AgentManager
{
    /// <summary>⚙ Configuración → *Sesión*: con quién se ha entrado y cerrar sesión, o activar Agent Manager si se instaló sin él.</summary>
    public partial class SessionCard : Border
    {
        private readonly AgentManagerPlugin _plugin;

        public SessionCard(AgentManagerPlugin plugin)
        {
            InitializeComponent();
            _plugin = plugin;
        }

        public void Refresh()
        {
            bool useAm = _plugin.Enabled;
            SessionUser.Text = useAm ? $"Conectado como {_plugin.Auth.UserName ?? "usuario de Agent Manager"}"
                                     : "No se usa Agent Manager: los agentes trabajan con los CLI de este equipo.";
            LogoutButton.Visibility = useAm ? Visibility.Visible : Visibility.Collapsed;
            UseAmButton.Visibility = useAm ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Logout_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin.Host?.Confirm("¿Cerrar la sesión de Agent Manager?\nTendrás que volver a iniciar sesión para ver tus agentes.") == true)
                _ = _plugin.Auth.LogoutAsync();
        }

        /// <summary>Se instaló sin Agent Manager y ahora se quiere usar: hay que iniciar sesión.</summary>
        private async void UseAgentManager_Click(object sender, RoutedEventArgs e) => await _plugin.EnableAsync();
    }
}
