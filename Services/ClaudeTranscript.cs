using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AgentManagerNotch.Models;
using AgentManagerNotch.Providers;

namespace AgentManagerNotch.Services
{
    /// <summary>
    /// Lee la conversación de una sesión de Claude Code abierta en la terminal (el <c>transcript_path</c> que manda su
    /// hook, un JSONL en ~/.claude/projects) y la convierte en mensajes del chat: lo que escribió el usuario, las
    /// respuestas y las acciones (herramientas), sin razonamientos, resultados de herramientas ni entradas internas.
    /// </summary>
    public static class ClaudeTranscript
    {
        /// <summary>
        /// Mensajes de la sesión (los últimos <paramref name="max"/>), o null si no se pudo leer. Por debajo del límite
        /// del historial (300) para que quepan el aviso de la pestaña y lo que se siga escribiendo.
        /// </summary>
        public static List<ChatMessage>? Read(string path, int max = 250)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var list = new List<ChatMessage>();
            try
            {
                // Claude Code puede estar escribiendo a la vez: se lee sin bloquear el archivo
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    try { AddEntry(list, line); }
                    catch (JsonException) { } // última línea a medio escribir
                }
            }
            catch (Exception ex)
            {
                Log.Error("Leer la sesión de Claude Code de la terminal", ex);
                return null;
            }
            return list.Count > max ? list.Skip(list.Count - max).ToList() : list;
        }

        private static void AddEntry(List<ChatMessage> list, string line)
        {
            using var doc = JsonDocument.Parse(line);
            var o = doc.RootElement;
            var type = Str(o, "type");
            if (type is not ("user" or "assistant")) return;
            if (Bool(o, "isSidechain") || Bool(o, "isMeta")) return; // subagentes y mensajes internos
            if (!o.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content)) return;
            var time = o.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTime(out var t) ? t.ToLocalTime() : DateTime.Now;

            void Add(ChatRole role, string text)
            {
                text = text.Trim();
                if (text.Length > 0) list.Add(new ChatMessage(role, text) { Time = time });
            }

            if (content.ValueKind == JsonValueKind.String)
            {
                var text = content.GetString() ?? "";
                if (type == "user" && !IsInternal(text)) Add(ChatRole.User, text);
                else if (type == "assistant") Add(ChatRole.Agent, text);
                return;
            }
            if (content.ValueKind != JsonValueKind.Array) return;
            foreach (var block in content.EnumerateArray())
            {
                switch (Str(block, "type"))
                {
                    case "text":
                        var text = Str(block, "text");
                        if (type == "assistant") Add(ChatRole.Agent, text);
                        else if (!IsInternal(text)) Add(ChatRole.User, text);
                        break;
                    case "tool_use" when type == "assistant":
                        var name = Str(block, "name");
                        JsonElement? input = block.TryGetProperty("input", out var i) ? i : null;
                        Add(ChatRole.Tool, $"{name}  {ProviderFactory.SummarizeInput(input)}");
                        break;
                }
            }
        }

        /// <summary>Lo que Claude Code mete como si lo dijera el usuario: comandos, su salida, recordatorios del sistema…</summary>
        private static bool IsInternal(string text)
        {
            var t = text.TrimStart();
            return t.StartsWith('<') || t.StartsWith("Caveat:", StringComparison.Ordinal) || t.StartsWith("[Request interrupted", StringComparison.Ordinal);
        }

        private static string Str(JsonElement e, string key) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        private static bool Bool(JsonElement e, string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
    }
}
