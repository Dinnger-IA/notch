using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentManagerNotch.Models;
using AgentManagerNotch.Providers;

namespace AgentManagerNotch.Services
{
    /// <summary>Un archivo con cambios según <c>git status</c>.</summary>
    public record GitChange(string Path, char Index, char WorkTree, string? OldPath)
    {
        public bool IsUntracked => Index == '?';
        public bool IsConflict => Index == 'U' || WorkTree == 'U' || (Index == 'A' && WorkTree == 'A') || (Index == 'D' && WorkTree == 'D');
        /// <summary>Tiene algo preparado (en el índice).</summary>
        public bool IsStaged => !IsUntracked && !IsConflict && Index != ' ';
        /// <summary>Tiene algo sin preparar (en la carpeta).</summary>
        public bool IsUnstaged => IsUntracked || IsConflict || WorkTree != ' ';
    }

    public class GitStatus
    {
        public string Branch { get; init; } = "";
        public string? Upstream { get; init; }
        public int Ahead { get; init; }
        public int Behind { get; init; }
        public bool Detached { get; init; }
        public List<GitChange> Changes { get; init; } = new();
    }

    public record GitResult(int Code, string Output, string Error)
    {
        public bool Ok => Code == 0;
        /// <summary>Mensaje para mostrar si falló: la última parte de stderr (o stdout).</summary>
        public string Message => (Error.Trim() != "" ? Error : Output).Trim();
    }

    /// <summary>
    /// Integración con git para los workspaces de código: estado, preparar (stage), ramas, commit y push.
    /// Todo se hace con el ejecutable <c>git</c> del PATH; si no está, la carpeta se trata como si no fuera un repositorio.
    /// </summary>
    public static class GitService
    {
        public static async Task<GitResult> RunAsync(string dir, IEnumerable<string> args, string? stdin = null, CancellationToken ct = default)
        {
            var psi = new ProcessStartInfo("git")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                StandardInputEncoding = new UTF8Encoding(false),
                WorkingDirectory = dir
            };
            // Rutas sin escapar (acentos) y sin paginador ni preguntas en la consola: las credenciales las pide
            // el administrador de credenciales de Git en su propia ventana.
            foreach (var a in new[] { "-c", "core.quotepath=false", "-c", "color.ui=never" }) psi.ArgumentList.Add(a);
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GIT_PAGER"] = "";
            psi.Environment["LC_ALL"] = "C";

            using var p = new Process { StartInfo = psi };
            try { p.Start(); }
            catch (Exception ex) { return new GitResult(-1, "", "No se encontró git en el PATH. " + ex.Message); }
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (stdin != null) await p.StandardInput.WriteAsync(stdin);
            p.StandardInput.Close();
            try { await p.WaitForExitAsync(ct); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
            return new GitResult(p.ExitCode, await outTask, await errTask);
        }

        public static Task<GitResult> RunAsync(string dir, params string[] args) => RunAsync(dir, (IEnumerable<string>)args);

        /// <summary>Estado del repositorio, o null si la carpeta no es un repositorio git (o no hay git).</summary>
        public static async Task<GitStatus?> StatusAsync(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;
            var r = await RunAsync(dir, "status", "--porcelain=v1", "-b", "-z", "--untracked-files=all");
            if (!r.Ok) return null;
            return ParseStatus(r.Output);
        }

        internal static GitStatus ParseStatus(string raw)
        {
            var parts = raw.Split('\0');
            string branch = "", upstream = "";
            int ahead = 0, behind = 0;
            bool detached = false;
            var changes = new List<GitChange>();
            for (int i = 0; i < parts.Length; i++)
            {
                var e = parts[i];
                if (e.Length == 0) continue;
                if (e.StartsWith("## "))
                {
                    // "## main...origin/main [ahead 1, behind 2]", "## No commits yet on main", "## HEAD (no branch)"
                    var h = e[3..];
                    var bracket = h.IndexOf(" [", StringComparison.Ordinal);
                    if (bracket >= 0)
                    {
                        var info = h[(bracket + 2)..].TrimEnd(']');
                        foreach (var kv in info.Split(", "))
                        {
                            if (kv.StartsWith("ahead ") && int.TryParse(kv[6..], out var a)) ahead = a;
                            else if (kv.StartsWith("behind ") && int.TryParse(kv[7..], out var b)) behind = b;
                        }
                        h = h[..bracket];
                    }
                    foreach (var prefix in new[] { "No commits yet on ", "Initial commit on " })
                        if (h.StartsWith(prefix)) h = h[prefix.Length..];
                    if (h.StartsWith("HEAD (no branch)")) { detached = true; h = "HEAD"; }
                    var dots = h.IndexOf("...", StringComparison.Ordinal);
                    if (dots >= 0) { upstream = h[(dots + 3)..]; h = h[..dots]; }
                    branch = h;
                    continue;
                }
                if (e.Length < 4) continue;
                char x = e[0], y = e[1];
                var path = e[3..];
                string? old = null;
                // En renombrados/copias, el siguiente campo es la ruta original
                if ((x is 'R' or 'C') && i + 1 < parts.Length) old = parts[++i];
                changes.Add(new GitChange(path, x, y, old));
            }
            return new GitStatus
            {
                Branch = branch, Upstream = upstream == "" ? null : upstream, Ahead = ahead, Behind = behind,
                Detached = detached, Changes = changes.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList()
            };
        }

        public static async Task<List<string>> BranchesAsync(string dir)
        {
            var r = await RunAsync(dir, "branch", "--format=%(refname:short)");
            return r.Ok ? r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() : new();
        }

        public static Task<GitResult> StageAsync(string dir, IEnumerable<string> paths)
            => RunAsync(dir, new[] { "add", "-A", "--" }.Concat(paths));

        public static async Task<GitResult> UnstageAsync(string dir, IEnumerable<string> paths)
        {
            var list = paths.ToList();
            var r = await RunAsync(dir, new[] { "restore", "--staged", "--" }.Concat(list));
            // Sin ningún commit todavía no hay HEAD al que volver: se sacan del índice
            if (!r.Ok) r = await RunAsync(dir, new[] { "rm", "--cached", "-r", "-q", "--" }.Concat(list));
            return r;
        }

        public static Task<GitResult> SwitchAsync(string dir, string branch, bool create = false)
            => create ? RunAsync(dir, "switch", "-c", branch) : RunAsync(dir, "switch", branch);

        public static Task<GitResult> CommitAsync(string dir, string message)
            => RunAsync(dir, new[] { "commit", "-F", "-" }, message.Trim() + "\n");

        /// <summary>Push de la rama actual; si aún no sigue a ninguna remota, la publica en la primera (origin si existe).</summary>
        public static async Task<GitResult> PushAsync(string dir, GitStatus st)
        {
            if (st.Upstream != null) return await RunAsync(dir, "push");
            var remotes = (await RunAsync(dir, "remote")).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (remotes.Length == 0) return new GitResult(1, "", "El repositorio no tiene ningún remoto configurado.");
            var remote = remotes.Contains("origin") ? "origin" : remotes[0];
            return await RunAsync(dir, "push", "-u", remote, st.Branch);
        }

        /// <summary>
        /// Contexto para redactar el commit: lo preparado o, si no hay nada preparado, todos los cambios
        /// (que es lo que se confirmará). Recorta el diff para no pasar del tamaño razonable de un prompt.
        /// </summary>
        public static async Task<string> CommitContextAsync(string dir, GitStatus st, int maxDiff = 14000)
        {
            bool staged = st.Changes.Any(c => c.IsStaged);
            var cached = staged ? new[] { "--cached" } : Array.Empty<string>();
            var sb = new StringBuilder();
            var log = await RunAsync(dir, "log", "-8", "--format=%s");
            if (log.Ok && log.Output.Trim() != "") sb.Append("Commits recientes (para seguir su estilo):\n").Append(log.Output.Trim()).Append("\n\n");
            sb.Append("Archivos:\n");
            foreach (var c in st.Changes.Where(c => !staged || c.IsStaged))
                sb.Append(c.IsUntracked ? "??" : $"{(staged ? c.Index : c.WorkTree)}").Append(' ').Append(c.Path).Append('\n');
            var diff = await RunAsync(dir, new[] { "diff", "--no-ext-diff" }.Concat(cached));
            var text = diff.Output;
            // Los archivos nuevos sin seguimiento no salen en git diff: se añade el principio de cada uno
            if (!staged)
                foreach (var c in st.Changes.Where(c => c.IsUntracked).Take(10))
                {
                    try
                    {
                        var full = Path.Combine(dir, c.Path);
                        if (new FileInfo(full).Length > 200_000) continue;
                        var content = await File.ReadAllTextAsync(full);
                        if (content.Contains('\0')) continue;
                        text += $"\n--- archivo nuevo: {c.Path}\n{(content.Length > 2000 ? content[..2000] + "\n…" : content)}\n";
                    }
                    catch { }
                }
            if (text.Length > maxDiff) text = text[..maxDiff] + "\n… (diff recortado)";
            sb.Append("\nDiff:\n").Append(text);
            return sb.ToString();
        }

        /// <summary>
        /// Pide al CLI del agente (en solo lectura y sin sesión) que redacte el mensaje del commit.
        /// Devuelve el texto limpio o lanza una excepción con el error del CLI.
        /// </summary>
        public static async Task<string> WriteCommitMessageAsync(AgentProfile profile, string dir, string context, CancellationToken ct)
        {
            var p = profile.Clone();
            p.WorkingDirectory = dir;
            p.Approval = ApprovalMode.ReadOnly;
            var prompt =
                "Escribe el mensaje de commit para los cambios de abajo. Sigue el idioma y el estilo de los commits recientes " +
                "(si no hay, escríbelo en español). Formato: un título breve (máximo 72 caracteres), una línea en blanco y " +
                "un cuerpo corto que explique el porqué. Responde SOLO con el mensaje, sin comillas, sin bloques de código " +
                "y sin usar herramientas.\n\n" + context;
            var provider = ProviderFactory.Create(p.Provider);
            var spec = provider.Build(p, prompt, null, new RunContext { AgentId = p.Id, AgentName = p.Name });
            var psi = spec.Psi;
            // Marca la ejecución como del notch: sin esto, los hooks globales de Claude Code (y el aviso de Codex) la
            // toman por una sesión de la terminal y el prompt y el mensaje del commit aparecen en el chat
            psi.Environment["COUCOU_AGENT_ID"] = $"{p.Id}:git-commit";

            using var proc = new Process { StartInfo = psi };
            var stderr = new StringBuilder();
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
            proc.Start();
            proc.BeginErrorReadLine();
            using var reg = ct.Register(() => { try { proc.Kill(true); } catch { } });
            if (spec.StdinText != null) await proc.StandardInput.WriteAsync(spec.StdinText);
            proc.StandardInput.Close();

            string? final = null, error = null;
            var deltas = new StringBuilder();
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
            {
                foreach (var ev in provider.Parse(line))
                    switch (ev)
                    {
                        case DeltaEvent d: deltas.Append(d.Text); break;
                        case TextEvent t: final = t.Text; break;
                        case ResultEvent { Success: false } r: error = r.Error; break;
                    }
            }
            await proc.WaitForExitAsync();
            ct.ThrowIfCancellationRequested();
            var text = final ?? deltas.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                string err; lock (stderr) err = stderr.ToString().Trim();
                throw new InvalidOperationException(error ?? (err != "" ? err : $"El CLI terminó con código {proc.ExitCode} sin escribir nada."));
            }
            return CleanMessage(text);
        }

        /// <summary>Quita vallas de código y comillas que a veces añade el modelo.</summary>
        internal static string CleanMessage(string s)
        {
            s = s.Trim();
            if (s.StartsWith("```"))
            {
                var nl = s.IndexOf('\n');
                s = nl >= 0 ? s[(nl + 1)..] : "";
                if (s.TrimEnd().EndsWith("```")) s = s.TrimEnd()[..^3];
            }
            s = s.Trim();
            if (s.Length > 1 && s[0] == '"' && s[^1] == '"') s = s[1..^1].Trim();
            return s.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        }
    }
}
