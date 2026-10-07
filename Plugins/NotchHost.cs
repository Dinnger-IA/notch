using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AgentManagerNotch.Controls;
using AgentManagerNotch.Models;
using AgentManagerNotch.Services;

namespace AgentManagerNotch.Plugins
{
    public enum SettingsColumn { Left, Right }

    /// <summary>
    /// Lo que la aplicación ofrece a los plugins en el arranque normal (ver <see cref="INotchPlugin.Start"/>).
    /// Lo que se añade aquí lo recoge el notch al crearse; lo que actúa sobre él (avisos, confirmaciones) sirve
    /// después, cuando ya está abierto.
    /// </summary>
    public sealed class NotchHost
    {
        private readonly App _app;
        internal NotchHost(App app) { _app = app; }

        internal readonly List<IAccessGate> Gates = new();
        internal readonly List<(FrameworkElement Card, SettingsColumn Column, Action<AgentEntry?>? Refresh)> SettingsCards = new();

        /// <summary>La aplicación (acceso completo para los plugins que viven en este mismo proyecto).</summary>
        public App App => _app;
        public AppSettings Settings => _app.Store.Config.Settings;
        public IReadOnlyCollection<AgentEntry> Agents => _app.Agents;
        public AgentEntry? EntryOf(AgentSession s) => _app.EntryOf(s);
        /// <summary>El agente principal (o el primero).</summary>
        public AgentEntry? Principal => _app.Agents.FirstOrDefault(a => a.IsDefault) ?? _app.Agents.FirstOrDefault();
        /// <summary>Escritura en el chat.</summary>
        public ChatComposer Composer { get; } = new();

        /// <summary>El notch ya está abierto (los avisos y confirmaciones funcionan desde aquí).</summary>
        public event Action? Started;
        /// <summary>Un agente terminó un turno (en el hilo de la interfaz).</summary>
        public event Action<AgentSession, TurnResult>? TurnFinished;

        /// <summary>Ajuste propio del plugin, guardado en config.json dentro de «Settings».</summary>
        public T Get<T>(string key, T fallback) => Settings.GetExtra(key, fallback);
        /// <summary>Guarda un ajuste propio del plugin (y config.json).</summary>
        public void Set<T>(string key, T value)
        {
            Settings.SetExtra(key, value);
            _app.Store.Save();
        }

        /// <summary>Puerta de acceso (p. ej. inicio de sesión): mientras alguna esté cerrada no se ven los agentes.</summary>
        public void AddAccessGate(IAccessGate gate) => Gates.Add(gate);

        /// <summary>Tarjeta en ⚙ Configuración. <paramref name="refresh"/> se llama cada vez que se abre, con el agente elegido.</summary>
        public void AddSettingsCard(FrameworkElement card, SettingsColumn column, Action<AgentEntry?>? refresh = null) =>
            SettingsCards.Add((card, column, refresh));

        /// <summary>Aviso en el notch (como los de los agentes).</summary>
        public void ShowBanner(Color color, MochiState state, string title, string body, bool sticky = false) =>
            _app.Notch?.ShowBannerRaw(color, state, title, body, null, sticky);

        /// <summary>Pregunta Sí/No sin que el notch se cierre mientras tanto.</summary>
        public bool Confirm(string message) => _app.Notch?.Confirm(message) ?? false;

        /// <summary>Vuelve a cargar la vista de configuración si está abierta.</summary>
        public void RefreshSettings() => _app.Notch?.RefreshPluginSettings();

        internal void RaiseStarted() => PluginHost.Safe("Started", () => Started?.Invoke());
        internal void RaiseTurnFinished(AgentSession s, TurnResult r) => PluginHost.Safe("TurnFinished", () => TurnFinished?.Invoke(s, r));
    }

    /// <summary>
    /// La caja de texto del chat para los plugins: botones junto a «Adjuntar», teclas, acciones del menú de cada
    /// mensaje y un aviso que puede tapar la caja mientras tanto (p. ej. «Escuchando…»).
    /// </summary>
    public sealed class ChatComposer
    {
        internal readonly List<FrameworkElement> InputButtons = new();
        internal readonly List<Func<Key, bool>> KeyHandlers = new();
        internal readonly List<(string Header, Action<AgentSession, ChatMessage> Run)> MessageActions = new();
        internal IComposerView? View;

        /// <summary>Botón (o lo que sea) a la derecha de la caja de texto.</summary>
        public void AddInputButton(FrameworkElement element) => InputButtons.Add(element);
        /// <summary>Tecla pulsada en la caja de texto: devuelve true si la atendió (antes que Intro y Esc).</summary>
        public void AddKeyHandler(Func<Key, bool> handler) => KeyHandlers.Add(handler);
        /// <summary>Opción en el menú contextual de cada mensaje del chat.</summary>
        public void AddMessageAction(string header, Action<AgentSession, ChatMessage> run) => MessageActions.Add((header, run));

        /// <summary>La conversación abierta en el chat (null si no hay chat abierto).</summary>
        public AgentSession? Selected => View?.Selected;
        public string Text { get => View?.Text ?? ""; set { if (View != null) View.Text = value; } }
        /// <summary>Envía lo escrito a la conversación abierta.</summary>
        public Task SendAsync() => View?.SendAsync() ?? Task.CompletedTask;
        public void Focus() => View?.FocusInput();
        /// <summary>Tapa la caja de texto con este elemento (null la vuelve a mostrar).</summary>
        public void SetOverlay(UIElement? overlay) => View?.SetOverlay(overlay);
        /// <summary>Texto de ejemplo de la caja vacía (null: el normal).</summary>
        public void SetPlaceholder(string? text) => View?.SetPlaceholder(text);
        /// <summary>Borde de color alrededor de la caja (null: sin borde).</summary>
        public void SetHighlight(Brush? brush) => View?.SetHighlight(brush);
    }

    /// <summary>Lo implementa la ventana del notch.</summary>
    internal interface IComposerView
    {
        AgentSession? Selected { get; }
        string Text { get; set; }
        Task SendAsync();
        void FocusInput();
        void SetOverlay(UIElement? overlay);
        void SetPlaceholder(string? text);
        void SetHighlight(Brush? brush);
    }

    /// <summary>Ajustes de los plugins: claves extra de «Settings» en config.json (se conservan aunque falte el plugin).</summary>
    public static class PluginSettings
    {
        public static T GetExtra<T>(this AppSettings s, string key, T fallback)
        {
            if (s.Extra == null || !s.Extra.TryGetValue(key, out var v)) return fallback;
            try { return v.Deserialize<T>(ConfigStore.Json) ?? fallback; }
            catch (JsonException) { return fallback; }
        }

        public static void SetExtra<T>(this AppSettings s, string key, T value)
        {
            s.Extra ??= new();
            s.Extra[key] = JsonSerializer.SerializeToElement(value, ConfigStore.Json);
        }
    }
}
