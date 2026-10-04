using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AgentManagerNotch.Services
{
    /// <summary>
    /// Canal de control: permite que scripts y otros programas hablen con Agent Manager Notch.
    ///   AgentManagerNotch.exe --notify "Título" "Mensaje" [--agent Nombre]
    ///   AgentManagerNotch.exe --ask Nombre "texto para el agente"
    ///   AgentManagerNotch.exe --claude-event      (hook global de Claude Code; JSON por stdin)
    ///   AgentManagerNotch.exe --codex-event JSON  (notify de Codex; JSON como último argumento)
    /// </summary>
    public static class ControlChannel
    {
        public static string PipeName => "agentmanagernotch-control-" + Environment.UserName.ToLowerInvariant();

        // ------------------------------------------------------------ servidor (dentro de la app)
        public static void StartServer(Action<JsonObject> onMessage, CancellationToken ct)
        {
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 4,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                        await pipe.WaitForConnectionAsync(ct);
                        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                        var line = await reader.ReadLineAsync(ct);
                        if (line != null && JsonNode.Parse(line) is JsonObject obj) onMessage(obj);
                        var ok = Encoding.UTF8.GetBytes("{\"ok\":true}\n");
                        await pipe.WriteAsync(ok, ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { Log.Error("Control pipe", ex); await Task.Delay(300); }
                }
            }, ct);
        }

        // ------------------------------------------------------------ cliente (modo línea de comandos)
        public static bool Send(object message, int timeoutMs = 1500)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
                client.Connect(timeoutMs);
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message) + "\n");
                client.Write(bytes, 0, bytes.Length);
                client.Flush();
                using var reader = new StreamReader(client);
                reader.ReadLine();
                return true;
            }
            catch { return false; }
        }

        /// <summary>Procesa los argumentos de línea de comandos. Devuelve null si no es un comando de control.</summary>
        public static int? TryRunCli(string[] args)
        {
            if (args.Length == 0) return null;
            switch (args[0])
            {
                case "--notify":
                    {
                        var title = args.Length > 1 ? args[1] : "Agent Manager Notch";
                        var body = args.Length > 2 ? args[2] : "";
                        var agentIdx = Array.IndexOf(args, "--agent");
                        var agent = agentIdx >= 0 && agentIdx + 1 < args.Length ? args[agentIdx + 1] : null;
                        return Send(new { cmd = "notify", title, body, agent }) ? 0 : 1;
                    }
                case "--code":
                    {
                        // Abre un agente de código para una carpeta (por defecto, la actual)
                        var folder = args.Length > 1 ? args[1] : Environment.CurrentDirectory;
                        folder = Path.GetFullPath(folder);
                        if (!Directory.Exists(folder)) return 2;
                        return Send(new { cmd = "code", folder }) ? 0 : 1;
                    }
                case "--show":
                    return Send(new { cmd = "show" }) ? 0 : 1;
                case "--new-agent":
                    return Send(new { cmd = "new-agent" }) ? 0 : 1;
                case "--reminders":
                    return Send(new { cmd = "reminders" }) ? 0 : 1;
                case "--release-notes":
                    // Notas de la versión (opcionalmente de una concreta: --release-notes 0.3.0)
                    return Send(new { cmd = "release-notes", version = args.Length > 1 ? args[1] : null }) ? 0 : 1;
                case "--ask":
                    {
                        if (args.Length < 3) return 2;
                        return Send(new { cmd = "ask", agent = args[1], text = string.Join(" ", args.Skip(2)) }) ? 0 : 1;
                    }
                case "--codex-event":
                    {
                        // Codex lo llama al terminar cada turno de una sesión de la terminal (opción notify), con el JSON
                        // como último argumento. Las sesiones de los agentes del notch no cuentan.
                        // Si notify lo usaba otra app (p. ej. la app de Codex), se le sigue pasando el aviso.
                        if (args.Length > 1) CodexNotify.ForwardToPrevious(args[^1]);
                        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COUCOU_AGENT_ID"))) return 0;
                        try
                        {
                            if (args.Length > 1 && JsonNode.Parse(args[^1]) is JsonObject payload)
                                Send(new { cmd = "codex-event", payload }, 800);
                        }
                        catch (Exception ex) { Log.Error("codex-event", ex); }
                        return 0;
                    }
                case "--claude-event":
                    {
                        // Nunca bloquea a Claude Code: si Agent Manager Notch no está, sale enseguida.
                        // Las sesiones lanzadas por los propios agentes de Agent Manager Notch ya notifican por su cuenta.
                        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COUCOU_AGENT_ID"))) return 0;
                        try
                        {
                            string input;
                            using (var sr = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
                                input = sr.ReadToEnd();
                            if (!string.IsNullOrWhiteSpace(input))
                                Send(new { cmd = "claude-event", payload = JsonNode.Parse(input) }, 800);
                        }
                        catch (Exception ex) { Log.Error("claude-event", ex); }
                        return 0;
                    }
            }
            return null;
        }

        // ------------------------------------------------------------ hooks globales de Claude Code (opcional)
        private static string ClaudeSettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

        private static string HookCommand(string? exe) => $"\"{(exe ?? Environment.ProcessPath ?? "AgentManagerNotch.exe").Replace('\\', '/')}\" --claude-event";

        /// <summary>Si los hooks globales apuntan a otro ejecutable (p. ej. el nombre anterior), los actualiza.</summary>
        public static void RefreshClaudeIntegrationPath()
        {
            try
            {
                if (!IsClaudeIntegrationInstalled()) return;
                var text = File.ReadAllText(ClaudeSettingsPath);
                var exe = (Environment.ProcessPath ?? "").Replace('\\', '/');
                // También si falta algún evento nuevo (UserPromptSubmit se añadió después)
                if ((exe != "" && !text.Contains(exe)) || !HasAllEvents(text)) SetClaudeIntegration(true);
            }
            catch (Exception ex) { Log.Error("Actualizar hooks de Claude Code", ex); }
        }

        /// <summary>
        /// Eventos que se escuchan: Stop y Notification avisan; UserPromptSubmit (una vez por mensaje, no por herramienta)
        /// muestra la sesión en el notch en cuanto empieza.
        /// </summary>
        private static readonly string[] HookEvents = { "Stop", "Notification", "UserPromptSubmit" };

        private static bool HasAllEvents(string settingsText)
        {
            try
            {
                var hooks = JsonNode.Parse(settingsText)?["hooks"] as JsonObject;
                return HookEvents.All(ev => hooks?[ev]?.ToJsonString().Contains("--claude-event") == true);
            }
            catch { return true; } // si no se puede leer, no se toca
        }

        public static bool IsClaudeIntegrationInstalled()
        {
            try { return File.Exists(ClaudeSettingsPath) && File.ReadAllText(ClaudeSettingsPath).Contains("--claude-event"); }
            catch { return false; }
        }

        /// <summary>Añade (o quita) los hooks de <see cref="HookEvents"/> en ~/.claude/settings.json, con copia de seguridad.</summary>
        /// <param name="exe">Ejecutable al que llaman los hooks (por defecto este; el instalador pasa el instalado).</param>
        public static void SetClaudeIntegration(bool install, string? exe = null)
        {
            var path = ClaudeSettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            JsonObject root = new();
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                File.Copy(path, path + ".coucou-backup", overwrite: true);
                if (!string.IsNullOrWhiteSpace(text)) root = JsonNode.Parse(text) as JsonObject ?? new JsonObject();
            }
            var hooks = root["hooks"] as JsonObject ?? new JsonObject();
            root["hooks"] = hooks;

            foreach (var ev in HookEvents)
            {
                var arr = hooks[ev] as JsonArray ?? new JsonArray();
                // Quita entradas previas de Agent Manager Notch
                for (int i = arr.Count - 1; i >= 0; i--)
                {
                    if (arr[i]?.ToJsonString().Contains("--claude-event") == true) arr.RemoveAt(i);
                }
                if (install)
                {
                    arr.Add(new JsonObject
                    {
                        ["matcher"] = "",
                        ["hooks"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "command",
                            ["command"] = HookCommand(exe),
                            ["timeout"] = 5
                        })
                    });
                }
                if (arr.Count > 0) hooks[ev] = arr; else hooks.Remove(ev);
            }
            if (hooks.Count == 0) root.Remove("hooks");
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
