using System;
using System.Collections.Generic;
using System.Linq;
using AgentManagerNotch.Models;
using AgentManagerNotch.Providers;
using AgentManagerNotch.Services;

namespace AgentManagerNotch.Plugins
{
    /// <summary>
    /// Carga los plugins (las clases de este ejecutable que implementan <see cref="INotchPlugin"/>) y reparte las
    /// llamadas entre ellos. Sin plugins, el notch funciona exactamente igual: cada punto de extensión queda vacío.
    /// </summary>
    public static class PluginHost
    {
        private static List<INotchPlugin>? _plugins;
        private static readonly PluginRegistry Registry = new();

        public static IReadOnlyList<INotchPlugin> Plugins => _plugins ??= Load();
        public static IReadOnlyList<PluginProvider> Providers { get { _ = Plugins; return Registry.Providers; } }
        public static IReadOnlyList<GifDemo> GifDemos { get { _ = Plugins; return Registry.GifDemos; } }
        /// <summary>El de la sesión normal (null en el instalador y en los modos de línea de comandos).</summary>
        public static NotchHost? Host { get; private set; }

        private static List<INotchPlugin> Load()
        {
            var list = new List<INotchPlugin>();
            foreach (var t in typeof(PluginHost).Assembly.GetTypes()
                         .Where(t => typeof(INotchPlugin).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false } && t.GetConstructor(Type.EmptyTypes) != null))
            {
                try { list.Add((INotchPlugin)Activator.CreateInstance(t)!); }
                catch (Exception ex) { Log.Error($"Plugin {t.Name}: no se pudo crear", ex); }
            }
            list.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.CompareOrdinal(a.Id, b.Id));
            foreach (var p in list)
            {
                Registry.Owner = p.Id;
                Safe($"{p.Id}.Register", () => p.Register(Registry));
            }
            Registry.Owner = null;
            return list;
        }

        public static PluginProvider? Provider(string? id) =>
            id == null ? null : Providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>Modos de línea de comandos de los plugins (el primero que lo atienda).</summary>
        public static int? RunCommandLine(string[] args)
        {
            foreach (var p in Plugins)
            {
                int? code = null;
                Safe($"{p.Id}.RunCommandLine", () => code = p.RunCommandLine(args));
                if (code != null) return code;
            }
            return null;
        }

        public static int? BeforeSilentInstall(string[] args)
        {
            foreach (var p in Plugins)
            {
                int? code = null;
                Safe($"{p.Id}.BeforeSilentInstall", () => code = p.BeforeSilentInstall(args));
                if (code != null) return code;
            }
            return null;
        }

        public static void ConfigureDefaultAgents(List<AgentProfile> agents, AppSettings settings)
        {
            foreach (var p in Plugins) Safe($"{p.Id}.ConfigureDefaultAgents", () => p.ConfigureDefaultAgents(agents, settings));
        }

        public static void ConfigureInstaller(IInstallerHost installer)
        {
            foreach (var p in Plugins) Safe($"{p.Id}.ConfigureInstaller", () => p.ConfigureInstaller(installer));
        }

        internal static NotchHost Start(App app)
        {
            Host = new NotchHost(app);
            foreach (var p in Plugins)
            {
                Safe($"{p.Id}.Start", () => p.Start(Host));
                Log.Info($"Plugin cargado: {p.Name} ({p.Id})");
            }
            return Host;
        }

        public static void Stop()
        {
            foreach (var p in Plugins) Safe($"{p.Id}.Stop", p.Stop);
        }

        /// <summary>Ejecuta código de un plugin sin que un fallo tumbe el notch.</summary>
        internal static void Safe(string what, Action action)
        {
            try { action(); }
            catch (Exception ex) { Log.Error($"Plugin: {what}", ex); }
        }

        /// <summary>Proveedor que falta (un agente guardado con un plugin que esta compilación no trae).</summary>
        internal sealed class MissingProvider(string id) : ICliProvider
        {
            public bool PlainText => true;
            public LaunchSpec Build(AgentProfile p, string prompt, string? sessionId, RunContext ctx) =>
                throw new InvalidOperationException($"Este agente usa «{id}», que no está disponible en esta versión de Agent Manager Notch. Elige otro CLI en el editor del agente.");
            public IEnumerable<AgentEvent> Parse(string line) => [];
        }
    }
}
