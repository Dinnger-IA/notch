using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentManagerNotch.Controls;
using AgentManagerNotch.Models;
using AgentManagerNotch.Providers;
using AgentManagerNotch.Services;

namespace AgentManagerNotch.Views
{
    /// <summary>
    /// Instalador por etapas (ver <see cref="Installer"/>): Herramientas (CLI instalados, con botón para instalar los
    /// que falten, y sus avisos de la terminal) → Opciones → Instalación (programa, avisos y los pasos que añada cada
    /// rama). Otras ediciones pueden añadir etapas y pasos con <see cref="ConfigureStages"/> en un archivo aparte,
    /// sin tocar este. También desinstala.
    /// </summary>
    public partial class InstallerWindow : Window
    {
        /// <summary>Una etapa del asistente: su panel, si deja seguir y qué hacer al entrar.</summary>
        private sealed record Stage(string Title, FrameworkElement View, Func<bool>? CanContinue = null, Action? OnEnter = null);

        private readonly bool _uninstall;
        private readonly string _version;
        private readonly List<Stage> _stages = new();
        /// <summary>Pasos tras copiar el programa (los añade cada rama); devuelven false si hay que parar.</summary>
        private readonly List<Func<Task<bool>>> _afterInstall = new();
        private readonly Dictionary<ProviderKind, CliSetup.CliState> _cli = new();
        private int _stage;
        private bool _installing, _done;

        private static readonly Brush OkBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x4D, 0xD0, 0xA1)));
        private static readonly Brush DimBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA8)));
        private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xF2)));
        private Brush ErrorBrush => (Brush)FindResource("ErrorFg");

        /// <summary>Punto de extensión: etapas extra (al principio) y pasos tras instalar.</summary>
        partial void ConfigureStages();

        public InstallerWindow(bool uninstall, string version)
        {
            InitializeComponent();
            Icon = NotificationService.WindowIcon;
            _uninstall = uninstall;
            _version = version;
            if (uninstall)
            {
                Title = "Desinstalar Agent Manager Notch";
                SubHeading.Text = "Se quitará del equipo: el programa, sus accesos directos, el inicio con Windows y los avisos de la terminal.";
                StageLabel.Visibility = Visibility.Collapsed;
                UninstallOptions.Visibility = Visibility.Visible;
                DataHint.Text = $"Agentes, conversaciones y sesión de {ConfigStore.Root}. Si no lo marcas, se conservan para cuando vuelvas a instalar.";
                MainButton.Content = "Desinstalar";
                MainButton.Background = ErrorBrush;
                MainButton.Foreground = new SolidColorBrush(Color.FromRgb(0x2A, 0x0E, 0x0E));
                Mochi.State = MochiState.Waiting;
                return;
            }

            var installed = Installer.InstalledVersion;
            SubHeading.Text = installed == null ? $"Versión {version} · tus agentes de IA en el notch."
                : installed == version ? $"La versión {version} ya está instalada; puedes reinstalarla."
                : $"Se actualizará de la versión {installed} a la {version}. Tus agentes y conversaciones se conservan.";
            if (installed != null) StartupOption.IsChecked = Installer.StartupPointsToInstalled();
            Where.Text = $"Se instala en {Installer.InstallDir}, solo para tu usuario (no pide permisos de administrador). " +
                         "Si Agent Manager Notch está abierto, se cerrará.";

            _stages.Add(new Stage("Herramientas", ToolsStage, OnEnter: () => _ = DetectClisAsync()));
            _stages.Add(new Stage("Opciones", OptionsStage));
            ConfigureStages();
            ShowStage(0);
        }

        // =============================================================== navegación
        private void ShowStage(int i)
        {
            _stage = i;
            foreach (var s in _stages) s.View.Visibility = Visibility.Collapsed;
            _stages[i].View.Visibility = Visibility.Visible;
            StageLabel.Text = $"PASO {i + 1} DE {_stages.Count + 1} · {_stages[i].Title.ToUpperInvariant()}";
            BackButton.Visibility = i > 0 ? Visibility.Visible : Visibility.Collapsed;
            MainButton.Content = i == _stages.Count - 1 ? (Installer.InstalledVersion == null ? "Instalar"
                : Installer.InstalledVersion == _version ? "Reinstalar" : "Actualizar") : "Siguiente";
            _stages[i].OnEnter?.Invoke();
            RefreshNav();
        }

        /// <summary>Habilita «Siguiente/Instalar» según la etapa (las etapas lo llaman cuando cambia algo).</summary>
        private void RefreshNav()
        {
            if (_uninstall || _installing || _done) return;
            bool ok = _stages[_stage].CanContinue?.Invoke() ?? true;
            MainButton.IsEnabled = ok;
            MainButton.Opacity = ok ? 1 : 0.45;
        }

        private void Back_Click(object sender, RoutedEventArgs e) { if (_stage > 0 && !_installing) ShowStage(_stage - 1); }
        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        private async void Main_Click(object sender, RoutedEventArgs e)
        {
            if (_done) { Finish(); return; }
            if (_uninstall) { await UninstallAsync(); return; }
            if (!(_stages[_stage].CanContinue?.Invoke() ?? true)) return;
            if (_stage < _stages.Count - 1) { ShowStage(_stage + 1); return; }
            await InstallAsync();
        }

        // =============================================================== herramientas (CLI)
        private bool _detecting;

        private async Task DetectClisAsync()
        {
            if (_detecting) return;
            _detecting = true;
            CliRows.Children.Clear();
            foreach (var cli in CliSetup.Clis) CliRows.Children.Add(CliRow(cli, null, "Comprobando…"));
            foreach (var cli in CliSetup.Clis)
            {
                var state = await Task.Run(() => CliSetup.Detect(cli));
                _cli[cli] = state;
                ReplaceCliRow(cli, state, null);
            }
            _detecting = false;
        }

        private void ReplaceCliRow(ProviderKind cli, CliSetup.CliState? state, string? message, bool error = false)
        {
            int i = Array.IndexOf(CliSetup.Clis, cli);
            CliRows.Children.RemoveAt(i);
            CliRows.Children.Insert(i, CliRow(cli, state, message, error));
        }

        private FrameworkElement CliRow(ProviderKind cli, CliSetup.CliState? state, string? message, bool error = false)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bool installed = state?.Installed == true;
            var icon = new TextBlock
            {
                Text = state == null ? "" : installed ? "" : "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12,
                Foreground = state == null ? DimBrush : installed ? OkBrush : ErrorBrush, Margin = new Thickness(0, 2, 0, 0)
            };
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = ProviderFactory.Label(cli), FontSize = 13, Foreground = TextBrush });
            text.Children.Add(new TextBlock
            {
                Text = message ?? (installed ? $"Instalado · {state!.Version}" : "No instalado"),
                FontSize = 11, Foreground = error ? ErrorBrush : DimBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(text, 1);
            row.Children.Add(icon);
            row.Children.Add(text);
            if (state is { Installed: false })
            {
                var button = new Button { Style = (Style)FindResource("PillButton"), Content = "Instalar", Padding = new Thickness(16, 5, 16, 5), VerticalAlignment = VerticalAlignment.Center };
                button.Click += async (_, _) => await InstallCliAsync(cli);
                Grid.SetColumn(button, 2);
                row.Children.Add(button);
            }
            return row;
        }

        private async Task InstallCliAsync(ProviderKind cli)
        {
            MainButton.IsEnabled = BackButton.IsEnabled = false;
            Mochi.State = MochiState.Working;
            ReplaceCliRow(cli, null, "Instalando…");
            var progress = new Progress<string>(m => ReplaceCliRow(cli, null, m));
            var error = await CliSetup.InstallAsync(cli, progress);
            var state = await Task.Run(() => CliSetup.Detect(cli));
            _cli[cli] = state;
            ReplaceCliRow(cli, state, error, error != null);
            Mochi.State = error == null ? MochiState.Done : MochiState.Error;
            BackButton.IsEnabled = true;
            RefreshNav();
        }

        // =============================================================== instalación
        /// <summary>Una fila de la etapa de instalación: icono de estado, nombre, detalle y, si hace falta, un botón.</summary>
        private sealed class StepRow
        {
            public readonly Grid Root = new() { Margin = new Thickness(0, 4, 0, 6) };
            private readonly TextBlock _icon, _detail;
            private readonly InstallerWindow _w;
            public Button? Action { get; private set; }

            public StepRow(InstallerWindow w, string name)
            {
                _w = w;
                Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                _icon = new TextBlock { FontFamily = (FontFamily)w.FindResource("IconFont"), FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = name, FontSize = 13, Foreground = TextBrush });
                _detail = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
                text.Children.Add(_detail);
                Grid.SetColumn(text, 1);
                Root.Children.Add(_icon);
                Root.Children.Add(text);
                Set(null, "Pendiente");
            }

            /// <summary>ok: true correcto, false falló, null en curso o pendiente (o solo informativo con <paramref name="info"/>).</summary>
            public void Set(bool? ok, string detail, bool info = false)
            {
                _icon.Text = ok == true ? "" : ok == false ? "" : info ? "" : "";
                _icon.Foreground = ok == true ? OkBrush : ok == false ? _w.ErrorBrush : DimBrush;
                _detail.Text = detail;
                _detail.Foreground = ok == false ? _w.ErrorBrush : DimBrush;
            }

            public Button AddAction(string label)
            {
                Action = new Button { Style = (Style)_w.FindResource("PillButton"), Content = label, Padding = new Thickness(16, 5, 16, 5), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(Action, 2);
                Root.Children.Add(Action);
                return Action;
            }
        }

        private StepRow AddStep(string name)
        {
            var row = new StepRow(this, name);
            StepRows.Children.Add(row.Root);
            return row;
        }

        private async Task InstallAsync()
        {
            _installing = true;
            foreach (var s in _stages) s.View.Visibility = Visibility.Collapsed;
            ProgressStage.Visibility = Visibility.Visible;
            StageLabel.Text = $"PASO {_stages.Count + 1} DE {_stages.Count + 1} · INSTALACIÓN";
            BackButton.Visibility = CancelButton.Visibility = Visibility.Collapsed;
            MainButton.IsEnabled = false;
            MainButton.Opacity = 0.45;
            Mochi.State = MochiState.Working;
            StepRows.Children.Clear();

            // 1. El programa (se abre al final, después de los demás pasos)
            var program = AddStep("Programa");
            program.Set(null, "Copiando y creando los accesos…");
            var options = new Installer.Options(StartupOption.IsChecked == true, DesktopOption.IsChecked == true, LaunchWhenDone: false);
            try
            {
                await RunSta(() => Installer.Install(options, _version));
                program.Set(true, $"Instalado en {Installer.InstallDir}" + (options.StartWithWindows ? " · se inicia con Windows" : ""));
            }
            catch (Exception ex)
            {
                Log.Error("Instalar", ex);
                program.Set(false, ex is InvalidOperationException ? ex.Message : $"No se pudo instalar: {ex.Message}");
                Fail("No se pudo instalar. Revisa el motivo y vuelve a intentarlo.");
                return;
            }

            // 2. Avisos de la terminal de cada CLI instalado (apuntan al programa instalado)
            if (HooksOption.IsChecked == true)
            {
                foreach (var cli in CliSetup.Clis)
                {
                    var row = AddStep($"Avisos de {ProviderFactory.Label(cli)}");
                    var (result, detail) = await Task.Run(() => CliSetup.ConnectHooks(cli, Installer.InstalledExe));
                    if (result is CliSetup.HookResult.Busy or CliSetup.HookResult.NotInstalled) row.Set(null, detail, info: true);
                    else row.Set(result != CliSetup.HookResult.Failed, detail);
                }
            }

            // 3. Los pasos extra (ver ConfigureStages)
            foreach (var step in _afterInstall)
                if (!await step()) { Fail("La instalación quedó a medias: completa el paso marcado y vuelve a abrir el instalador."); return; }

            _installing = false;
            _done = true;
            Mochi.State = MochiState.Done;
            ShowStatus("¡Listo! Lo encontrarás en el menú Inicio" + (options.StartWithWindows ? " y se abrirá solo al iniciar Windows." : "."));
            MainButton.Content = LaunchOption.IsChecked == true ? "Abrir Agent Manager Notch" : "Cerrar";
            MainButton.IsEnabled = true;
            MainButton.Opacity = 1;
        }

        private void Fail(string message)
        {
            _installing = false;
            Mochi.State = MochiState.Error;
            ShowStatus(message, error: true);
            CancelButton.Visibility = Visibility.Visible;
            CancelButton.Content = "Cerrar";
        }

        private void Finish()
        {
            if (!_uninstall && LaunchOption.IsChecked == true)
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Installer.InstalledExe) { UseShellExecute = true, WorkingDirectory = Installer.InstallDir }); }
                catch (Exception ex) { Log.Error("Abrir tras instalar", ex); }
            Close();
        }

        // =============================================================== desinstalar
        private async Task UninstallAsync()
        {
            MainButton.IsEnabled = CancelButton.IsEnabled = UninstallOptions.IsEnabled = false;
            Mochi.State = MochiState.Working;
            ShowStatus("Desinstalando…");
            bool removeData = RemoveDataOption.IsChecked == true;
            try
            {
                await RunSta(() => Installer.Uninstall(removeData));
                _done = true;
                Mochi.State = MochiState.Done;
                ShowStatus("Listo: Agent Manager Notch se quitó del equipo." + (removeData ? "" : " Tus datos siguen en su carpeta."));
                MainButton.Content = "Cerrar";
                MainButton.IsEnabled = true;
                CancelButton.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Log.Error("Desinstalar", ex);
                Mochi.State = MochiState.Error;
                ShowStatus($"No se pudo desinstalar: {ex.Message}", error: true);
                MainButton.IsEnabled = CancelButton.IsEnabled = UninstallOptions.IsEnabled = true;
            }
        }

        // =============================================================== utilidades
        private void ShowStatus(string text, bool error = false)
        {
            Status.Text = text;
            Status.Foreground = error ? ErrorBrush : (Brush)FindResource("TextPrimary");
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

        private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
    }
}
