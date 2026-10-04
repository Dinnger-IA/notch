using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AgentManagerNotch.Models;
using AgentManagerNotch.Providers;

namespace AgentManagerNotch.Services
{
    /// <summary>
    /// CLI que usa el notch (Claude Code y Codex) para el instalador: si están instalados y su versión, instalarlos con
    /// su método oficial y conectar sus avisos de la terminal con el notch.
    /// <list type="bullet">
    /// <item>Claude Code: instalador oficial de Windows (<c>irm https://claude.ai/install.ps1 | iex</c>), sin Node ni
    /// administrador. Avisos: hooks en ~/.claude/settings.json (<see cref="ControlChannel.SetClaudeIntegration"/>).</item>
    /// <item>Codex: <c>npm install -g @openai/codex</c> (si no hay Node, antes Node LTS con winget). Avisos: la opción
    /// <c>notify</c> de ~/.codex/config.toml. Codex admite uno solo: si ya lo usa otra app (p. ej. la app de Codex), se
    /// encadena: el notch lo toma y reenvía cada aviso al programa anterior (<see cref="CodexNotify"/>).</item>
    /// </list>
    /// </summary>
    public static class CliSetup
    {
        public static readonly ProviderKind[] Clis = { ProviderKind.ClaudeCode, ProviderKind.Codex };
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);

        public record CliState(ProviderKind Cli, bool Installed, string? Version, string? Path);

        public static CliState Detect(ProviderKind cli)
        {
            RefreshPath();
            var path = CliResolver.Find(ProviderFactory.Executable(cli));
            if (path == null) return new CliState(cli, false, null, null);
            var (_, output) = Run(path, "--version", TimeSpan.FromSeconds(20));
            var version = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Any(char.IsDigit));
            return new CliState(cli, true, version, path);
        }

        /// <summary>Instala el CLI. Devuelve null si quedó instalado, o el motivo del fallo para mostrarlo.</summary>
        public static async Task<string?> InstallAsync(ProviderKind cli, IProgress<string>? progress = null)
        {
            return await Task.Run(() =>
            {
                if (cli == ProviderKind.ClaudeCode)
                {
                    progress?.Report("Descargando el instalador oficial de Claude Code…");
                    var (code, output) = Run("powershell.exe",
                        "-NoProfile -ExecutionPolicy Bypass -Command \"irm https://claude.ai/install.ps1 | iex\"", InstallTimeout);
                    Log.Info($"Instalar Claude Code: código {code} · {Tail(output)}");
                    if (Detect(cli).Installed) return null;
                    return code == 0 ? "El instalador terminó pero no se encuentra «claude». Abre una terminal nueva y prueba «claude --version»."
                                     : $"El instalador de Claude Code falló: {Tail(output)}";
                }
                if (cli == ProviderKind.Codex)
                {
                    RefreshPath();
                    if (CliResolver.Find("npm") == null)
                    {
                        progress?.Report("Codex necesita Node.js: instalando Node LTS con winget…");
                        if (CliResolver.Find("winget") == null)
                            return "Falta Node.js y no hay winget para instalarlo. Instala Node LTS desde nodejs.org y vuelve a intentarlo.";
                        var (wc, wo) = Run("winget", "install --id OpenJS.NodeJS.LTS -e --silent --accept-package-agreements --accept-source-agreements", InstallTimeout);
                        Log.Info($"Instalar Node LTS: código {wc} · {Tail(wo)}");
                        RefreshPath();
                        if (CliResolver.Find("npm") == null) return $"No se pudo instalar Node.js con winget: {Tail(wo)}";
                    }
                    progress?.Report("Instalando Codex con npm…");
                    var npm = CliResolver.Find("npm")!;
                    var (code, output) = Run(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", $"/d /c \"\"{npm}\" install -g @openai/codex\"", InstallTimeout);
                    Log.Info($"Instalar Codex: código {code} · {Tail(output)}");
                    if (Detect(cli).Installed) return null;
                    return $"npm no pudo instalar Codex: {Tail(output)}";
                }
                return "Este CLI no se instala desde aquí.";
            });
        }

        // =============================================================== avisos de la terminal
        public enum HookResult { Connected, AlreadyConnected, Busy, NotInstalled, Failed }

        /// <summary>Conecta los avisos de la terminal del CLI con <paramref name="exe"/> (el notch instalado).</summary>
        public static (HookResult Result, string Detail) ConnectHooks(ProviderKind cli, string exe)
        {
            try
            {
                if (!Detect(cli).Installed) return (HookResult.NotInstalled, "No está instalado.");
                if (cli == ProviderKind.ClaudeCode)
                {
                    ControlChannel.SetClaudeIntegration(true, exe);
                    return (HookResult.Connected, "Avisos y sesiones de la terminal conectados (hooks en ~/.claude/settings.json).");
                }
                return CodexNotify.Install(exe);
            }
            catch (Exception ex)
            {
                Log.Error($"Conectar avisos de {ProviderFactory.Label(cli)}", ex);
                return (HookResult.Failed, ex.Message);
            }
        }

        // =============================================================== utilidades
        /// <summary>
        /// Recarga el PATH del registro (máquina + usuario): un CLI recién instalado lo modifica, pero este proceso
        /// conserva el de cuando se abrió.
        /// </summary>
        public static void RefreshPath()
        {
            var machine = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
            var user = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
            var current = Environment.GetEnvironmentVariable("PATH") ?? "";
            var all = (machine + ";" + user + ";" + current).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Environment.ExpandEnvironmentVariables).Distinct(StringComparer.OrdinalIgnoreCase);
            Environment.SetEnvironmentVariable("PATH", string.Join(";", all));
        }

        private static (int Code, string Output) Run(string file, string args, TimeSpan timeout)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            try
            {
                using var p = Process.Start(psi)!;
                var o = p.StandardOutput.ReadToEndAsync();
                var e = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit((int)timeout.TotalMilliseconds)) { try { p.Kill(true); } catch { } return (-1, "tiempo agotado"); }
                return (p.ExitCode, o.Result + e.Result);
            }
            catch (Exception ex) { return (-1, ex.Message); }
        }

        private static string Tail(string s)
        {
            s = string.Join(" ", s.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
            return s.Length > 300 ? "…" + s[^300..] : s;
        }
    }

    /// <summary>
    /// La opción <c>notify</c> de Codex (~/.codex/config.toml): Codex ejecuta ese programa al terminar cada turno con un
    /// JSON como último argumento. Solo admite uno, así que si ya lo usa otra aplicación (p. ej. la app de Codex) no se
    /// toca.
    /// </summary>
    /// <summary>
    /// La opción <c>notify</c> de ~/.codex/config.toml apuntando al notch. Si otra app ya la usaba, su valor se guarda
    /// en <see cref="PreviousPath"/> y el notch le reenvía cada aviso (<see cref="ForwardToPrevious"/>); al quitarlo se
    /// restaura.
    /// </summary>
    public static class CodexNotify
    {
        public const string Arg = "--codex-event";
        private static string ConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
        /// <summary>Línea <c>notify = …</c> que había antes del notch (otra app), para reenviarle los avisos.</summary>
        private static string PreviousPath => Path.Combine(ConfigStore.Root, "codex-notify-anterior.txt");

        /// <summary>
        /// Si Codex guardó esa sesión (~/.codex/sessions o archived_sessions). Las tareas internas de Codex, como la que
        /// genera el título de la conversación, también avisan por notify pero no se guardan y no se pueden continuar.
        /// </summary>
        public static bool HasRollout(string threadId)
        {
            if (string.IsNullOrWhiteSpace(threadId) || threadId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            var home = Path.GetDirectoryName(ConfigPath)!;
            foreach (var dir in new[] { "sessions", "archived_sessions" })
            {
                var root = Path.Combine(home, dir);
                try
                {
                    if (Directory.Exists(root) && Directory.EnumerateFiles(root, $"rollout-*{threadId}.jsonl", SearchOption.AllDirectories).Any())
                        return true;
                }
                catch (Exception ex) { Log.Error("Codex: buscar la sesión guardada", ex); return true; }
            }
            return false;
        }

        private static bool IsNotifyLine(string l) => l.TrimStart().StartsWith("notify", StringComparison.Ordinal) && l.Contains('=');

        public static (CliSetup.HookResult, string) Install(string exe)
        {
            var path = ConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new();
            int firstTable = lines.FindIndex(l => l.TrimStart().StartsWith('['));
            int top = firstTable < 0 ? lines.Count : firstTable;
            int notify = lines.FindIndex(0, top, IsNotifyLine);
            // Cadena literal de TOML (comillas simples): sin escapar las barras de la ruta
            var line = $"notify = ['{exe}', '{Arg}']";
            string chained = "";
            if (notify >= 0)
            {
                if (lines[notify] == line) return (CliSetup.HookResult.AlreadyConnected, "Avisos de la terminal ya conectados.");
                if (!lines[notify].Contains(Arg))
                {
                    // Otra app usa notify: se guarda para reenviarle los avisos
                    Directory.CreateDirectory(ConfigStore.Root);
                    File.WriteAllText(PreviousPath, lines[notify].Trim());
                    chained = " Se siguen reenviando a la app que ya los usaba.";
                    Log.Info("Codex: notify encadenado con el anterior");
                }
                lines[notify] = line;
            }
            else lines.Insert(0, line);
            if (File.Exists(path)) File.Copy(path, path + ".agent-manager-notch-backup", overwrite: true);
            File.WriteAllLines(path, lines);
            return (CliSetup.HookResult.Connected, "Avisos de la terminal conectados (notify en ~/.codex/config.toml)." + chained);
        }

        /// <summary>Quita el notify del notch (si es el suyo) y deja el que había antes, si lo había.</summary>
        public static void Uninstall()
        {
            var path = ConfigPath;
            if (!File.Exists(path)) return;
            var lines = File.ReadAllLines(path).ToList();
            int i = lines.FindIndex(l => IsNotifyLine(l) && l.Contains(Arg));
            if (i < 0) return;
            var previous = File.Exists(PreviousPath) ? File.ReadAllText(PreviousPath).Trim() : "";
            if (previous.Length > 0) lines[i] = previous; else lines.RemoveAt(i);
            File.WriteAllLines(path, lines);
            try { File.Delete(PreviousPath); } catch { }
        }

        public static bool IsInstalled() => File.Exists(ConfigPath) && File.ReadAllText(ConfigPath).Contains(Arg);

        /// <summary>Pasa el aviso al notify anterior (mismo comando con el JSON como último argumento), sin esperarlo.</summary>
        public static void ForwardToPrevious(string json)
        {
            try
            {
                if (!File.Exists(PreviousPath)) return;
                var cmd = ParseArray(File.ReadAllText(PreviousPath));
                if (cmd.Count == 0 || cmd[0].Contains(Arg)) return;
                var psi = new ProcessStartInfo(cmd[0]) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in cmd.Skip(1)) psi.ArgumentList.Add(a);
                psi.ArgumentList.Add(json);
                Process.Start(psi)?.Dispose();
            }
            catch (Exception ex) { Log.Error("Codex: reenviar el aviso al notify anterior", ex); }
        }

        /// <summary>Cadenas del array TOML de <c>notify = [...]</c> (básicas con escapes o literales con comillas simples).</summary>
        private static System.Collections.Generic.List<string> ParseArray(string line)
        {
            var result = new System.Collections.Generic.List<string>();
            int i = line.IndexOf('[');
            if (i < 0) return result;
            while (++i < line.Length)
            {
                char q = line[i];
                if (q == ']') break;
                if (q != '"' && q != '\'') continue;
                var sb = new StringBuilder();
                while (++i < line.Length && line[i] != q)
                {
                    if (q == '"' && line[i] == '\\' && i + 1 < line.Length)
                    {
                        char e = line[++i];
                        sb.Append(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => e });
                    }
                    else sb.Append(line[i]);
                }
                result.Add(sb.ToString());
            }
            return result;
        }
    }
}
