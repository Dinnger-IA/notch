using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoucouWin.Models;

namespace CoucouWin.Services
{
    /// <summary>Guarda configuración e historial en %APPDATA%\CoucouWin.</summary>
    public class ConfigStore
    {
        public static readonly string Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CoucouWin");
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
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(HistoryDir);
            try
            {
                if (File.Exists(ConfigPath))
                    Config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), Json) ?? new AppConfig();
            }
            catch (Exception ex)
            {
                Log.Error("No se pudo leer config.json", ex);
                try { File.Copy(ConfigPath, ConfigPath + ".bak", true); } catch { }
                Config = new AppConfig();
            }
            if (Config.Agents.Count == 0) Config.Agents.AddRange(DefaultAgents());
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
                    File.AppendAllText(Path.Combine(ConfigStore.Root, "coucou.log"),
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }
}
