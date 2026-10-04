using System;
using System.Linq;
using System.Windows.Controls;
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
        /// <summary>Pasó la comprobación de Agent Manager (se pierde al cambiar la opción).</summary>
        private bool _amPassed;
        private static readonly Brush OkBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x4D, 0xD0, 0xA1)));
        private record StepView(string Icon, Brush Color, string Name, string Detail);

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
                InstallOptions.Visibility = AmOptions.Visibility = Visibility.Collapsed;
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
            AmOption.IsChecked = SavedUseAgentManager() ?? true;
            UpdateAmState();
            Where.Text = $"Se instala en {Installer.InstallDir}, solo para tu usuario (no pide permisos de administrador). " +
                         "Si Agent Manager Notch está abierto, se cerrará.";
        }

        private async void Main_Click(object sender, RoutedEventArgs e)
        {
            if (_done) { Close(); return; }
            bool useAm = AmOption.IsChecked == true;
            if (!_uninstall && useAm && !_amPassed) { UpdateAmState(); return; } // sin comprobación no se pasa
            MainButton.IsEnabled = CancelButton.IsEnabled = false;
            InstallOptions.IsEnabled = UninstallOptions.IsEnabled = AmOptions.IsEnabled = false;
            Mochi.State = MochiState.Working;
            ShowStatus(_uninstall ? "Desinstalando…" : "Instalando…");
            var options = new Installer.Options(StartupOption.IsChecked == true, DesktopOption.IsChecked == true, LaunchOption.IsChecked == true);
            bool removeData = RemoveDataOption.IsChecked == true;
            try
            {
                // En un hilo STA aparte: la copia no congela la ventana y el acceso directo usa COM
                await RunSta(() =>
                {
                    if (_uninstall) { Installer.Uninstall(removeData); return; }
                    AgentManagerCheck.SaveChoice(useAm); // antes de abrirlo, para que el primer arranque ya sepa el camino
                    Installer.Install(options, _version);
                });
                _done = true;
                Mochi.State = MochiState.Done;
                ShowStatus(_uninstall
                    ? "Listo: Agent Manager Notch se quitó del equipo." + (removeData ? "" : " Tus datos siguen en su carpeta.")
                    : "¡Listo! Lo encontrarás en el menú Inicio" + (options.StartWithWindows ? " y se abrirá solo al iniciar Windows." : ".") +
                      (useAm ? " Al abrirlo, inicia sesión con tu cuenta de Agent Manager." : ""));
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
                CancelButton.IsEnabled = true;
                InstallOptions.IsEnabled = UninstallOptions.IsEnabled = AmOptions.IsEnabled = true;
                UpdateAmState();
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        // =============================================================== Agent Manager
        private void AmOption_Click(object sender, RoutedEventArgs e)
        {
            _amPassed = false;
            AmSteps.ItemsSource = null;
            AmSummary.Text = "Hay que comprobarlo antes de instalar.";
            AmSummary.Foreground = (Brush)FindResource("TextSecondary");
            Mochi.State = MochiState.Idle;
            UpdateAmState();
        }

        /// <summary>Con Agent Manager marcado, Instalar solo se habilita tras pasar la comprobación.</summary>
        private void UpdateAmState()
        {
            bool useAm = AmOption.IsChecked == true;
            AmCheckPanel.Visibility = useAm ? Visibility.Visible : Visibility.Collapsed;
            bool blocked = useAm && !_amPassed;
            MainButton.IsEnabled = !blocked;
            MainButton.Opacity = blocked ? 0.45 : 1;
            MainButton.ToolTip = blocked ? "Primero pulsa «Comprobar conexión» (o desmarca «Usar Agent Manager»)." : null;
            ToolTipService.SetShowOnDisabled(MainButton, true);
        }

        private async void AmCheck_Click(object sender, RoutedEventArgs e)
        {
            AmCheckButton.IsEnabled = false;
            AmSteps.ItemsSource = null;
            AmSummary.Text = "Comprobando…";
            AmSummary.Foreground = (Brush)FindResource("TextSecondary");
            Mochi.State = MochiState.Thinking;
            try
            {
                var steps = await AgentManagerCheck.RunAsync();
                AmSteps.ItemsSource = steps.Select(s => new StepView(s.Ok ? "" : "",
                    s.Ok ? OkBrush : (Brush)FindResource("ErrorFg"), s.Name, s.Detail)).ToList();
                _amPassed = steps.All(s => s.Ok);
                Mochi.State = _amPassed ? MochiState.Done : MochiState.Error;
                AmSummary.Text = _amPassed ? "Todo correcto: ya puedes instalar."
                    : "No se puede instalar con Agent Manager hasta resolverlo (o desmarca la opción para instalar sin él).";
                AmSummary.Foreground = _amPassed ? OkBrush : (Brush)FindResource("ErrorFg");
                Log.Info("Instalador: comprobación de Agent Manager " + (_amPassed ? "correcta" : "fallida: " +
                    string.Join(" | ", steps.Where(s => !s.Ok).Select(s => s.Detail))));
            }
            finally
            {
                AmCheckButton.IsEnabled = true;
                UpdateAmState();
            }
        }

        /// <summary>Lo elegido en una instalación anterior (null si no hay configuración).</summary>
        private static bool? SavedUseAgentManager()
        {
            try
            {
                var path = System.IO.Path.Combine(ConfigStore.Root, "config.json");
                if (!System.IO.File.Exists(path)) return null;
                return System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(path))?["Settings"]?["UseAgentManager"]?.GetValue<bool>() ?? true;
            }
            catch { return null; }
        }

        private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

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
