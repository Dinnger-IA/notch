using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AgentManagerNotch.Services
{
    public enum ApprovalDecision { Allow, Deny, AllowAlways }

    public class ApprovalRequest
    {
        public string AgentId { get; init; } = "";
        public string Tool { get; init; } = "";
        public string Detail { get; init; } = "";
        public DateTime Time { get; } = DateTime.Now;
        internal TaskCompletionSource<ApprovalDecision> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Resolve(ApprovalDecision d) => Tcs.TrySetResult(d);
    }

    /// <summary>
    /// Servidor de named pipe. El hook PreToolUse de Claude Code (este mismo .exe con --hook)
    /// se conecta, envía la petición y espera la decisión del usuario.
    /// </summary>
    public class ApprovalServer
    {
        public string PipeName { get; } = $"agentmanagernotch-{Environment.ProcessId}";
        private readonly CancellationTokenSource _cts = new();

        /// <summary>Se invoca en un hilo de fondo. Debe devolver la decisión (puede tardar).</summary>
        public Func<ApprovalRequest, Task<ApprovalDecision>>? Handler { get; set; }

        public void Start()
        {
            for (int i = 0; i < 4; i++) _ = Task.Run(AcceptLoop);
        }

        public void Stop() => _cts.Cancel();

        private async Task AcceptLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(_cts.Token);
                    var p = pipe; pipe = null;
                    _ = Task.Run(() => Serve(p));
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log.Error("Pipe accept", ex);
                    await Task.Delay(500);
                }
                finally { pipe?.Dispose(); }
            }
        }

        private async Task Serve(NamedPipeServerStream pipe)
        {
            using (pipe)
            {
                try
                {
                    var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                    var line = await reader.ReadLineAsync();
                    if (line == null) return;
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    var req = new ApprovalRequest
                    {
                        AgentId = r.GetProperty("agentId").GetString() ?? "",
                        Tool = r.GetProperty("tool").GetString() ?? "",
                        Detail = r.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : ""
                    };

                    ApprovalDecision decision = ApprovalDecision.Deny;
                    if (Handler != null)
                    {
                        var task = Handler(req);
                        var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromMinutes(10)));
                        decision = done == task ? task.Result : ApprovalDecision.Deny;
                    }

                    var resp = JsonSerializer.Serialize(new { decision = decision == ApprovalDecision.Deny ? "deny" : "allow" });
                    var bytes = Encoding.UTF8.GetBytes(resp + "\n");
                    await pipe.WriteAsync(bytes);
                    await pipe.FlushAsync();
                }
                catch (Exception ex) { Log.Error("Pipe serve", ex); }
            }
        }
    }

    /// <summary>
    /// Modo hook: Claude Code ejecuta «AgentManagerNotch.exe --hook» antes de cada herramienta.
    /// Lee el JSON de stdin, pregunta al Agent Manager Notch que lanzó la sesión y responde por stdout.
    /// Si Agent Manager Notch no responde, no bloquea: sale sin decidir.
    /// </summary>
    public static class HookBridge
    {
        // Herramientas inofensivas: no molestamos al usuario por ellas.
        private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "Read", "Glob", "Grep", "LS", "WebSearch", "WebFetch", "TodoWrite", "TodoRead", "NotebookRead",
            "ToolSearch", "Task", "Agent", "ListMcpResourcesTool", "ReadMcpResourceTool", "BashOutput", "TaskOutput",
            "Skill", "AskUserQuestion", "ExitPlanMode", "EnterPlanMode"
        };
        private static readonly HashSet<string> EditTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "Edit", "Write", "MultiEdit", "NotebookEdit"
        };

        public static int Run()
        {
            try
            {
                var pipeName = Environment.GetEnvironmentVariable("COUCOU_PIPE");
                var agentId = Environment.GetEnvironmentVariable("COUCOU_AGENT_ID") ?? "";
                var mode = Environment.GetEnvironmentVariable("COUCOU_APPROVAL") ?? "Ask";

                string input;
                using (var stdin = Console.OpenStandardInput())
                using (var sr = new StreamReader(stdin, new UTF8Encoding(false)))
                    input = sr.ReadToEnd();

                if (string.IsNullOrEmpty(pipeName) || string.IsNullOrWhiteSpace(input)) return 0;

                using var doc = JsonDocument.Parse(input);
                var root = doc.RootElement;
                var tool = root.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "" : "";
                if (tool == "" || ReadOnlyTools.Contains(tool)) return 0;
                if (mode == "AcceptEdits" && EditTools.Contains(tool)) return Emit("allow", "Edición aceptada automáticamente por Agent Manager Notch");

                string detail = "";
                if (root.TryGetProperty("tool_input", out var ti))
                {
                    detail = DescribeInput(tool, ti);
                }

                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
                try { client.Connect(1500); } catch { return 0; } // Agent Manager Notch no está: no bloqueamos

                var req = JsonSerializer.Serialize(new { agentId, tool, detail }) + "\n";
                var bytes = Encoding.UTF8.GetBytes(req);
                client.Write(bytes, 0, bytes.Length);
                client.Flush();

                using var reader = new StreamReader(client, new UTF8Encoding(false));
                var line = reader.ReadLine();
                if (line == null) return 0;
                using var resp = JsonDocument.Parse(line);
                var decision = resp.RootElement.GetProperty("decision").GetString();
                return decision == "allow"
                    ? Emit("allow", "Aprobado por el usuario en Agent Manager Notch")
                    : Emit("deny", "El usuario rechazó esta acción desde Agent Manager Notch. No la reintentes; pregunta qué prefiere hacer.");
            }
            catch (Exception ex)
            {
                Log.Error("Hook", ex);
                return 0;
            }
        }

        private static string DescribeInput(string tool, JsonElement ti)
        {
            if (ti.ValueKind != JsonValueKind.Object) return ti.GetRawText();
            string S(string k) => ti.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            switch (tool)
            {
                case "Bash":
                case "PowerShell":
                    var desc = S("description");
                    return (desc != "" ? desc + "\n" : "") + "$ " + S("command");
                case "Edit":
                case "MultiEdit":
                    var o = S("old_string"); var n = S("new_string");
                    return S("file_path") + (o != "" ? $"\n- {Trim(o)}\n+ {Trim(n)}" : "");
                case "Write":
                    return S("file_path") + "\n" + Trim(S("content"), 400);
                default:
                    var raw = ti.GetRawText();
                    return raw.Length > 600 ? raw[..600] + "…" : raw;
            }
        }

        private static string Trim(string s, int max = 300) => s.Length > max ? s[..max] + "…" : s;

        private static int Emit(string decision, string reason)
        {
            var json = JsonSerializer.Serialize(new
            {
                hookSpecificOutput = new
                {
                    hookEventName = "PreToolUse",
                    permissionDecision = decision,
                    permissionDecisionReason = reason
                }
            });
            using var stdout = Console.OpenStandardOutput();
            var b = new UTF8Encoding(false).GetBytes(json);
            stdout.Write(b, 0, b.Length);
            stdout.Flush();
            return 0;
        }

        /// <summary>Escribe el settings.json que se pasa a Claude Code con --settings.</summary>
        public static string WriteClaudeHookSettings()
        {
            var exe = (Environment.ProcessPath ?? "AgentManagerNotch.exe").Replace('\\', '/');
            var settings = new
            {
                hooks = new
                {
                    PreToolUse = new[]
                    {
                        new
                        {
                            matcher = "*",
                            hooks = new[] { new { type = "command", command = $"\"{exe}\" --hook", timeout = 600 } }
                        }
                    }
                }
            };
            var path = Path.Combine(ConfigStore.Root, "claude-hook-settings.json");
            Directory.CreateDirectory(ConfigStore.Root);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            return path;
        }
    }
}
