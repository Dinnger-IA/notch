using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentManagerNotch.Models;
using AgentManagerNotch.Providers;
using AgentManagerNotch.Services;

namespace AgentManagerNotch.Plugins.AgentManager
{
    /// <summary>
    /// Segundo paso tras validar la sesión: revisa los CLI instalados (Claude Code, Codex, Gemini) y, si alguno no
    /// tiene el MCP de Agent Manager, lo instala con el comando oficial del CLI usando la URL con el token.
    /// La detección lee la configuración de usuario de cada CLI (rápido y sin conectar a ningún servidor).
    /// </summary>
    public static class McpSetup
    {
        public const string ServerName = "agent-manager";
        private const string McpEndpoint = AuthService.BaseUrl + "/mcp";
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);
        private static readonly SemaphoreSlim Gate = new(1, 1);

        public enum Status { NotInstalled, AlreadyConfigured, Installed, Updated, Failed }
        public record Result(ProviderKind Cli, Status Status, string? Error = null);

        private static readonly ProviderKind[] Clis = { ProviderKind.ClaudeCode, ProviderKind.Codex, ProviderKind.Gemini };

        /// <summary>
        /// Revisa e instala donde falte; si la entrada usa la URL con un token distinto al de la sesión (p. ej. tras
        /// volver a iniciar sesión, que revoca el anterior), la reemplaza. Si ya hay una revisión en curso, no hace nada.
        /// </summary>
        public static async Task<List<Result>> EnsureAsync(string token)
        {
            var results = new List<Result>();
            if (!await Gate.WaitAsync(0)) return results;
            try
            {
                var url = $"{McpEndpoint}?t={Uri.EscapeDataString(token)}";
                foreach (var cli in Clis)
                {
                    var r = await Task.Run(() => Ensure(cli, url, token));
                    Log.Info($"MCP {ServerName} · {ProviderFactory.Label(cli)}: {r.Status}{(r.Error != null ? " · " + r.Error : "")}");
                    results.Add(r);
                }
            }
            catch (Exception ex) { Log.Error("Error revisando el MCP de Agent Manager en los CLI", ex); }
            finally { Gate.Release(); }
            return results;
        }

        private static async Task<Result> Ensure(ProviderKind cli, string url, string token)
        {
            var exe = ProviderFactory.Executable(cli);
            if (CliResolver.Find(exe) == null) return new Result(cli, Status.NotInstalled);
            try
            {
                var entry = FindEntry(cli);
                bool stale = entry != null && TokenOf(entry.Url) is string old && old != token;
                // Entradas sin ?t= (OAuth u otra configuración del usuario) no se tocan
                if (entry != null && !stale) return new Result(cli, Status.AlreadyConfigured);

                if (stale)
                {
                    var remove = cli switch
                    {
                        ProviderKind.ClaudeCode => new[] { "mcp", "remove", entry!.Name, "--scope", "user" },
                        ProviderKind.Codex => new[] { "mcp", "remove", entry!.Name },
                        _ => new[] { "mcp", "remove", "--scope", "user", entry!.Name }
                    };
                    var (rc, rout) = await RunAsync(exe, remove, () => SafeFind(cli) == null);
                    if (FindEntry(cli) != null)
                        return new Result(cli, Status.Failed, $"no se pudo quitar la entrada anterior (código {rc}): {Short(Mask(rout, token))}");
                }

                var add = cli switch
                {
                    ProviderKind.ClaudeCode => new[] { "mcp", "add", "--transport", "http", "--scope", "user", ServerName, url },
                    ProviderKind.Codex => new[] { "mcp", "add", ServerName, "--url", url },
                    _ => new[] { "mcp", "add", "--scope", "user", "--transport", "http", ServerName, url }
                };
                var (code, output) = await RunAsync(exe, add, () => TokenOf(SafeFind(cli)?.Url) == token);
                if (TokenOf(FindEntry(cli)?.Url) == token) return new Result(cli, stale ? Status.Updated : Status.Installed);
                return new Result(cli, Status.Failed, code == 0 ? "no aparece en la configuración tras instalarlo" : $"código {code}: {Short(Mask(output, token))}");
            }
            catch (Exception ex) { return new Result(cli, Status.Failed, ex.Message); }
        }

        /// <summary>Nunca se registra el token.</summary>
        private static string Mask(string text, string token) =>
            text.Replace(token, "***").Replace(Uri.EscapeDataString(token), "***").Trim();

        // =============================================================== detección
        private record Entry(string Name, string? Url);

        private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        /// <summary>Token de una URL del MCP de Agent Manager (…/mcp?t=…), o null si no es esa URL o no lleva token.</summary>
        private static string? TokenOf(string? url)
        {
            if (url == null || !url.StartsWith(McpEndpoint + "?", StringComparison.OrdinalIgnoreCase)) return null;
            foreach (var part in url[(McpEndpoint.Length + 1)..].Split('&'))
                if (part.StartsWith("t=")) return Uri.UnescapeDataString(part[2..]);
            return null;
        }

        private static Entry? FindEntry(ProviderKind cli) => cli switch
        {
            ProviderKind.ClaudeCode => JsonFind(ClaudeConfigPath()),
            ProviderKind.Codex => TomlFind(Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } ch ? ch : Path.Combine(Home, ".codex"), "config.toml")),
            _ => JsonFind(Path.Combine(Home, ".gemini", "settings.json"))
        };

        /// <summary>Mientras el CLI escribe su configuración el archivo puede estar a medias.</summary>
        private static Entry? SafeFind(ProviderKind cli)
        {
            try { return FindEntry(cli); } catch { return null; }
        }

        /// <summary>MCP de ámbito usuario de Claude Code: ~/.claude.json (o $CLAUDE_CONFIG_DIR/.claude.json).</summary>
        private static string ClaudeConfigPath()
        {
            var dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            return Path.Combine(string.IsNullOrWhiteSpace(dir) ? Home : dir, ".claude.json");
        }

        private static bool IsAgentManager(string name, string? url) =>
            name.Equals(ServerName, StringComparison.OrdinalIgnoreCase)
            || (url != null && url.StartsWith(McpEndpoint, StringComparison.OrdinalIgnoreCase));

        /// <summary>Claude (~/.claude.json) y Gemini (settings.json): objeto "mcpServers" en la raíz.</summary>
        private static Entry? JsonFind(string path)
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty("mcpServers", out var servers) || servers.ValueKind != JsonValueKind.Object) return null;
            foreach (var s in servers.EnumerateObject())
            {
                string? url = null;
                if (s.Value.ValueKind == JsonValueKind.Object)
                    foreach (var key in new[] { "url", "httpUrl" })
                        if (s.Value.TryGetProperty(key, out var u) && u.ValueKind == JsonValueKind.String) { url = u.GetString(); break; }
                if (IsAgentManager(s.Name, url)) return new Entry(s.Name, url);
            }
            return null;
        }

        /// <summary>Codex (~/.codex/config.toml): tablas [mcp_servers.&lt;nombre&gt;] con su url.</summary>
        private static Entry? TomlFind(string path)
        {
            if (!File.Exists(path)) return null;
            string? table = null, url = null;
            Entry? found = null;
            void Close() { if (table != null && found == null && IsAgentManager(table, url)) found = new Entry(table, url); }
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                {
                    Close();
                    var m = Regex.Match(line, @"^\[\s*mcp_servers\.(""?)([^""\]]+)\1\s*\]$");
                    table = m.Success ? m.Groups[2].Value : null;
                    url = null;
                    continue;
                }
                if (table == null) continue;
                var u = Regex.Match(line, @"^url\s*=\s*[""']([^""']*)[""']");
                if (u.Success) url = u.Groups[1].Value;
            }
            Close();
            return found;
        }

        // =============================================================== ejecución
        private static async Task<(int, string)> RunAsync(string command, IEnumerable<string> args, Func<bool> configured)
        {
            var r = CliResolver.Resolve(command) ?? throw new InvalidOperationException($"No se encontró «{command}»");
            var psi = new ProcessStartInfo(r.FileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = Home
            };
            foreach (var a in r.PrefixArgs.Concat(args)) psi.ArgumentList.Add(a);
            psi.Environment.Remove("CLAUDECODE");
            psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");
            psi.Environment["NO_COLOR"] = "1";

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("No se pudo iniciar el proceso");
            p.StandardInput.Close();
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            // Codex guarda la entrada al instante y después abre un flujo OAuth que espera al navegador; con el
            // token en la URL no hace falta, así que en cuanto la entrada aparece se da por instalado y se corta.
            var deadline = DateTime.UtcNow + CommandTimeout;
            while (!p.HasExited)
            {
                await Task.WhenAny(p.WaitForExitAsync(), Task.Delay(500));
                if (p.HasExited) break;
                if (configured()) { Kill(p); return (0, ""); }
                if (DateTime.UtcNow > deadline) { Kill(p); return (-1, "se agotó el tiempo de espera"); }
            }
            return (p.ExitCode, (await stdout) + (await stderr));
        }

        private static void Kill(Process p)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
        }

        private static string Short(string s) => s.Length <= 200 ? s : s[..200] + "…";
    }
}
