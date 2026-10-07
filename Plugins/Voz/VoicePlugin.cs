using System;
using System.Collections.Generic;
using AgentManagerNotch.Controls;
using AgentManagerNotch.Services;

namespace AgentManagerNotch.Plugins.Voz
{
    /// <summary>
    /// Voz de los agentes con el servidor TTS/STT interno (<see cref="VoiceService"/>): micrófono en el chat (se habla
    /// manteniéndolo pulsado), respuesta en voz alta solo a lo que se pidió hablando, botón para callar, «Escuchar» en
    /// el menú de cada mensaje y la tarjeta ⚙ Configuración → *Voz*. No depende de Agent Manager.
    /// Ajustes en config.json: <c>VoiceUrl</c> y <c>VoiceSpeed</c>.
    /// </summary>
    public sealed class VoicePlugin : INotchPlugin
    {
        public string Id => "voz";
        public string Name => "Voz";
        public int Order => 10;

        public NotchHost? Host { get; private set; }
        public VoiceService Voice { get; private set; } = null!;
        /// <summary>Conversaciones cuyo último mensaje se dictó: su respuesta se lee en voz alta.</summary>
        private readonly HashSet<AgentSession> _byVoice = new();

        public void Register(PluginRegistry registry)
        {
            registry.AddGifDemo("voz", "el agente leyendo su respuesta en voz alta", VoiceGifDemos.SpeakingAsync);
            registry.AddGifDemo("microfono", "hablar manteniendo pulsado el micrófono", VoiceGifDemos.PushToTalkAsync);
        }

        public void Start(NotchHost host)
        {
            Host = host;
            Voice = new VoiceService(() => host.Get<string?>("VoiceUrl", VoiceService.DefaultUrl), () => host.Get("VoiceSpeed", 1.0));
            MochiView.SpeechLevel = () => Voice.Level;

            var buttons = new VoiceButtons(this, host.Composer);
            host.Composer.AddInputButton(buttons);
            host.Composer.AddKeyHandler(buttons.HandleKey);
            host.Composer.AddMessageAction("Escuchar", (s, m) => Speak(s, m.Text));

            var card = new VoiceCard(this);
            host.AddSettingsCard(card, SettingsColumn.Left, card.Refresh);

            host.TurnFinished += OnTurnFinished;
        }

        public void Stop() => Voice?.Dispose();

        /// <summary>El próximo turno de esta conversación se pidió hablando.</summary>
        internal void ReplyByVoice(AgentSession s) => _byVoice.Add(s);

        /// <summary>Voz: la respuesta se lee en voz alta solo si se pidió hablando (micrófono).</summary>
        private void OnTurnFinished(AgentSession s, TurnResult r)
        {
            if (r.Cancelled) { _byVoice.Remove(s); return; }
            bool byVoice = _byVoice.Remove(s);
            if (r.Success && !r.FromSchedule && byVoice && !string.IsNullOrWhiteSpace(r.FinalText))
                Speak(s, r.FinalText);
        }

        /// <summary>Lee en voz alta un texto del agente; si falla, lo avisa en su conversación.</summary>
        public async void Speak(AgentSession s, string text)
        {
            try { await Voice.SpeakAsync(Host?.EntryOf(s), text); }
            catch (Exception ex) { s.AddNotice(ex.Message, error: true); }
        }
    }
}
