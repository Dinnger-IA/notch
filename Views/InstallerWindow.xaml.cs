using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using AgentManagerNotch.Controls;
using AgentManagerNotch.Services;

namespace AgentManagerNotch.Views
{
    /// <summary>Instalar (o desinstalar) Agent Manager Notch para el usuario actual. Ver <see cref="Installer"/>.</summary>
    public partial class InstallerWindow : Window
    {
        private readonly bool _uninstall;
        private readonly string _version;
        private bool _done;

        public InstallerWindow(bool uninstall, string version)
        {
            InitializeComponent();
            Icon = NotificationService.WindowIcon;
            _uninstall = uninstall;
            _version = version;
            if (uninstall)
            {
                Title = "Desinstalar Agent Manager Notch";
                SubHeading.Text = "Se quitará del equipo: el programa, sus accesos directos y el inicio con Windows.";
                InstallOptions.Visibility = Visibility.Collapsed;
                UninstallOptions.Visibility = Visibility.Visible;
                DataHint.Text = $"Agentes, conversaciones y sesión de {ConfigStore.Root}. Si no lo marcas, se conservan para cuando vuelvas a instalar.";
                MainButton.Content = "Desinstalar";
                MainButton.Background = (Brush)FindResource("ErrorFg");
                MainButton.Foreground = new SolidColorBrush(Color.FromRgb(0x2A, 0x0E, 0x0E));
                Mochi.State = MochiState.Waiting;
                return;
            }
            var installed = Installer.InstalledVersion;
            SubHeading.Text = installed == null ? $"Versión {version} · tus agentes de IA en el notch."
                : installed == version ? $"La versión {version} ya está instalada; puedes reinstalarla."
                : $"Se actualizará de la versión {installed} a la {version}. Tus agentes y conversaciones se conservan.";
            if (installed != null)
            {
                MainButton.Content = installed == version ? "Reinstalar" : "Actualizar";
                StartupOption.IsChecked = Installer.StartupPointsToInstalled();
            }
            Where.Text = $"Se instala en {Installer.InstallDir}, solo para tu usuario (no pide permisos de administrador). " +
                         "Si Agent Manager Notch está abierto, se cerrará.";
        }

        private async void Main_Click(object sender, RoutedEventArgs e)
        {
            if (_done) { Close(); return; }
            MainButton.IsEnabled = CancelButton.IsEnabled = false;
            InstallOptions.IsEnabled = UninstallOptions.IsEnabled = false;
            Mochi.State = MochiState.Working;
            ShowStatus(_uninstall ? "Desinstalando…" : "Instalando…");
            var options = new Installer.Options(StartupOption.IsChecked == true, DesktopOption.IsChecked == true, LaunchOption.IsChecked == true);
            bool removeData = RemoveDataOption.IsChecked == true;
            try
            {
                // En un hilo STA aparte: la copia no congela la ventana y el acceso directo usa COM
                await RunSta(() => { if (_uninstall) Installer.Uninstall(removeData); else Installer.Install(options, _version); });
                _done = true;
                Mochi.State = MochiState.Done;
                ShowStatus(_uninstall
                    ? "Listo: Agent Manager Notch se quitó del equipo." + (removeData ? "" : " Tus datos siguen en su carpeta.")
                    : "¡Listo! Lo encontrarás en el menú Inicio" + (options.StartWithWindows ? " y se abrirá solo al iniciar Windows." : "."));
                MainButton.Content = "Cerrar";
                MainButton.IsEnabled = true;
                CancelButton.Visibility = Visibility.Collapsed;
                Where.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Log.Error(_uninstall ? "Desinstalar" : "Instalar", ex);
                Mochi.State = MochiState.Error;
                ShowStatus(ex is InvalidOperationException ? ex.Message : $"No se pudo {(_uninstall ? "desinstalar" : "instalar")}: {ex.Message}", error: true);
                MainButton.IsEnabled = CancelButton.IsEnabled = true;
                InstallOptions.IsEnabled = UninstallOptions.IsEnabled = true;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        private void ShowStatus(string text, bool error = false)
        {
            Status.Text = text;
            Status.Foreground = (Brush)FindResource(error ? "ErrorFg" : "TextPrimary");
            Status.Visibility = Visibility.Visible;
        }

        private static Task RunSta(Action action)
        {
            var tcs = new TaskCompletionSource();
            var t = new Thread(() =>
            {
                try { action(); tcs.SetResult(); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            return tcs.Task;
        }
    }
}
