using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentManagerNotch.Models;
using AgentManagerNotch.Plugins;
using AgentManagerNotch.Services;

namespace AgentManagerNotch.Providers
{
    // ---------- Eventos normalizados que emiten todos los CLI ----------
    public abstract record AgentEvent;
    public record SessionEvent(string SessionId) : AgentEvent;
    public record DeltaEvent(string Text) : AgentEvent;
    public record TextEvent(string Text) : AgentEvent;
    public record ToolEvent(string Tool, string Detail) : AgentEvent;
    public record NoticeEvent(string Text, bool IsError) : AgentEvent;
    public record ResultEvent(bool Success, string? Error, double? CostUsd) : AgentEvent;
    /// <summary>Lista de pasos que el propio CLI mantiene (TodoWrite, todo_list, write_todos).</summary>
    public record TodoEvent(IReadOnlyList<TaskStep> Steps) : AgentEvent;

    public class RunContext
    {
        public string AgentId { get; init; } = "";
        public string AgentName { get; init; } = "";
        public string PipeName { get; init; } = "";
        public string HookSettingsPath { get; init; } = "";
        /// <summary>System prompt del agente + instrucciones de Agent Manager Notch (fecha, recordatorios).</summary>
        public string FullSystemPrompt { get; init; } = "";
        public IReadOnlyList<ChatMessage> History { get; init; } = Array.Empty<ChatMessage>();
        /// <summary>Carpetas de otros workspaces citados con @ en el mensaje: el CLI necesita acceso para leerlas.</summary>
        public IReadOnlyList<string> ExtraDirs { get; set; } = Array.Empty<string>();
    }

    public class LaunchSpec
    {
        public ProcessStartInfo Psi { get; init; } = new();
        public string? StdinText { get; init; }
    }

    public interface ICliProvider
    {
        LaunchSpec Build(AgentProfile p, string prompt, string? sessionId, RunContext ctx);
        IEnumerable<AgentEvent> Parse(string line);
        /// <summary>true si el CLI imprime texto plano (sin eventos JSON).</summary>
        bool PlainText { get; }
    }

    public static class ProviderFactory
    {
        /// <summary>El proveedor de un agente (el de su plugin si usa uno).</summary>
        public static ICliProvider Create(AgentProfile p) =>
            p.Provider == ProviderKind.Plugin
                ? PluginHost.Provider(p.PluginProvider)?.Create() ?? new PluginHost.MissingProvider(p.PluginProvider ?? "?")
                : Create(p.Provider);

        public static string Label(AgentProfile p) =>
            p.Provider == ProviderKind.Plugin ? PluginHost.Provider(p.PluginProvider)?.Label ?? p.PluginProvider ?? "Plugin" : Label(p.Provider);

        public static ICliProvider Create(ProviderKind kind) => kind switch
        {
            ProviderKind.ClaudeCode => new ClaudeCodeProvider(),
            ProviderKind.Codex => new CodexProvider(),
            ProviderKind.Gemini => new GeminiProvider(),
            _ => new CustomProvider()
        };

        public static string Label(ProviderKind kind) => kind switch
        {
            ProviderKind.ClaudeCode => "Claude Code",
            ProviderKind.Codex => "Codex",
            ProviderKind.Gemini => "Gemini CLI",
            ProviderKind.Plugin => "Plugin",
            _ => "Personalizado"
        };

        public static string Executable(ProviderKind kind) => kind switch
        {
            ProviderKind.ClaudeCode => "claude",
            ProviderKind.Codex => "codex",
            ProviderKind.Gemini => "gemini",
            _ => ""
        };

        internal static ProcessStartInfo NewPsi(string command, AgentProfile p)
        {
            var r = CliResolver.Resolve(command)
                    ?? throw new InvalidOperationException(
                        $"No se encontró «{command}» en el PATH. Instálalo o usa un agente con otro CLI.");
            var psi = new ProcessStartInfo(r.FileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                StandardInputEncoding = new UTF8Encoding(false),
                WorkingDirectory = ResolveWorkDir(p.WorkingDirectory)
            };
            foreach (var a in r.PrefixArgs) psi.ArgumentList.Add(a);
            // Si Agent Manager Notch se lanzó desde una sesión de Claude Code, evitamos que el hijo se crea anidado.
            psi.Environment.Remove("CLAUDECODE");
            psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");
            psi.Environment["NO_COLOR"] = "1";
            psi.Environment["FORCE_COLOR"] = "0";
            return psi;
        }

        internal static string ResolveWorkDir(string dir)
        {
            if (!string.IsNullOrWhiteSpace(dir))
            {
                var expanded = Environment.ExpandEnvironmentVariables(dir.Trim());
                if (Directory.Exists(expanded)) return expanded;
            }
            var def = Path.Combine(ConfigStore.Root, "workspace");
            Directory.CreateDirectory(def);
            return def;
        }

        internal static void AddExtra(ProcessStartInfo psi, string extra)
        {
            foreach (var a in CliResolver.SplitArgs(extra)) psi.ArgumentList.Add(a);
        }

        internal static string Str(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";

        internal static JsonElement? Obj(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : null;

        internal static string Short(string s, int max = 140)
        {
            s = s.Replace("\r", " ").Replace("\n", " ⏎ ").Trim();
            return s.Length <= max ? s : s[..max] + "…";
        }

        internal static StepStatus ParseStepStatus(string s) => s.ToLowerInvariant() switch
        {
            "completed" or "done" or "complete" => StepStatus.Completed,
            "in_progress" or "in-progress" or "inprogress" or "active" or "running" => StepStatus.InProgress,
            _ => StepStatus.Pending
        };

        /// <summary>Lee un arreglo de todos con formato {content|text|description, status|completed}.</summary>
        internal static List<TaskStep>? ParseTodos(JsonElement? arr)
        {
            if (arr is not { ValueKind: JsonValueKind.Array } a) return null;
            var list = new List<TaskStep>();
            foreach (var t in a.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object) continue;
                var text = Str(t, "content");
                if (text == "") text = Str(t, "text");
                if (text == "") text = Str(t, "description");
                if (text == "") text = Str(t, "subject");
                if (text == "") continue;
                StepStatus st;
                if (t.TryGetProperty("completed", out var c) && (c.ValueKind == JsonValueKind.True || c.ValueKind == JsonValueKind.False))
                    st = c.ValueKind == JsonValueKind.True ? StepStatus.Completed : StepStatus.Pending;
                else st = ParseStepStatus(Str(t, "status"));
                if (st == StepStatus.InProgress)
                {
                    var active = Str(t, "activeForm");
                    if (active != "") text = active;
                }
                list.Add(new TaskStep { Text = text, Status = st });
            }
            return list;
        }

        /// <summary>Resume el input de una herramienta en una línea legible.</summary>
        internal static string SummarizeInput(JsonElement? input)
        {
            if (input is not { ValueKind: JsonValueKind.Object } i) return "";
            foreach (var key in new[] { "command", "file_path", "path", "notebook_path", "pattern", "url", "query", "description", "prompt", "skill" })
            {
                var v = Str(i, key);
                if (!string.IsNullOrEmpty(v)) return Short(v);
            }
            return Short(i.GetRawText(), 120);
        }
    }

    // =====================================================================
    // Claude Code:  claude -p --output-format stream-json --verbose
    // =====================================================================
    public class ClaudeCodeProvider : ICliProvider
    {
        public bool PlainText => false;

        public LaunchSpec Build(AgentProfile p, string prompt, string? sessionId, RunContext ctx)
        {
            var psi = ProviderFactory.NewPsi("claude", p);
            var a = psi.ArgumentList;
            a.Add("-p");
            a.Add("--output-format"); a.Add("stream-json");
            a.Add("--verbose");
            a.Add("--include-partial-messages");
            if (!string.IsNullOrWhiteSpace(ctx.FullSystemPrompt)) { a.Add("--append-system-prompt"); a.Add(ctx.FullSystemPrompt); }
            if (!string.IsNullOrWhiteSpace(p.Model)) { a.Add("--model"); a.Add(p.Model.Trim()); }
            if (!string.IsNullOrEmpty(sessionId)) { a.Add("--resume"); a.Add(sessionId); }
            foreach (var dir in ctx.ExtraDirs) { a.Add("--add-dir"); a.Add(dir); }

            switch (p.Approval)
            {
                case ApprovalMode.Auto:
                    a.Add("--dangerously-skip-permissions");
                    break;
                case ApprovalMode.ReadOnly:
                    a.Add("--permission-mode"); a.Add("default");
                    a.Add("--disallowedTools"); a.Add("Bash,PowerShell,Edit,Write,MultiEdit,NotebookEdit");
                    break;
                case ApprovalMode.AcceptEdits:
                    a.Add("--permission-mode"); a.Add("acceptEdits");
                    a.Add("--settings"); a.Add(ctx.HookSettingsPath);
                    break;
                default:
                    a.Add("--permission-mode"); a.Add("default");
                    a.Add("--settings"); a.Add(ctx.HookSettingsPath);
                    break;
            }
            ProviderFactory.AddExtra(psi, p.ExtraArgs);

            // El prompt va por stdin: evita límites de longitud y problemas de comillas.
            return new LaunchSpec { Psi = psi, StdinText = prompt };
        }

        public IEnumerable<AgentEvent> Parse(string line)
        {
            if (!line.StartsWith('{')) yield break;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch { yield break; }
            using (doc)
            {
                var r = doc.RootElement;
                var type = ProviderFactory.Str(r, "type");
                switch (type)
                {
                    case "system":
                        if (ProviderFactory.Str(r, "subtype") == "init")
                        {
                            var sid = ProviderFactory.Str(r, "session_id");
                            if (sid != "") yield return new SessionEvent(sid);
                        }
                        break;

                    case "stream_event":
                        {
                            var ev = ProviderFactory.Obj(r, "event");
                            if (ev is { } e && ProviderFactory.Str(e, "type") == "content_block_delta"
                                && ProviderFactory.Obj(e, "delta") is { } d && ProviderFactory.Str(d, "type") == "text_delta")
                            {
                                var t = ProviderFactory.Str(d, "text");
                                if (t != "") yield return new DeltaEvent(t);
                            }
                            break;
                        }

                    case "assistant":
                        {
                            if (ProviderFactory.Obj(r, "message") is { } msg
                                && ProviderFactory.Obj(msg, "content") is { ValueKind: JsonValueKind.Array } content)
                            {
                                foreach (var block in content.EnumerateArray())
                                {
                                    var bt = ProviderFactory.Str(block, "type");
                                    if (bt == "text")
                                    {
                                        var t = ProviderFactory.Str(block, "text");
                                        if (!string.IsNullOrWhiteSpace(t)) yield return new TextEvent(t);
                                    }
                                    else if (bt == "tool_use")
                                    {
                                        var name = ProviderFactory.Str(block, "name");
                                        var input = ProviderFactory.Obj(block, "input");
                                        if (name.Contains("Todo", StringComparison.OrdinalIgnoreCase)
                                            && input is { } inp && ProviderFactory.ParseTodos(ProviderFactory.Obj(inp, "todos")) is { } todos)
                                        {
                                            yield return new TodoEvent(todos);
                                            continue;
                                        }
                                        yield return new ToolEvent(name, ProviderFactory.SummarizeInput(input));
                                    }
                                }
                            }
                            break;
                        }

                    case "result":
                        {
                            bool isError = r.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                            double? cost = r.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null;
                            string? err = null;
                            if (isError)
                            {
                                err = ProviderFactory.Str(r, "result");
                                if (err == "") err = ProviderFactory.Str(r, "subtype");
                            }
                            yield return new ResultEvent(!isError, err, cost);
                            break;
                        }
                }
            }
        }
    }

    // =====================================================================
    // Codex:  codex exec --json [resume <id>] <prompt>
    // =====================================================================
    public class CodexProvider : ICliProvider
    {
        public bool PlainText => false;

        public LaunchSpec Build(AgentProfile p, string prompt, string? sessionId, RunContext ctx)
        {
            var psi = ProviderFactory.NewPsi("codex", p);
            var a = psi.ArgumentList;
            a.Add("exec");
            a.Add("--json");
            a.Add("--skip-git-repo-check");
            a.Add("--color"); a.Add("never");
            if (!string.IsNullOrWhiteSpace(p.Model)) { a.Add("-m"); a.Add(p.Model.Trim()); }
            switch (p.Approval)
            {
                case ApprovalMode.Auto: a.Add("--dangerously-bypass-approvals-and-sandbox"); break;
                case ApprovalMode.ReadOnly: a.Add("-s"); a.Add("read-only"); break;
                default: a.Add("-s"); a.Add("workspace-write"); break;
            }
            if (!string.IsNullOrWhiteSpace(ctx.FullSystemPrompt))
            {
                a.Add("-c");
                a.Add("developer_instructions=" + TomlString(ctx.FullSystemPrompt));
            }
            ProviderFactory.AddExtra(psi, p.ExtraArgs);

            if (!string.IsNullOrEmpty(sessionId))
            {
                a.Add("resume");
                a.Add(sessionId);
            }
            else
            {
                a.Add("-C"); a.Add(psi.WorkingDirectory);
            }
            a.Add("-"); // el prompt se lee de stdin
            return new LaunchSpec { Psi = psi, StdinText = prompt };
        }

        private static string TomlString(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append($"\\u{(int)c:X4}"); else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        public IEnumerable<AgentEvent> Parse(string line)
        {
            if (!line.StartsWith('{')) yield break;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch { yield break; }
            using (doc)
            {
                var r = doc.RootElement;
                var type = ProviderFactory.Str(r, "type");
                switch (type)
                {
                    case "thread.started":
                        {
                            var id = ProviderFactory.Str(r, "thread_id");
                            if (id != "") yield return new SessionEvent(id);
                            break;
                        }
                    case "item.started":
                    case "item.updated":
                    case "item.completed":
                        {
                            if (ProviderFactory.Obj(r, "item") is not { } item) break;
                            var it = ProviderFactory.Str(item, "type");
                            bool started = type == "item.started";
                            if (type == "item.updated" && it != "todo_list") break;
                            switch (it)
                            {
                                case "agent_message":
                                    if (!started)
                                    {
                                        var t = ProviderFactory.Str(item, "text");
                                        if (!string.IsNullOrWhiteSpace(t)) yield return new TextEvent(t);
                                    }
                                    break;
                                case "command_execution":
                                    if (started) yield return new ToolEvent("Shell", ProviderFactory.Short(ProviderFactory.Str(item, "command")));
                                    break;
                                case "file_change":
                                    if (!started && ProviderFactory.Obj(item, "changes") is { ValueKind: JsonValueKind.Array } ch)
                                    {
                                        var paths = ch.EnumerateArray().Select(x => ProviderFactory.Str(x, "path")).Where(x => x != "");
                                        yield return new ToolEvent("Edit", ProviderFactory.Short(string.Join(", ", paths)));
                                    }
                                    break;
                                case "mcp_tool_call":
                                    if (started) yield return new ToolEvent("MCP",
                                        ProviderFactory.Str(item, "server") + "." + ProviderFactory.Str(item, "tool"));
                                    break;
                                case "todo_list":
                                    if (ProviderFactory.ParseTodos(ProviderFactory.Obj(item, "items")) is { } todos)
                                        yield return new TodoEvent(todos);
                                    break;
                                case "web_search":
                                    if (started) yield return new ToolEvent("Web", ProviderFactory.Short(ProviderFactory.Str(item, "query")));
                                    break;
                                case "error":
                                    if (!started)
                                    {
                                        var m = ProviderFactory.Str(item, "message");
                                        // Avisos internos poco útiles para el usuario
                                        if (m != "" && !m.Contains("Skill descriptions were shortened"))
                                            yield return new NoticeEvent(m, false);
                                    }
                                    break;
                            }
                            break;
                        }
                    case "turn.completed":
                        yield return new ResultEvent(true, null, null);
                        break;
                    case "turn.failed":
                        {
                            var err = ProviderFactory.Obj(r, "error") is { } e ? ProviderFactory.Str(e, "message") : "Falló el turno";
                            yield return new ResultEvent(false, err, null);
                            break;
                        }
                    case "error":
                        yield return new NoticeEvent(ProviderFactory.Str(r, "message"), true);
                        break;
                }
            }
        }
    }

    // =====================================================================
    // Gemini CLI:  gemini -p <prompt> --output-format stream-json
    // =====================================================================
    public class GeminiProvider : ICliProvider
    {
        public bool PlainText => false;

        public LaunchSpec Build(AgentProfile p, string prompt, string? sessionId, RunContext ctx)
        {
            var psi = ProviderFactory.NewPsi("gemini", p);
            var a = psi.ArgumentList;
            var full = string.IsNullOrEmpty(sessionId) && !string.IsNullOrWhiteSpace(ctx.FullSystemPrompt)
                ? $"<instrucciones_del_sistema>\n{ctx.FullSystemPrompt}\n</instrucciones_del_sistema>\n\n{prompt}"
                : prompt;
            a.Add("-p"); a.Add(full);
            a.Add("--output-format"); a.Add("stream-json");
            if (!string.IsNullOrWhiteSpace(p.Model)) { a.Add("-m"); a.Add(p.Model.Trim()); }
            if (!string.IsNullOrEmpty(sessionId)) { a.Add("--resume"); a.Add(sessionId); }
            if (ctx.ExtraDirs.Count > 0) { a.Add("--include-directories"); a.Add(string.Join(",", ctx.ExtraDirs)); }
            switch (p.Approval)
            {
                case ApprovalMode.Auto: a.Add("--yolo"); break;
                case ApprovalMode.AcceptEdits: a.Add("--approval-mode"); a.Add("auto_edit"); break;
            }
            ProviderFactory.AddExtra(psi, p.ExtraArgs);
            return new LaunchSpec { Psi = psi };
        }

        public IEnumerable<AgentEvent> Parse(string line)
        {
            if (!line.StartsWith('{')) yield break;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch { yield break; }
            using (doc)
            {
                var r = doc.RootElement;
                switch (ProviderFactory.Str(r, "type"))
                {
                    case "init":
                        var sid = ProviderFactory.Str(r, "session_id");
                        if (sid != "") yield return new SessionEvent(sid);
                        break;
                    case "message":
                        if (ProviderFactory.Str(r, "role") == "assistant")
                        {
                            var t = ProviderFactory.Str(r, "content");
                            bool delta = r.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.True;
                            if (t != "") yield return delta ? new DeltaEvent(t) : new TextEvent(t);
                        }
                        break;
                    case "tool_use":
                        {
                            var tn = ProviderFactory.Str(r, "tool_name");
                            var pars = ProviderFactory.Obj(r, "parameters");
                            if (tn.Contains("todo", StringComparison.OrdinalIgnoreCase) && pars is { } pp
                                && ProviderFactory.ParseTodos(ProviderFactory.Obj(pp, "todos")) is { } todos)
                            {
                                yield return new TodoEvent(todos);
                                break;
                            }
                            yield return new ToolEvent(tn, ProviderFactory.SummarizeInput(pars));
                            break;
                        }
                    case "error":
                        yield return new NoticeEvent(ProviderFactory.Str(r, "message"), true);
                        break;
                    case "result":
                        var ok = ProviderFactory.Str(r, "status") is "" or "success";
                        yield return new ResultEvent(ok, ok ? null : ProviderFactory.Str(r, "status"), null);
                        break;
                }
            }
        }
    }

    // =====================================================================
    // Personalizado: cualquier comando que imprima texto (ollama, aider, scripts...)
    // =====================================================================
    public class CustomProvider : ICliProvider
    {
        private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
        public bool PlainText => true;

        public LaunchSpec Build(AgentProfile p, string prompt, string? sessionId, RunContext ctx)
        {
            var tokens = CliResolver.SplitArgs(p.CustomCommand);
            if (tokens.Count == 0)
                throw new InvalidOperationException("Este agente personalizado no tiene comando configurado.");

            // Sin sesión propia: mandamos el contexto de la conversación en el prompt.
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(ctx.FullSystemPrompt))
                sb.AppendLine("[Sistema]").AppendLine(ctx.FullSystemPrompt).AppendLine();
            var hist = ctx.History.Where(m => m.Role is ChatRole.User or ChatRole.Agent).TakeLast(20).ToList();
            if (hist.Count > 1)
            {
                sb.AppendLine("[Conversación previa]");
                foreach (var m in hist.Take(hist.Count - 1))
                    sb.AppendLine((m.Role == ChatRole.User ? "Usuario: " : "Asistente: ") + m.Text);
                sb.AppendLine();
            }
            sb.AppendLine("[Usuario]").Append(prompt);
            var fullPrompt = sb.ToString();

            bool promptInArgs = tokens.Any(t => t.Contains("{prompt}"));
            var psi = ProviderFactory.NewPsi(tokens[0], p);
            foreach (var t in tokens.Skip(1))
                psi.ArgumentList.Add(t.Replace("{prompt}", fullPrompt)
                                      .Replace("{system}", ctx.FullSystemPrompt)
                                      .Replace("{model}", p.Model));
            ProviderFactory.AddExtra(psi, p.ExtraArgs);
            return new LaunchSpec { Psi = psi, StdinText = promptInArgs ? null : fullPrompt };
        }

        public IEnumerable<AgentEvent> Parse(string line)
        {
            yield return new DeltaEvent(Ansi.Replace(line, "") + "\n");
        }
    }
}
