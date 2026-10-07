using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AgentManagerNotch.Services;

namespace AgentManagerNotch.Plugins.AgentManager
{
    /// <summary>
    /// Comprueba, antes de instalar con Agent Manager, que este equipo puede usarlo: que encuentra el servidor (red de la
    /// empresa o VPN), que responde por HTTPS con un certificado válido y que el puerto local del inicio de sesión está
    /// libre. Cada paso dice qué falló y qué hacer.
    /// </summary>
    public static class AgentManagerCheck
    {
        public record Step(string Name, bool Ok, string Detail);

        public static async Task<List<Step>> RunAsync()
        {
            var steps = new List<Step>();
            var host = new Uri(AuthService.BaseUrl).Host;

            // 1. Nombre del servidor
            try
            {
                await Dns.GetHostAddressesAsync(host).WaitAsync(TimeSpan.FromSeconds(8));
                steps.Add(new Step("Red", true, $"{host} encontrado"));
            }
            catch (Exception ex)
            {
                Log.Error("Comprobar Agent Manager: DNS", ex);
                steps.Add(new Step("Red", false,
                    $"No se encuentra {host}. Conéctate a la red de la empresa o a la VPN y vuelve a comprobar."));
                return steps;
            }

            // 2. El servidor responde (sin token, lo esperado es 401: está vivo y pide sesión)
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var res = await http.GetAsync($"{AuthService.BaseUrl}/api/auth/me");
                int code = (int)res.StatusCode;
                steps.Add(code >= 500
                    ? new Step("Servidor", false, $"Agent Manager respondió con un error ({code} {res.ReasonPhrase}). Inténtalo más tarde o avisa al equipo de Agent Manager.")
                    : new Step("Servidor", true, $"Agent Manager responde ({code})"));
            }
            catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
            {
                Log.Error("Comprobar Agent Manager: TLS", ex);
                steps.Add(new Step("Servidor", false,
                    "El certificado de Agent Manager no es de confianza en este equipo (¿un proxy o antivirus intercepta HTTPS?)."));
            }
            catch (TaskCanceledException)
            {
                steps.Add(new Step("Servidor", false, "Agent Manager no respondió en 15 segundos. Revisa la conexión o la VPN y vuelve a comprobar."));
            }
            catch (Exception ex)
            {
                Log.Error("Comprobar Agent Manager: conexión", ex);
                steps.Add(new Step("Servidor", false, $"No se pudo conectar con Agent Manager: {ex.Message}"));
            }

            // 3. Puerto del inicio de sesión (el navegador vuelve a http://localhost:5454 con el token)
            steps.Add(PortStep());
            return steps;
        }

        /// <summary>Lo que se marcó en el instalador: se guarda en config.json sin tocar el resto de la configuración.</summary>
        public static void SaveChoice(bool useAgentManager)
        {
            var path = Path.Combine(ConfigStore.Root, "config.json");
            Directory.CreateDirectory(ConfigStore.Root);
            JsonObject root;
            try { root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject() : new JsonObject(); }
            catch (Exception ex) { Log.Error("Instalador: config.json ilegible, se guarda solo la opción de Agent Manager", ex); return; }
            if (root["Settings"] is not JsonObject settings) root["Settings"] = settings = new JsonObject();
            settings["UseAgentManager"] = useAgentManager;
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        private static Step PortStep()
        {
            int port = AuthService.CallbackPort;
            try
            {
                foreach (var ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                    if (ep.Port == port)
                        return new Step("Inicio de sesión", false,
                            $"Otra aplicación usa el puerto {port}, que hace falta para iniciar sesión. Ciérrala y vuelve a comprobar.");
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Start();
                listener.Stop();
                return new Step("Inicio de sesión", true, $"Puerto {port} libre");
            }
            catch (Exception ex) when (ex is HttpListenerException or SocketException)
            {
                return new Step("Inicio de sesión", false, $"No se pudo abrir el puerto {port} para el inicio de sesión: {ex.Message}");
            }
        }
    }
}
