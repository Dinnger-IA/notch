using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AgentManagerNotch.Services
{
    /// <summary>
    /// Busca versiones nuevas cada 2 horas: compara la versión con la que se compiló (la mayor de la carpeta
    /// <c>versiones\</c>) con la mayor de esa carpeta en su rama del repositorio (<c>git fetch</c> en la carpeta del
    /// repositorio, o un clon mínimo temporal si esta copia no está dentro de uno). Solo avisa si la del repositorio
    /// es mayor, así que un commit sin versión nueva no pide actualizar. Para actualizar: git pull --ff-only +
    /// publish.ps1 en la carpeta del repositorio, con un script aparte que espera a que el notch se cierre y abre
    /// la copia recién publicada.
    /// </summary>
    public sealed class UpdateService
    {
        public const string RepoUrl = "https://github.com/Dinnger-IA/agent-manager-notch";
        private static readonly TimeSpan Interval = TimeSpan.FromHours(2);

        public string LocalCommit { get; } = Metadata("GitCommit");
        public string Branch { get; } = Metadata("GitBranch");
        public Version LocalVersion { get; } = Normalize(typeof(UpdateService).Assembly.GetName().Version);
        public string Version => LocalVersion.ToString(3);
        /// <summary>Mayor versión publicada en la rama del repositorio (null hasta la primera comprobación).</summary>
        public Version? RemoteVersion { get; private set; }
        /// <summary>Novedades de <see cref="RemoteVersion"/> (las líneas «- …» de su archivo).</summary>
        public string[] RemoteNotes { get; private set; } = [];
        public DateTime? LastCheck { get; private set; }
        public string? LastError { get; private set; }
        public bool Checking { get; private set; }
        public bool UpdateAvailable => RemoteVersion != null && RemoteVersion > LocalVersion;
        public string RemoteVersionText => RemoteVersion?.ToString(3) ?? "?";

        /// <summary>Carpeta del repositorio (la publicación vive en &lt;repo&gt;\publish), o null si no se puede actualizar sola.</summary>
        public string? RepoDir { get; }

        /// <summary>Cambió el estado (comprobación terminada). En el hilo de la interfaz.</summary>
        public event Action? Changed;
        /// <summary>Se encontró una versión nueva (una vez por versión).</summary>
        public event Action? Found;

        private readonly DispatcherTimer _timer = new() { Interval = Interval };
        private Version? _notifiedFor;

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
            if (AppIdentity.IsPackaged) { Log.Info("Actualizaciones: edición de Microsoft Store, las hace la Store"); return; }
            if (Branch is "" or "HEAD") { Log.Info("Actualizaciones: compilación sin información de git, no se comprueba"); return; }
            _timer.Start();
            // Primera comprobación al poco de arrancar, sin estorbar el inicio
            var first = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            first.Tick += async (_, _) => { first.Stop(); await CheckAsync(); };
            first.Start();
        }

        public async Task CheckAsync()
        {
            if (Checking || Branch is "" or "HEAD" || AppIdentity.IsPackaged) return;
            Checking = true;
            Changed?.Invoke();
            try
            {
                var (version, notes, error) = await Task.Run(ReadRemote);
                LastError = error;
                if (error != null) Log.Info($"Actualizaciones: {error}");
                else
                {
                    RemoteVersion = version;
                    RemoteNotes = notes;
                    Log.Info($"Actualizaciones: local {Version} ({Short(LocalCommit)}) · remoto {RemoteVersionText} ({Branch})");
                }
                LastCheck = DateTime.Now;
            }
            finally
            {
                Checking = false;
                Changed?.Invoke();
                if (UpdateAvailable && _notifiedFor != RemoteVersion) { _notifiedFor = RemoteVersion; Found?.Invoke(); }
            }
        }

        /// <summary>Lee la mayor versión de <c>versiones\</c> en la rama del repositorio y sus novedades.</summary>
        private (Version?, string[], string?) ReadRemote()
        {
            string? temp = null;
            try
            {
                string dir, rev;
                if (RepoDir != null)
                {
                    var (fc, fo) = Git(RepoDir, "fetch", "--quiet", "origin", Branch);
                    if (fc != 0) return (null, [], $"No se pudo consultar el repositorio (¿sin conexión o sin acceso?). {Tail(fo)}");
                    (dir, rev) = (RepoDir, $"origin/{Branch}");
                }
                else
                {
                    // Copia suelta: clon mínimo (sin archivos ni historia) solo para leer la carpeta de versiones
                    temp = Path.Combine(Path.GetTempPath(), $"agent-manager-notch-check-{Guid.NewGuid():N}");
                    var (cc, co) = Git(null, "clone", "--quiet", "--depth", "1", "--filter=blob:none", "--no-checkout", "--branch", Branch, RepoUrl, temp);
                    if (cc != 0) return (null, [], $"No se pudo consultar el repositorio (¿sin conexión o sin acceso?). {Tail(co)}");
                    (dir, rev) = (temp, "HEAD");
                }
                var (lc, list) = Git(dir, "ls-tree", "--name-only", $"{rev}:versiones");
                if (lc != 0) return (null, [], $"La rama «{Branch}» del repositorio todavía no tiene carpeta de versiones.");
                var latest = list.Split('\n')
                    .Select(n => n.Trim())
                    .Where(n => n.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    .Select(n => (name: n, v: System.Version.TryParse(Path.GetFileNameWithoutExtension(n), out var v) ? Normalize(v) : null))
                    .Where(x => x.v != null)
                    .OrderByDescending(x => x.v)
                    .FirstOrDefault();
                if (latest.v == null) return (null, [], $"La rama «{Branch}» del repositorio todavía no tiene versiones.");
                var (sc, text) = Git(dir, "show", $"{rev}:versiones/{latest.name}");
                return (latest.v, sc != 0 ? [] : NoteLines(text), null);
            }
            finally
            {
                if (temp != null) try { ForceDelete(temp); } catch { }
            }
        }

        /// <summary>
        /// Comprueba que se puede actualizar sola (repositorio sin cambios locales y en la rama compilada) y lanza el
        /// script que actualiza y vuelve a abrir el notch. Devuelve null si se lanzó, o el motivo por el que no.
        /// </summary>
        public async Task<string?> StartUpdateAsync()
        {
            if (AppIdentity.IsPackaged) return "Esta copia es la de Microsoft Store: se actualiza desde la Store.";
            if (RepoDir == null) return $"Esta copia no está dentro del repositorio. Descarga la versión nueva desde {RepoUrl}.";
            var (bc, branch) = await Task.Run(() => Git(RepoDir, "rev-parse", "--abbrev-ref", "HEAD"));
            if (bc != 0) return "No se pudo leer el repositorio local.";
            if (branch.Trim() != Branch) return $"El repositorio está en la rama «{branch.Trim()}» y esta versión se compiló desde «{Branch}».";
            var (sc, status) = await Task.Run(() => Git(RepoDir, "status", "--porcelain", "--untracked-files=no"));
            if (sc != 0) return "No se pudo leer el estado del repositorio.";
            if (status.Trim() != "") return "El repositorio tiene cambios sin guardar en git; actualízalo a mano (git pull) para no perderlos.";

            // Siempre la copia recién publicada (aunque esta corra desde bin\…): si no, se volvería a abrir la antigua
            var exe = Path.Combine(RepoDir, "publish", "AgentManagerNotch.exe");
            // «Iniciar con Windows» también pasa a la copia publicada. Se hace aquí y no en el script: un PowerShell oculto
            // que escribe en la clave Run es justo lo que buscan los antivirus heurísticos
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                if (run?.GetValue("AgentManagerNotch") != null) run.SetValue("AgentManagerNotch", $"\"{exe}\"");
            }
            catch (Exception ex) { Log.Error("Actualizaciones: inicio con Windows", ex); }
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
                if (Test-Path '{{exe}}') { Start-Process '{{exe}}' } else { Start-Process '{{Environment.ProcessPath}}' }
                """, new UTF8Encoding(true));
            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script },
                UseShellExecute = false,
                CreateNoWindow = true
            });
            Log.Info($"Actualizaciones: actualizando de {Version} a {RemoteVersionText}");
            return null;
        }

        // =============================================================== utilidades
        /// <summary>Versión con tres componentes (1.2 → 1.2.0, 1.2.0.0 → 1.2.0) para compararlas sin sorpresas.</summary>
        private static Version Normalize(Version? v) => v == null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(v.Build, 0));

        /// <summary>Las novedades («- …») de un archivo de versión, como texto plano para la tarjeta.</summary>
        public static string[] NoteLines(string md) => md.Split('\n')
            .Select(l => l.Trim()).Where(l => l.StartsWith("- "))
            .Select(l => l[2..].Replace("*", "").Replace("`", "").Trim()).ToArray();

        /// <summary>Borra una carpeta aunque git haya dejado archivos de solo lectura.</summary>
        private static void ForceDelete(string dir)
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, true);
        }

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
