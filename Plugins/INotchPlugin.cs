using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using AgentManagerNotch.Models;
using AgentManagerNotch.Providers;

namespace AgentManagerNotch.Plugins
{
    /// <summary>
    /// Un plugin del notch. Basta con crear una clase pública que implemente esta interfaz (con constructor sin
    /// parámetros) en cualquier carpeta del proyecto, normalmente <c>Plugins\&lt;Nombre&gt;\</c>: <see cref="PluginHost"/>
    /// la encuentra al arrancar. Todos los métodos tienen una implementación vacía: implementa solo los que necesites.
    /// Un fallo dentro de un plugin se registra y no detiene el notch.
    /// </summary>
    public interface INotchPlugin
    {
        /// <summary>Identificador estable (sin espacios). Se usa en el registro y en config.json.</summary>
        string Id { get; }
        /// <summary>Nombre para mostrar.</summary>
        string Name { get; }
        /// <summary>Orden de carga (menor primero): decide el orden de sus etapas, tarjetas y botones.</summary>
        int Order => 0;

        /// <summary>
        /// Al cargar, antes de cualquier ventana (también en los modos de línea de comandos): proveedores de agentes,
        /// vistas de <c>--grabar-gif</c> y otros registros que no necesitan la interfaz.
        /// </summary>
        void Register(PluginRegistry registry) { }

        /// <summary>Modo de línea de comandos propio (p. ej. <c>--mi-modo</c>): devuelve el código de salida si lo atendió.</summary>
        int? RunCommandLine(string[] args) => null;

        /// <summary>Instalación silenciosa (<c>--silencioso</c>): devuelve un código de salida para cancelarla.</summary>
        int? BeforeSilentInstall(string[] args) => null;

        /// <summary>Primera configuración (no hay agentes): puede cambiar la lista de agentes iniciales.</summary>
        void ConfigureDefaultAgents(List<AgentProfile> agents, AppSettings settings) { }

        /// <summary>Ventana del instalador: etapas extra y pasos tras instalar.</summary>
        void ConfigureInstaller(IInstallerHost installer) { }

        /// <summary>Arranque normal, antes de crear el notch: puertas de acceso, tarjetas de configuración, chat…</summary>
        void Start(NotchHost host) { }

        /// <summary>Al salir de la aplicación.</summary>
        void Stop() { }
    }

    /// <summary>Registros que no dependen de la interfaz (ver <see cref="INotchPlugin.Register"/>).</summary>
    public sealed class PluginRegistry
    {
        internal readonly List<PluginProvider> Providers = new();
        internal readonly List<GifDemo> GifDemos = new();
        internal string? Owner;

        /// <summary>Un proveedor de agentes nuevo (aparece en el editor de agentes junto a los CLI).</summary>
        public void AddProvider(PluginProvider provider) => Providers.Add(provider with { Owner = Owner ?? "" });

        /// <summary>Una vista para <c>AgentManagerNotch.exe --grabar-gif &lt;nombre&gt; &lt;salida.gif&gt;</c>.</summary>
        /// <param name="record">Graba la vista en la ruta indicada y devuelve el número de fotogramas.</param>
        public void AddGifDemo(string name, string description, Func<string, Task<int>> record) =>
            GifDemos.Add(new GifDemo(name, description, record));
    }

    /// <summary>
    /// Proveedor de agentes aportado por un plugin. Los agentes que lo usan guardan
    /// <c>Provider = Plugin</c> y <c>PluginProvider = Id</c>.
    /// </summary>
    /// <param name="Id">Identificador estable: es lo que se guarda en config.json.</param>
    /// <param name="Label">Nombre en el editor y en el chat.</param>
    /// <param name="Create">Crea el proveedor para una ejecución.</param>
    public sealed record PluginProvider(string Id, string Label, Func<ICliProvider> Create)
    {
        /// <summary>Si se ofrece en el editor para agentes nuevos (los que ya lo usan lo ven siempre).</summary>
        public Func<bool> IsAvailable { get; init; } = () => true;
        /// <summary>Texto bajo el selector del editor (en lugar de «Encontrado: …»).</summary>
        public string StatusText { get; init; } = "";
        /// <summary>Modelos que se sugieren en el editor ("" = el predeterminado).</summary>
        public string[] Models { get; init; } = [""];
        internal string Owner { get; init; } = "";
    }

    public sealed record GifDemo(string Name, string Description, Func<string, Task<int>> Record);

    /// <summary>
    /// Puerta de acceso: mientras esté cerrada, el notch solo muestra su vista (p. ej. un inicio de sesión) en lugar
    /// de los agentes. Se añade con <see cref="NotchHost.AddAccessGate"/>.
    /// </summary>
    public interface IAccessGate
    {
        bool IsOpen { get; }
        /// <summary>Se abrió o se cerró (puede lanzarse desde cualquier hilo).</summary>
        event Action? Changed;
        /// <summary>Título de la barra superior mientras se muestra.</summary>
        string Title { get; }
        /// <summary>Texto de la píldora cerrada (junto al candado).</summary>
        string LockedLabel { get; }
        /// <summary>Alto del panel.</summary>
        double Height => 200;
        /// <summary>Contenido del panel (se crea una vez).</summary>
        FrameworkElement View { get; }
        /// <summary>Se va a mostrar: es el momento de refrescar la vista.</summary>
        void OnShow() { }
    }

    /// <summary>Lo que un plugin puede hacer en el instalador (ver <see cref="INotchPlugin.ConfigureInstaller"/>).</summary>
    public interface IInstallerHost
    {
        /// <summary>Añade una etapa antes de las del instalador (en el orden en que se añadan).</summary>
        /// <param name="canContinue">Si deja pasar a la siguiente etapa (llama a <see cref="RefreshNav"/> cuando cambie).</param>
        /// <param name="gifDemo">Qué hacer en esta etapa al grabar el GIF del instalador (sin instalar nada).</param>
        void AddStage(string title, FrameworkElement view, Func<bool>? canContinue = null, Action? onEnter = null, Func<Task>? gifDemo = null);
        /// <summary>Paso tras copiar el programa; devuelve false para dejar la instalación a medias.</summary>
        void AddAfterInstall(Func<Task<bool>> step);
        /// <summary>Una fila en la lista de pasos de la instalación.</summary>
        IInstallStep AddStep(string name);
        /// <summary>Vuelve a evaluar si se puede seguir.</summary>
        void RefreshNav();
        /// <summary>Estado del personaje del instalador.</summary>
        Controls.MochiState MochiState { get; set; }
        /// <summary>El área donde viven las etapas (añade aquí la vista de una etapa nueva).</summary>
        System.Windows.Controls.Panel StageArea { get; }
        /// <summary>Recursos de la ventana (estilos «StageCard», «Switch», «OptionTitle», «OptionHint», «PillButton»…).</summary>
        object FindResource(object key);
    }

    /// <summary>Una fila de la etapa de instalación.</summary>
    public interface IInstallStep
    {
        /// <summary>ok: true correcto, false falló, null en curso o pendiente (o solo informativo con <paramref name="info"/>).</summary>
        void Set(bool? ok, string detail, bool info = false);
        System.Windows.Controls.Button AddAction(string label);
        FrameworkElement Root { get; }
    }
}
