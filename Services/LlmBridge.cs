using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentManagerNotch.Services
{
    /// <summary>
    /// Modo <c>--llm</c>: puente entre un agente con proveedor «Agent Manager» y el LLM de Agent Manager
    /// (POST /api/default/test-ia, contrato OpenAI Chat Completions con streaming SSE).
    /// stdin: {"messages":[{role,content}…]}. stdout: una línea JSON por evento:
    /// {"type":"delta","text":…} y al final {"type":"result","ok":true|false,"error":…}.
    /// Usa el token de la sesión guardada (DPAPI), así funciona igual que los demás CLI: un proceso que se puede detener.
    /// </summary>
    public static class LlmBridge
    {
        public const string Endpoint = AuthService.BaseUrl + "/api/default/test-ia";

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
                using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
                {
                    Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
                };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.Accept.ParseAdd("text/event-stream");
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
                // Sin streaming (respuesta JSON normal): se emite entero
                if (res.Content.Headers.ContentType?.MediaType?.Contains("json") == true)
                {
                    var text = Content(JsonNode.Parse(reader.ReadToEnd()), "message");
                    if (!string.IsNullOrEmpty(text)) Emit(new { type = "delta", text });
                    Emit(new { type = "result", ok = true });
                    return 0;
                }

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!line.StartsWith("data:")) continue;
                    var data = line[5..].Trim();
                    if (data == "[DONE]") break;
                    if (data == "") continue;
                    JsonNode? chunk;
                    try { chunk = JsonNode.Parse(data); } catch (JsonException) { continue; }
                    if (chunk?["error"] is JsonNode err)
                        return Fail(err["message"]?.GetValue<string>() ?? err.ToJsonString());
                    var text = Content(chunk, "delta");
                    if (!string.IsNullOrEmpty(text)) Emit(new { type = "delta", text });
                }
                Emit(new { type = "result", ok = true });
                return 0;
            }
            catch (Exception ex)
            {
                return Fail(ex is HttpRequestException ? "No se pudo conectar con Agent Manager: " + ex.Message : ex.Message);
            }
        }

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
