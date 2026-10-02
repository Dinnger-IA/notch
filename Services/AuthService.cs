using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace AgentManagerNotch.Services
{
    /// <summary>
    /// Inicio de sesión con Agent Manager. El navegador abre /external/token?client=notch, el Agent Manager
    /// redirige a http://localhost:5454/api/callback?token=…&amp;state=… y aquí se valida el token con
    /// /api/auth/me. El token se guarda cifrado con DPAPI (solo lo puede leer este usuario de Windows).
    /// </summary>
    public sealed class AuthService
    {
        public const string BaseUrl = "https://agent-manager.distelsa.net";
        public const int CallbackPort = 5454;
        public static readonly string CallbackUrl = $"http://localhost:{CallbackPort}/api/callback";
        private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);
        private static readonly string TokenPath = Path.Combine(ConfigStore.Root, "auth.bin");
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AgentManagerNotch.Auth");
        private static readonly HttpClient Http = new() { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(15) };

        private string? _token;
        private CancellationTokenSource? _loginCts;

        public bool IsAuthenticated => _token != null;
        public string? UserName { get; private set; }
        public bool IsLoggingIn => _loginCts != null;

        /// <summary>Cambió la sesión (inició o se cerró). Siempre en el hilo de la interfaz.</summary>
        public event Action? Changed;

        private enum Check { Valid, Rejected, Unreachable }

        // =============================================================== arranque
        /// <summary>Recupera el token guardado y lo valida. Sin red se confía en él para no bloquear sin conexión.</summary>
        public async Task RestoreAsync()
        {
            var token = LoadToken();
            if (token == null) return;
            var (check, user) = await ValidateAsync(token);
            if (check == Check.Rejected)
            {
                Log.Info("Sesión guardada caducada: hay que volver a iniciar sesión");
                DeleteToken();
                return;
            }
            _token = token;
            UserName = user ?? UserName;
            Log.Info(check == Check.Valid ? $"Sesión restaurada ({UserName})" : "Sesión restaurada sin validar (Agent Manager no responde)");
            RaiseChanged();
        }

        // =============================================================== login
        /// <summary>
        /// Abre el navegador y espera el callback. Devuelve null si todo fue bien, o el mensaje de error
        /// para mostrar (también null si se canceló).
        /// </summary>
        public async Task<string?> LoginAsync()
        {
            CancelLogin();
            var cts = new CancellationTokenSource(LoginTimeout);
            _loginCts = cts;
            var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{CallbackPort}/");
            try
            {
                try { listener.Start(); }
                catch (HttpListenerException ex)
                {
                    Log.Error("No se pudo abrir el puerto del callback", ex);
                    return $"El puerto {CallbackPort} está en uso por otra aplicación.";
                }

                var url = $"{BaseUrl}/external/token?client=notch&state={state}&redirect_uri={Uri.EscapeDataString(CallbackUrl)}";
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Exception ex) { Log.Error("No se pudo abrir el navegador", ex); return "No se pudo abrir el navegador."; }

                using var reg = cts.Token.Register(() => { try { listener.Stop(); } catch { } });
                while (true)
                {
                    HttpListenerContext ctx;
                    try { ctx = await listener.GetContextAsync(); }
                    catch when (cts.IsCancellationRequested)
                    {
                        return _loginCts == cts ? "Se agotó el tiempo de espera. Vuelve a intentarlo." : null;
                    }

                    if (!string.Equals(ctx.Request.Url?.AbsolutePath.TrimEnd('/'), "/api/callback", StringComparison.OrdinalIgnoreCase))
                    {
                        Respond(ctx, 404, "No encontrado", "Esta dirección solo recibe el inicio de sesión de Agent Manager Notch.");
                        continue;
                    }

                    var q = ctx.Request.QueryString;
                    var token = q["token"];
                    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(q["state"] ?? ""), Encoding.UTF8.GetBytes(state)))
                    {
                        Log.Info("Callback de login con state inválido: ignorado");
                        Respond(ctx, 400, "Enlace no válido", "Este inicio de sesión no lo pidió Agent Manager Notch. Vuelve a intentarlo desde el notch.");
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        Respond(ctx, 400, "Falta el token", "Agent Manager no devolvió un token. Vuelve a intentarlo desde el notch.");
                        return "Agent Manager no devolvió un token.";
                    }

                    var (check, user) = await ValidateAsync(token);
                    if (check != Check.Valid)
                    {
                        var msg = check == Check.Rejected ? "Agent Manager rechazó el token." : "No se pudo verificar el token con Agent Manager.";
                        Respond(ctx, 401, "No se pudo iniciar sesión", msg);
                        return msg;
                    }

                    _token = token;
                    UserName = user;
                    SaveToken(token);
                    Respond(ctx, 200, "Sesión iniciada", $"Sesión iniciada{(user != null ? " como " + user : "")}. Ya puedes cerrar esta pestaña y volver al notch.");
                    Log.Info($"Sesión iniciada ({user})");
                    RaiseChanged();
                    return null;
                }
            }
            finally
            {
                try { listener.Close(); } catch { }
                if (_loginCts == cts) _loginCts = null;
                cts.Dispose();
            }
        }

        public void CancelLogin()
        {
            var cts = _loginCts;
            _loginCts = null;
            try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        }

        // =============================================================== logout
        public async Task LogoutAsync()
        {
            var token = _token;
            _token = null;
            UserName = null;
            DeleteToken();
            Log.Info("Sesión cerrada");
            RaiseChanged();
            if (token == null) return;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var _ = await Http.SendAsync(req);
            }
            catch (Exception ex) { Log.Error("No se pudo cerrar la sesión en Agent Manager", ex); }
        }

        // =============================================================== utilidades
        private static async Task<(Check, string?)> ValidateAsync(string token)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var res = await Http.SendAsync(req);
                if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return (Check.Rejected, null);
                if (!res.IsSuccessStatusCode) return (Check.Unreachable, null);
                string? user = null;
                try
                {
                    using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                    var root = doc.RootElement;
                    if (root.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object) root = u;
                    foreach (var name in new[] { "username", "name", "email" })
                        if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String) { user = v.GetString(); break; }
                }
                catch (JsonException) { }
                return (Check.Valid, user);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Log.Error("Agent Manager no responde al validar la sesión", ex);
                return (Check.Unreachable, null);
            }
        }

        private static void Respond(HttpListenerContext ctx, int status, string title, string message)
        {
            try
            {
                var ok = status == 200;
                var html = $@"<!doctype html><html lang=""es""><head><meta charset=""utf-8""><title>Agent Manager Notch</title>
<style>body{{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;background:#0c0c10;color:#ececf2;font-family:'Segoe UI',sans-serif}}
.card{{background:#16152a;border-radius:20px;padding:36px 40px;max-width:420px;text-align:center;box-shadow:0 10px 40px #0008}}
.icon{{font-size:40px;margin-bottom:8px}}h1{{font-size:20px;margin:0 0 10px}}p{{color:#9a9aa8;font-size:14px;line-height:1.5;margin:0}}</style></head>
<body><div class=""card""><div class=""icon"">{(ok ? "✅" : "⚠️")}</div><h1>{WebUtility.HtmlEncode(title)}</h1><p>{WebUtility.HtmlEncode(message)}</p></div></body></html>";
                var bytes = Encoding.UTF8.GetBytes(html);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes);
                ctx.Response.Close();
            }
            catch (Exception ex) { Log.Error("No se pudo responder al navegador", ex); }
        }

        private void RaiseChanged()
        {
            var d = Application.Current?.Dispatcher;
            if (d == null || d.CheckAccess()) Changed?.Invoke();
            else d.BeginInvoke(() => Changed?.Invoke());
        }

        private static string? LoadToken()
        {
            try
            {
                if (!File.Exists(TokenPath)) return null;
                var data = ProtectedData.Unprotect(File.ReadAllBytes(TokenPath), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch (Exception ex)
            {
                Log.Error("No se pudo leer la sesión guardada", ex);
                DeleteToken();
                return null;
            }
        }

        private static void SaveToken(string token)
        {
            try
            {
                Directory.CreateDirectory(ConfigStore.Root);
                File.WriteAllBytes(TokenPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) { Log.Error("No se pudo guardar la sesión", ex); }
        }

        private static void DeleteToken()
        {
            try { File.Delete(TokenPath); } catch { }
        }
    }
}
