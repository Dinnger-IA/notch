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

namespace AgentManagerNotch.Services
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

        public enum Status { NotInstalled, AlreadyConfigured, Installed, Failed }
        public record Result(ProviderKind Cli, Status Status, string? Error = null);

        private static readonly ProviderKind[] Clis = { ProviderKind.ClaudeCode, ProviderKind.Codex, ProviderKind.Gemini };

        /// <summary>Revisa e instala donde falte. Si ya hay una revisión en curso, no hace nada.</summary>
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
                if (IsConfigured(cli)) return new Result(cli, Status.AlreadyConfigured);
                var args = cli switch
                {
                    ProviderKind.ClaudeCode => new[] { "mcp", "add", "--transport", "http", "--scope", "user", ServerName, url },
                    ProviderKind.Codex => new[] { "mcp", "add", ServerName, "--url", url },
                    _ => new[] { "mcp", "add", "--scope", "user", "--transport", "http", ServerName, url }
                };
                var (code, output) = await RunAsync(exe, args);
                if (code == 0 && IsConfigured(cli)) return new Result(cli, Status.Installed);
                // Nunca se registra el token
                var msg = output.Replace(token, "***").Replace(Uri.EscapeDataString(token), "***").Trim();
                return new Result(cli, Status.Failed, code == 0 ? "no aparece en la configuración tras instalarlo" : $"código {code}: {Short(msg)}");
            }
            catch (Exception ex) { return new Result(cli, Status.Failed, ex.Message); }
        }

        // =============================================================== detección
        private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        private static bool IsConfigured(ProviderKind cli) => cli switch
        {
            ProviderKind.ClaudeCode => JsonHasServer(ClaudeConfigPath()),
            ProviderKind.Codex => TomlHasServer(Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } ch ? ch : Path.Combine(Home, ".codex"), "config.toml")),
            _ => JsonHasServer(Path.Combine(Home, ".gemini", "settings.json"))
        };

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
        private static bool JsonHasServer(string path)
        {
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty("mcpServers", out var servers) || servers.ValueKind != JsonValueKind.Object) return false;
            foreach (var s in servers.EnumerateObject())
            {
                string? url = null;
                if (s.Value.ValueKind == JsonValueKind.Object)
                    foreach (var key in new[] { "url", "httpUrl" })
                        if (s.Value.TryGetProperty(key, out var u) && u.ValueKind == JsonValueKind.String) { url = u.GetString(); break; }
                if (IsAgentManager(s.Name, url)) return true;
            }
            return false;
        }

        /// <summary>Codex (~/.codex/config.toml): tabla [mcp_servers.agent-manager] o un servidor con su URL.</summary>
        private static bool TomlHasServer(string path)
        {
            if (!File.Exists(path)) return false;
            var toml = File.ReadAllText(path);
            if (Regex.IsMatch(toml, @"^\s*\[\s*mcp_servers\.(""?)" + Regex.Escape(ServerName) + @"\1\s*\]", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                return true;
            return Regex.IsMatch(toml, @"^\s*url\s*=\s*[""']" + Regex.Escape(McpEndpoint), RegexOptions.Multiline | RegexOptions.IgnoreCase);
        }

        // =============================================================== ejecución
        private static async Task<(int, string)> RunAsync(string command, IEnumerable<string> args)
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
            using var cts = new CancellationTokenSource(CommandTimeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (-1, "se agotó el tiempo de espera");
            }
            return (p.ExitCode, (await stdout) + (await stderr));
        }

        private static string Short(string s) => s.Length <= 200 ? s : s[..200] + "…";
    }
}
