using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentManagerNotch.Services
{
    /// <summary>
    /// Encuentra un CLI en el PATH y lo convierte en algo que Process puede lanzar sin
    /// pasar por cmd.exe (los shims .cmd de npm rompen comillas y saltos de línea).
    /// </summary>
    public static class CliResolver
    {
        public record Resolved(string FileName, List<string> PrefixArgs);

        private static readonly string[] Exts = { ".exe", ".cmd", ".bat", ".ps1", "" };

        public static Resolved? Resolve(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            string? path = Find(command);
            if (path == null) return null;

            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".cmd" or ".bat")
            {
                var js = TryNpmShim(path);
                if (js != null)
                {
                    var dir = Path.GetDirectoryName(path)!;
                    var localNode = Path.Combine(dir, "node.exe");
                    var node = File.Exists(localNode) ? localNode : Find("node");
                    if (node != null) return new Resolved(node, new List<string> { js });
                }
                return new Resolved(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                    new List<string> { "/d", "/c", path });
            }
            if (ext == ".ps1")
                return new Resolved("powershell.exe",
                    new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", path });
            return new Resolved(path, new List<string>());
        }

        public static string? Find(string command)
        {
            if (Path.IsPathRooted(command))
            {
                if (File.Exists(command)) return command;
                foreach (var e in Exts) if (File.Exists(command + e)) return command + e;
                return null;
            }

            var dirs = new List<string>();
            dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            dirs.Add(Path.Combine(home, ".local", "bin"));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"));

            bool hasExt = Path.HasExtension(command);
            foreach (var e in Exts)
            {
                if (hasExt && e != "") continue;
                foreach (var d in dirs)
                {
                    try
                    {
                        var p = Path.Combine(d, command + e);
                        if (File.Exists(p)) return p;
                    }
                    catch { /* entrada inválida en PATH */ }
                }
            }
            return null;
        }

        private static string? TryNpmShim(string cmdPath)
        {
            try
            {
                var text = File.ReadAllText(cmdPath);
                var m = Regex.Match(text, @"%~?dp0%?\\(?<js>[^""\s%]+?\.(?:js|cjs|mjs))", RegexOptions.IgnoreCase);
                if (!m.Success) return null;
                var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cmdPath)!, m.Groups["js"].Value));
                return File.Exists(full) ? full : null;
            }
            catch { return null; }
        }

        /// <summary>Divide una línea de argumentos respetando comillas dobles.</summary>
        public static List<string> SplitArgs(string? line)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(line)) return result;
            var sb = new StringBuilder();
            bool inQuotes = false, any = false;
            foreach (var c in line)
            {
                if (c == '"') { inQuotes = !inQuotes; any = true; continue; }
                if (char.IsWhiteSpace(c) && !inQuotes)
                {
                    if (sb.Length > 0 || any) { result.Add(sb.ToString()); sb.Clear(); any = false; }
                    continue;
                }
                sb.Append(c);
            }
            if (sb.Length > 0 || any) result.Add(sb.ToString());
            return result;
        }
    }
}
