using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace AgentManagerNotch.Services
{
    /// <summary>
    /// Busca versiones nuevas cada 2 horas de la forma más ligera posible: compara el commit con el que se compiló
    /// (incrustado en el ensamblado) con el de su rama en el repositorio (<c>git ls-remote</c>, solo una referencia,
    /// sin descargar nada). Para actualizar: git pull --ff-only + publish.ps1 en la carpeta del repositorio, con un
    /// script aparte que espera a que el notch se cierre y lo vuelve a abrir.
    /// </summary>
    public sealed class UpdateService
    {
        public const string RepoUrl = "https://dev.azure.com/grupodistelsa/innovation-sandbox/_git/agent-manager-notch";
        private static readonly TimeSpan Interval = TimeSpan.FromHours(2);

        public string LocalCommit { get; } = Metadata("GitCommit");
        public string Branch { get; } = Metadata("GitBranch");
        public string Version { get; } = typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "";
        public string? RemoteCommit { get; private set; }
        public DateTime? LastCheck { get; private set; }
        public string? LastError { get; private set; }
        public bool Checking { get; private set; }
        public bool UpdateAvailable => RemoteCommit != null && LocalCommit != "" && !RemoteCommit.Equals(LocalCommit, StringComparison.OrdinalIgnoreCase);

        /// <summary>Carpeta del repositorio (la publicación vive en &lt;repo&gt;\publish), o null si no se puede actualizar sola.</summary>
        public string? RepoDir { get; }

        /// <summary>Cambió el estado (comprobación terminada). En el hilo de la interfaz.</summary>
        public event Action? Changed;
        /// <summary>Se encontró una versión nueva (una vez por commit).</summary>
        public event Action? Found;

        private readonly DispatcherTimer _timer = new() { Interval = Interval };
        private string? _notifiedFor;

        public UpdateService()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('\\', '/'));
            for (var d = dir; d != null; d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, ".git")) && File.Exists(Path.Combine(d.FullName, "publish.ps1"))) { RepoDir = d.FullName; break; }
            _timer.Tick += async (_, _) => await CheckAsync();
        }

        public static string Short(string? commit) => string.IsNullOrEmpty(commit) ? "?" : commit[..Math.Min(7, commit.Length)];

        public void Start()
        {
            if (LocalCommit == "" || Branch is "" or "HEAD") { Log.Info("Actualizaciones: compilación sin información de git, no se comprueba"); return; }
            _timer.Start();
            // Primera comprobación al poco de arrancar, sin estorbar el inicio
            var first = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            first.Tick += async (_, _) => { first.Stop(); await CheckAsync(); };
            first.Start();
        }

        public async Task CheckAsync()
        {
            if (Checking || LocalCommit == "") return;
            Checking = true;
            Changed?.Invoke();
            try
            {
                var (code, output) = await Task.Run(() => Git(null, "ls-remote", RepoUrl, $"refs/heads/{Branch}"));
                var hash = output.Split('\n').Select(l => l.Split('\t')[0].Trim()).FirstOrDefault(h => h.Length >= 40);
                if (code != 0 || hash == null)
                {
                    LastError = code != 0 ? "No se pudo consultar el repositorio (¿sin conexión o sin acceso?)." : $"La rama «{Branch}» no existe en el repositorio.";
                    Log.Info($"Actualizaciones: {LastError} {Tail(output)}");
                }
                else
                {
                    LastError = null;
                    RemoteCommit = hash;
                    Log.Info($"Actualizaciones: local {Short(LocalCommit)} · remoto {Short(hash)} ({Branch})");
                }
                LastCheck = DateTime.Now;
            }
            finally
            {
                Checking = false;
                Changed?.Invoke();
                if (UpdateAvailable && _notifiedFor != RemoteCommit) { _notifiedFor = RemoteCommit; Found?.Invoke(); }
            }
        }

        /// <summary>
        /// Comprueba que se puede actualizar sola (repositorio sin cambios locales y en la rama compilada) y lanza el
        /// script que actualiza y vuelve a abrir el notch. Devuelve null si se lanzó, o el motivo por el que no.
        /// </summary>
        public async Task<string?> StartUpdateAsync()
        {
            if (RepoDir == null) return $"Esta copia no está dentro del repositorio. Descarga la versión nueva desde {RepoUrl}.";
            var (bc, branch) = await Task.Run(() => Git(RepoDir, "rev-parse", "--abbrev-ref", "HEAD"));
            if (bc != 0) return "No se pudo leer el repositorio local.";
            if (branch.Trim() != Branch) return $"El repositorio está en la rama «{branch.Trim()}» y esta versión se compiló desde «{Branch}».";
            var (sc, status) = await Task.Run(() => Git(RepoDir, "status", "--porcelain", "--untracked-files=no"));
            if (sc != 0) return "No se pudo leer el estado del repositorio.";
            if (status.Trim() != "") return "El repositorio tiene cambios sin guardar en git; actualízalo a mano (git pull) para no perderlos.";

            var exe = Environment.ProcessPath ?? Path.Combine(RepoDir, "publish", "AgentManagerNotch.exe");
            var log = Path.Combine(ConfigStore.Root, "update.log");
            var script = Path.Combine(Path.GetTempPath(), "agent-manager-notch-update.ps1");
            File.WriteAllText(script, $$"""
                $ErrorActionPreference = 'Continue'
                Start-Transcript -Path '{{log}}' -Force | Out-Null
                try { Wait-Process -Id {{Environment.ProcessId}} -Timeout 30 -ErrorAction SilentlyContinue } catch {}
                Set-Location '{{RepoDir}}'
                $env:GIT_TERMINAL_PROMPT = '0'
                git pull --ff-only
                if ($LASTEXITCODE -eq 0) { & '.\publish.ps1' } else { Write-Host 'git pull falló: se abre la versión actual' }
                Stop-Transcript | Out-Null
                Start-Process '{{exe}}'
                """, new UTF8Encoding(true));
            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script },
                UseShellExecute = false,
                CreateNoWindow = true
            });
            Log.Info($"Actualizaciones: actualizando de {Short(LocalCommit)} a {Short(RemoteCommit)}");
            return null;
        }

        // =============================================================== utilidades
        private static string Metadata(string key) =>
            typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value?.Trim() ?? "";

        private static (int, string) Git(string? workDir, params string[] args)
        {
            var git = CliResolver.Find("git") ?? "git";
            var psi = new ProcessStartInfo(git)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = workDir ?? Path.GetTempPath()
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0"; // nunca se queda esperando credenciales
            try
            {
                using var p = Process.Start(psi)!;
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(30000)) { try { p.Kill(true); } catch { } return (-1, "tiempo agotado"); }
                return (p.ExitCode, outTask.Result + errTask.Result);
            }
            catch (Exception ex) { return (-1, ex.Message); }
        }

        private static string Tail(string s) { s = s.Trim(); return s.Length > 200 ? s[^200..] : s; }
    }
}
