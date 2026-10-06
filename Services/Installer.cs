using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace AgentManagerNotch.Services
{
    /// <summary>
    /// Instalación por usuario (sin permisos de administrador). El instalador es el propio ejecutable autónomo
    /// (<c>AgentManagerNotch-Setup-X.Y.Z.exe</c>, ver <c>crear-instalador.ps1</c>): se copia a
    /// <c>%LOCALAPPDATA%\Programs\Agent Manager Notch</c>, crea el acceso del menú Inicio (y, si se pide, el del
    /// escritorio), se registra en «Aplicaciones instaladas» y, opcionalmente, en el inicio de Windows. El mismo
    /// ejecutable instalado se desinstala con <c>--desinstalar</c>. Los datos del usuario (%APPDATA%) no se tocan
    /// salvo que se pida al desinstalar.
    /// </summary>
    public static class Installer
    {
        public const string AppName = "Agent Manager Notch";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentManagerNotch";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "AgentManagerNotch";

        public static string InstallDir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
        public static string InstalledExe { get; } = Path.Combine(InstallDir, "AgentManagerNotch.exe");
        private static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
        private static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

        /// <summary>Se abrió como instalador: con <c>--instalar</c> o con el nombre del archivo de instalación.</summary>
        public static bool IsSetupLaunch(string[] args) =>
            args.FirstOrDefault() == "--instalar" ||
            Path.GetFileName(Environment.ProcessPath ?? "").StartsWith("AgentManagerNotch-Setup", StringComparison.OrdinalIgnoreCase);

        public static bool IsInstalled => File.Exists(InstalledExe);

        /// <summary>Versión instalada (la del registro de «Aplicaciones instaladas»), si la hay.</summary>
        public static string? InstalledVersion
        {
            get
            {
                using var k = Registry.CurrentUser.OpenSubKey(UninstallKey);
                return IsInstalled ? k?.GetValue("DisplayVersion") as string : null;
            }
        }

        /// <summary>¿El inicio con Windows ya abre la copia instalada? (para proponerlo marcado al reinstalar)</summary>
        public static bool StartupPointsToInstalled()
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunValue) is string v && v.Contains(InstalledExe, StringComparison.OrdinalIgnoreCase);
        }

        public record Options(bool StartWithWindows, bool DesktopShortcut, bool LaunchWhenDone);

        /// <summary>Versión de este ejecutable (la mayor de versiones\ al compilar).</summary>
        public static string Version => (typeof(Installer).Assembly.GetName().Version ?? new Version(0, 0, 0)).ToString(3);

        /// <summary>Instalar o desinstalar sin ventana. Devuelve 0 si fue bien.</summary>
        public static int RunSilent(bool uninstall, string[] args)
        {
            try
            {
                if (uninstall) Uninstall(removeData: args.Contains("--borrar-datos"));
                else
                {
                    // Como en la ventana: CLI que falten (con --instalar-cli) y avisos de la terminal (salvo --sin-avisos)
                    if (args.Contains("--instalar-cli"))
                        foreach (var cli in CliSetup.Clis.Where(c => !CliSetup.Detect(c).Installed))
                            Log.Info($"Instalar {cli} (silencioso): " + (CliSetup.InstallAsync(cli).GetAwaiter().GetResult() ?? "instalado"));
                    Install(new Options(!args.Contains("--sin-inicio-windows"), args.Contains("--escritorio"), LaunchWhenDone: false), Version);
                    if (!args.Contains("--sin-avisos"))
                        foreach (var cli in CliSetup.Clis)
                            Log.Info($"Avisos de {cli} (silencioso): {CliSetup.ConnectHooks(cli, InstalledExe).Detail}");
                    if (args.Contains("--abrir")) Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true, WorkingDirectory = InstallDir });
                }
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error(uninstall ? "Desinstalar (silencioso)" : "Instalar (silencioso)", ex);
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        /// <summary>Instala (o actualiza) la copia de este ejecutable. Lanza excepción con un mensaje para el usuario si falla.</summary>
        public static void Install(Options o, string version)
        {
            var self = Environment.ProcessPath ?? throw new InvalidOperationException("No se encontró el ejecutable del instalador.");
            StopRunningCopies();
            Directory.CreateDirectory(InstallDir);
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
                Retry(() => File.Copy(self, InstalledExe, overwrite: true),
                    "No se pudo copiar el programa. Cierra Agent Manager Notch (icono de la bandeja → Salir) y vuelve a intentarlo.");

            CreateShortcut(StartMenuLink);
            if (o.DesktopShortcut) CreateShortcut(DesktopLink);
            else TryDelete(DesktopLink);

            using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (o.StartWithWindows) run.SetValue(RunValue, $"\"{InstalledExe}\"");
                else if (run.GetValue(RunValue) != null) run.DeleteValue(RunValue, false);
            }

            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", version);
                k.SetValue("Publisher", "Agent Manager Notch");
                k.SetValue("DisplayIcon", InstalledExe);
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", $"\"{InstalledExe}\" --desinstalar");
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            Log.Info($"Instalado {version} en {InstallDir} (inicio con Windows: {o.StartWithWindows}, escritorio: {o.DesktopShortcut})");

            if (o.LaunchWhenDone) Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true, WorkingDirectory = InstallDir });
        }

        /// <summary>
        /// Quita accesos, registro e inicio con Windows y programa el borrado de la carpeta para cuando este proceso
        /// termine (el ejecutable en uso no se puede borrar). Con <paramref name="removeData"/> también borra los datos.
        /// </summary>
        public static void Uninstall(bool removeData)
        {
            StopRunningCopies();
            TryDelete(StartMenuLink);
            TryDelete(DesktopLink);
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
                if (run?.GetValue(RunValue) is string v && v.Contains(InstallDir, StringComparison.OrdinalIgnoreCase)) run.DeleteValue(RunValue, false);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
            // Los avisos de Claude Code y Codex apuntan a este ejecutable: sin él fallarían
            try
            {
                if (ControlChannel.IsClaudeIntegrationInstalled() && IsSelfInstalled()) ControlChannel.SetClaudeIntegration(false);
                if (CodexNotify.IsInstalled() && IsSelfInstalled()) CodexNotify.Uninstall();
            }
            catch (Exception ex) { Log.Error("Desinstalar: avisos de Claude Code y Codex", ex); }
            Log.Info($"Desinstalado de {InstallDir} (datos borrados: {removeData})");

            var dirs = removeData ? new[] { InstallDir, ConfigStore.Root } : new[] { InstallDir };
            // Espera a que se cierre este proceso y borra; reintenta hasta un minuto porque el antivirus o el Explorador
            // pueden tener abierto el ejecutable un rato. Con Wait-Process y no con el truco de «ping 127.0.0.1» como
            // pausa, que es la firma clásica del malware que se borra a sí mismo y la detectan los antivirus heurísticos
            var paths = string.Join(",", dirs.Select(d => $"'{d.Replace("'", "''")}'"));
            var ps = $"Wait-Process -Id {Environment.ProcessId} -Timeout 60 -ErrorAction SilentlyContinue; " +
                     $"foreach ($i in 1..60) {{ $left = @({paths}) | Where-Object {{ Test-Path -LiteralPath $_ }}; if (-not $left) {{ break }}; " +
                     "$left | ForEach-Object { Remove-Item -LiteralPath $_ -Recurse -Force -ErrorAction SilentlyContinue }; Start-Sleep -Seconds 1 }";
            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", ps },
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath()
            });
        }

        private static bool IsSelfInstalled() =>
            string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

        /// <summary>Cierra los notch que estén abiertos (cualquier copia), menos este proceso.</summary>
        private static void StopRunningCopies()
        {
            foreach (var p in Process.GetProcessesByName("AgentManagerNotch").Where(p => p.Id != Environment.ProcessId))
            {
                try { p.Kill(true); p.WaitForExit(5000); } catch { }
                finally { p.Dispose(); }
            }
        }

        private static void CreateShortcut(string path)
        {
            // Acceso directo con el objeto COM del shell de Windows (sin dependencias)
            var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("No se pudo crear el acceso directo.");
            dynamic shell = Activator.CreateInstance(type)!;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                dynamic link = shell.CreateShortcut(path);
                link.TargetPath = InstalledExe;
                link.WorkingDirectory = InstallDir;
                link.IconLocation = InstalledExe + ",0";
                link.Description = "Tus agentes de IA en el notch";
                link.Save();
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }

        private static void Retry(Action action, string message)
        {
            for (int i = 0; ; i++)
            {
                try { action(); return; }
                catch (IOException) when (i < 10) { Thread.Sleep(500); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new InvalidOperationException(message, ex); }
            }
        }

        private static void TryDelete(string file) { try { if (File.Exists(file)) File.Delete(file); } catch { } }
    }
}
