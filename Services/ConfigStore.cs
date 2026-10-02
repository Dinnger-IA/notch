using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentManagerNotch.Models;
using AgentManagerNotch.Providers;

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
                    Config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), Json) ?? new AppConfig();
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
                // Primera configuración: los agentes iniciales dependen de los CLI instalados
                Config.Agents.AddRange(DefaultAgents());
                Config.Settings.DefaultAgentId = Config.Agents[0].Id;
                Config.Settings.SeededCodeAgent = true;
                Log.Info("Primera configuración: " + string.Join(", ", Config.Agents.Select(a => $"{a.Name} ({a.Provider})")));
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

        /// <summary>Primer CLI instalado (Claude Code, Codex o Gemini), o null si no hay ninguno.</summary>
        public static ProviderKind? FirstInstalledCli() =>
            new[] { ProviderKind.ClaudeCode, ProviderKind.Codex, ProviderKind.Gemini }
                .Cast<ProviderKind?>()
                .FirstOrDefault(k => CliResolver.Find(ProviderFactory.Executable(k!.Value)) != null);

        /// <summary>
        /// Agentes iniciales. Con algún CLI instalado: Codi (código), Doky (chat con el LLM de Agent Manager) y
        /// Nube (chat), Codi y Nube con ese CLI. Sin CLI: Doky y Nube, los dos con el LLM de Agent Manager.
        /// </summary>
        public static List<AgentProfile> DefaultAgents()
        {
            var cli = FirstInstalledCli();
            var list = new List<AgentProfile>();
            if (cli is ProviderKind c)
            {
                var codi = NewCodeAgent(null, "Codi", "#4DD0A1");
                codi.Provider = c;
                list.Add(codi);
            }
            list.Add(new AgentProfile
            {
                Name = "Doky", ColorHex = "#64B5F6", Kind = AgentKind.Interactive, Provider = ProviderKind.AgentManager,
                Approval = ApprovalMode.ReadOnly,
                SystemPrompt = "Eres Doky, el asistente de Agent Manager. Respondes en español, de forma breve y clara. " +
                               "Ayudas con preguntas rápidas, redacción, ideas y recordatorios."
            });
            list.Add(new AgentProfile
            {
                Name = "Nube", ColorHex = "#9C8CFF", Kind = AgentKind.Interactive, Provider = cli ?? ProviderKind.AgentManager,
                Approval = ApprovalMode.ReadOnly,
                SystemPrompt = "Eres Nube, un asistente personal de planificación. Ayudas a organizar el día, dividir tareas grandes en pasos y creas recordatorios cuando el usuario lo pide."
            });
            return list;
        }
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
