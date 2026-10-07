using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentManagerNotch.Models;
using AgentManagerNotch.Plugins;

namespace AgentManagerNotch.Services
{
    /// <summary>Guarda configuración e historial en %APPDATA%\AgentManagerNotch.</summary>
    public class ConfigStore
    {
        public static readonly string Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentManagerNotch");
        /// <summary>Carpeta de datos del nombre anterior del proyecto (se migra una vez).</summary>
        public static readonly string LegacyRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Coucou" + "Win");
        private static readonly string ConfigPath = Path.Combine(Root, "config.json");
        private static readonly string HistoryDir = Path.Combine(Root, "history");

        public static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            PropertyNameCaseInsensitive = true
        };

        public AppConfig Config { get; private set; } = new();

        public void Load()
        {
            MigrateLegacyRoot();
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(HistoryDir);
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var node = JsonNode.Parse(File.ReadAllText(ConfigPath));
                    MovePluginProviders(node);
                    Config = node.Deserialize<AppConfig>(Json) ?? new AppConfig();
                }
            }
            catch (Exception ex)
            {
                Log.Error("No se pudo leer config.json", ex);
                // Respaldo con fecha: un segundo fallo no debe pisar el respaldo del primero
                try { File.Copy(ConfigPath, $"{ConfigPath}.{DateTime.Now:yyyyMMdd-HHmmss}.bak", false); } catch { }
                Config = new AppConfig();
            }
            if (Config.Agents.Count == 0)
            {
                // Primera configuración: los agentes iniciales (los plugins pueden cambiarlos)
                var agents = DefaultAgents().ToList();
                PluginHost.ConfigureDefaultAgents(agents, Config.Settings);
                Config.Agents.AddRange(agents);
                if (agents.Count > 0) Config.Settings.DefaultAgentId = agents[0].Id;
                Config.Settings.SeededCodeAgent = true;
            }
        }

        /// <summary>
        /// Agentes guardados con un proveedor que no es un CLI conocido (p. ej. «"Provider": "MiPlugin"»): pasan a
        /// Provider=Plugin con ese id, para que la configuración se lea aunque esta compilación no traiga el plugin.
        /// </summary>
        private static void MovePluginProviders(JsonNode? root)
        {
            if (root?["Agents"] is not JsonArray agents) return;
            foreach (var a in agents.OfType<JsonObject>())
            {
                if (a["Provider"] is not JsonValue v || !v.TryGetValue<string>(out var name)) continue;
                if (Enum.TryParse<ProviderKind>(name, true, out _)) continue;
                a["PluginProvider"] = name;
                a["Provider"] = nameof(ProviderKind.Plugin);
            }
        }

        /// <summary>Copia los datos de la carpeta del nombre anterior (la original queda como respaldo).</summary>
        private static void MigrateLegacyRoot()
        {
            try
            {
                if (Directory.Exists(Root) || !Directory.Exists(LegacyRoot)) return;
                foreach (var dir in Directory.GetDirectories(LegacyRoot, "*", SearchOption.AllDirectories))
                    Directory.CreateDirectory(dir.Replace(LegacyRoot, Root));
                foreach (var file in Directory.GetFiles(LegacyRoot, "*", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(file);
                    if (name is "claude-hook-settings.json") continue; // se regenera con la ruta nueva
                    File.Copy(file, file.Replace(LegacyRoot, Root), overwrite: false);
                }
                Log.Info($"Datos migrados desde {LegacyRoot}");
            }
            catch (Exception ex) { Log.Error("Migración de datos", ex); }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Root);
                var tmp = ConfigPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Config, Json));
                File.Move(tmp, ConfigPath, true);
            }
            catch (Exception ex) { Log.Error("No se pudo guardar config.json", ex); }
        }

        public AgentHistory LoadHistory(string agentId)
        {
            try
            {
                var p = Path.Combine(HistoryDir, agentId + ".json");
                if (File.Exists(p))
                    return JsonSerializer.Deserialize<AgentHistory>(File.ReadAllText(p), Json) ?? new AgentHistory();
            }
            catch (Exception ex) { Log.Error("Historial dañado " + agentId, ex); }
            return new AgentHistory();
        }

        public void SaveHistory(string agentId, AgentHistory h)
        {
            try
            {
                Directory.CreateDirectory(HistoryDir);
                if (h.Messages.Count > 300) h.Messages = h.Messages.Skip(h.Messages.Count - 300).ToList();
                File.WriteAllText(Path.Combine(HistoryDir, agentId + ".json"), JsonSerializer.Serialize(h, Json));
            }
            catch (Exception ex) { Log.Error("No se pudo guardar historial", ex); }
        }

        public void DeleteHistory(string agentId)
        {
            try { File.Delete(Path.Combine(HistoryDir, agentId + ".json")); } catch { }
        }

        public static readonly string[] CodePalette = { "#FF9E5E", "#64B5F6", "#4DD0A1", "#BA68C8", "#FFD54F", "#F06292", "#4DD0E1", "#AED581" };

        public const string CodePrompt =
            "Eres un ingeniero de software senior que trabaja en esta carpeta. Lees el código antes de cambiarlo, " +
            "haces cambios pequeños y verificables, ejecutas las pruebas cuando existen y explicas en pocas líneas qué hiciste. " +
            "Respondes en español.";

        public static AgentProfile NewCodeAgent(string? folder, string name, string color) => new()
        {
            Name = name,
            ColorHex = color,
            Kind = AgentKind.Code,
            Provider = ProviderKind.ClaudeCode,
            Approval = ApprovalMode.Ask,
            WorkingDirectory = folder ?? "",
            SystemPrompt = CodePrompt
        };

        public static AgentProfile[] DefaultAgents() => new[]
        {
            NewCodeAgent(null, "Código", "#FF9E5E"),
            new AgentProfile
            {
                Name = "Mochi", ColorHex = "#FF8A65", Provider = ProviderKind.ClaudeCode,
                SystemPrompt = "Eres Mochi, un asistente amable y conciso que vive en la parte superior de la pantalla de Windows. Respondes en español, de forma breve y clara. Ayudas con tareas de programación, preguntas rápidas y recordatorios."
            },
            new AgentProfile
            {
                Name = "Codi", ColorHex = "#4DD0A1", Provider = ProviderKind.Codex,
                SystemPrompt = "Eres Codi, un ingeniero de software senior. Escribes código limpio, explicas los cambios en pocas líneas y siempre verificas lo que haces.",
                Approval = ApprovalMode.AcceptEdits
            },
            new AgentProfile
            {
                Name = "Nube", ColorHex = "#9C8CFF", Provider = ProviderKind.ClaudeCode,
                SystemPrompt = "Eres Nube, un asistente personal de planificación. Ayudas a organizar el día, dividir tareas grandes en pasos y creas recordatorios cuando el usuario lo pide.",
                Approval = ApprovalMode.ReadOnly
            }
        };
    }

    public static class Log
    {
        private static readonly object Gate = new();
        public static void Info(string msg) => Write("INFO", msg);
        public static void Error(string msg, Exception? ex = null) => Write("ERROR", msg + (ex != null ? " :: " + ex : ""));
        private static void Write(string level, string msg)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(ConfigStore.Root);
                    File.AppendAllText(Path.Combine(ConfigStore.Root, "agent-manager-notch.log"),
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }
}
