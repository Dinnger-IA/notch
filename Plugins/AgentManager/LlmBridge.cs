using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentManagerNotch.Services;

namespace AgentManagerNotch.Plugins.AgentManager
{
    /// <summary>
    /// Modo <c>--llm</c>: puente entre un agente con proveedor «Agent Manager» y el ruteador de Agent Manager
    /// (POST /api/notch/notch-agent con {"messages":[…], "stream": true}). Pide streaming SSE; si el servidor aún
    /// responde JSON entero ({success, data:{conversationId, response}}) también lo entiende. El lector SSE acepta
    /// fragmentos estilo OpenAI (choices[0].delta.content) o campos delta/text/content/token, y una respuesta
    /// completa final (data.response) sin duplicar lo ya recibido.
    /// stdin: {"messages":[{role,content}…], "attachments":[{name,data}…]} (attachments, opcional: archivos en base64,
    /// ver AgentManagerProvider). stdout: una línea JSON por evento:
    /// {"type":"delta","text":…}, {"type":"tool","tool":…,"detail":…} (las acciones del ruteador y de los agentes en los
    /// que delega, para el panel de acciones) y al final {"type":"result","ok":true|false,"error":…}.
    /// Usa el token de la sesión guardada (DPAPI), así funciona igual que los demás CLI: un proceso que se puede detener.
    /// </summary>
    public static class LlmBridge
    {
        public const string Endpoint = AuthService.BaseUrl + "/api/notch/notch-agent";

        public static int Run()
        {
            using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            void Emit(object o) => stdout.WriteLine(JsonSerializer.Serialize(o));
            int Fail(string error) { Emit(new { type = "result", ok = false, error }); return 1; }

            try
            {
                string input;
                using (var sr = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
                    input = sr.ReadToEnd();
                var body = JsonNode.Parse(input) as JsonObject ?? throw new InvalidDataException("Entrada vacía");
                body["stream"] = true;

                var token = AuthService.LoadSavedToken();
                if (token == null) return Fail("No hay sesión de Agent Manager. Inicia sesión en el notch.");

                using var http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
                // AMN_LLM_ENDPOINT: solo para desarrollo (probar contra un servidor local)
                var url = Environment.GetEnvironmentVariable("AMN_LLM_ENDPOINT") is { Length: > 0 } dev ? dev : Endpoint;
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
                };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.Accept.ParseAdd("text/event-stream");
                req.Headers.Accept.ParseAdd("application/json");
                using var res = http.Send(req, HttpCompletionOption.ResponseHeadersRead);

                if (!res.IsSuccessStatusCode)
                {
                    var detail = ReadError(res);
                    return Fail(res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? "La sesión de Agent Manager no es válida. Vuelve a iniciar sesión en el notch."
                        : $"Agent Manager respondió {(int)res.StatusCode}{(detail != "" ? ": " + detail : "")}");
                }

                using var stream = res.Content.ReadAsStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                // Respuesta JSON (el ruteador responde entero, sin streaming)
                if (res.Content.Headers.ContentType?.MediaType?.Contains("json") == true)
                {
                    var json = JsonNode.Parse(reader.ReadToEnd());
                    if (json?["success"]?.GetValueKind() == JsonValueKind.False)
                        return Fail(json["error"]?.ToString() ?? "Agent Manager no pudo responder.");
                    var text = json?["data"]?["response"]?.ToString() ?? Content(json, "message");
                    if (string.IsNullOrEmpty(text)) return Fail("Agent Manager respondió sin contenido.");
                    Emit(new { type = "delta", text });
                    Emit(new { type = "result", ok = true });
                    return 0;
                }

                // Streaming SSE
                var received = new StringBuilder();
                var toolNames = new Dictionary<string, string>(); // callId → herramienta, para avisar si falla
                var agentNames = new Dictionary<string, string>(); // agentId → nombre del agente en el que delegó
                void Append(string? text)
                {
                    if (string.IsNullOrEmpty(text)) return;
                    received.Append(text);
                    Emit(new { type = "delta", text });
                }
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!line.StartsWith("data:")) continue;
                    var data = line[5..].TrimStart();
                    if (data.Trim() == "[DONE]") break;
                    if (data.Trim() == "") continue;
                    JsonNode? chunk;
                    try { chunk = JsonNode.Parse(data); }
                    catch (JsonException) { Append(data); continue; } // texto plano
                    if (chunk is not JsonObject) { Append(chunk?.ToString()); continue; }
                    if (Action(chunk, toolNames, agentNames) is { } action) { Emit(action); continue; }
                    if (Str(chunk, "type") is "tool_result" or "agent_end" or "title") continue;
                    if (chunk["success"]?.GetValueKind() == JsonValueKind.False || chunk["error"] is JsonNode)
                    {
                        var err = chunk["error"];
                        return Fail((err is JsonObject eo ? eo["message"]?.ToString() : null) ?? err?.ToString() ?? "Agent Manager no pudo responder.");
                    }
                    var delta = Content(chunk, "delta") ?? Str(chunk, "delta") ?? Str(chunk, "text") ?? Str(chunk, "content") ?? Str(chunk, "token");
                    if (delta != null) { Append(delta); continue; }
                    // Respuesta completa (p. ej. evento final): solo lo que falte por mostrar
                    var full = Str(chunk["data"], "response") ?? Str(chunk, "response") ?? Content(chunk, "message");
                    if (full == null) continue;
                    var sofar = received.ToString();
                    if (sofar == "") Append(full);
                    else if (full.StartsWith(sofar, StringComparison.Ordinal)) Append(full[sofar.Length..]);
                }
                if (received.Length == 0) return Fail("Agent Manager respondió sin contenido.");
                Emit(new { type = "result", ok = true });
                return 0;
            }
            catch (Exception ex)
            {
                return Fail(ex is HttpRequestException ? "No se pudo conectar con Agent Manager: " + ex.Message : ex.Message);
            }
        }

        /// <summary>
        /// Acciones del ruteador (eventos tool, agent_start y tool_result fallido) como evento «tool» del notch. Las
        /// herramientas no traen sus argumentos: se muestra el nombre y, si es de un agente en el que delegó, cuál.
        /// </summary>
        private static object? Action(JsonNode chunk, Dictionary<string, string> toolNames, Dictionary<string, string> agentNames)
        {
            var callId = Str(chunk, "callId") ?? "";
            var agent = Str(chunk, "agentId") is { } id && agentNames.TryGetValue(id, out var n) ? n : "";
            switch (Str(chunk, "type"))
            {
                case "agent_start":
                    var name = Str(chunk, "name") ?? Str(chunk, "slug") ?? "agente";
                    if (Str(chunk, "agentId") is { } aid) agentNames[aid] = name;
                    var instr = (Str(chunk, "instruction") ?? "").ReplaceLineEndings(" ");
                    return new { type = "tool", tool = "Agente " + name, detail = instr.Length > 140 ? instr[..140] + "…" : instr };
                case "tool":
                    var tool = ToolLabel(Str(chunk, "name") ?? "herramienta");
                    toolNames[callId] = tool;
                    return new { type = "tool", tool = agent != "" ? $"{agent} › {tool}" : tool, detail = "" };
                case "tool_result" when Str(chunk, "status") is { } st && st != "completed":
                    return new { type = "tool", tool = toolNames.GetValueOrDefault(callId, "herramienta"), detail = "✗ " + (Str(chunk, "error") ?? st) };
                default:
                    return null;
            }
        }

        /// <summary>«mcp__clarify-2__project_search» → «clarify-2 · project_search».</summary>
        private static string ToolLabel(string name)
        {
            if (!name.StartsWith("mcp__")) return name;
            var parts = name[5..].Split("__", 2);
            return parts.Length == 2 ? $"{parts[0]} · {parts[1]}" : parts[0];
        }

        /// <summary>Propiedad de texto (solo si es string).</summary>
        private static string? Str(JsonNode? n, string name) =>
            n is JsonObject o && o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        /// <summary>choices[0].{delta|message}.content</summary>
        private static string? Content(JsonNode? n, string part)
        {
            try { return n?["choices"]?[0]?[part]?["content"]?.GetValue<string>(); }
            catch (InvalidOperationException) { return null; }
        }

        private static string ReadError(HttpResponseMessage res)
        {
            try
            {
                var s = res.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim();
                try
                {
                    var n = JsonNode.Parse(s);
                    s = n?["error"] is JsonValue v ? v.ToString() : n?["error"]?["message"]?.ToString() ?? n?["message"]?.ToString() ?? s;
                }
                catch (JsonException) { }
                return s.Length > 300 ? s[..300] + "…" : s;
            }
            catch { return ""; }
        }
    }
}
