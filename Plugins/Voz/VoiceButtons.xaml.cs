using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace AgentManagerNotch.Plugins.Voz
{
    /// <summary>
    /// Micrófono y altavoz de la caja del chat. Se habla manteniendo pulsado el micrófono: al soltar se transcribe y
    /// se envía (y la respuesta se lee en voz alta); alejar el cursor antes de soltar, o Esc, cancela.
    /// </summary>
    public partial class VoiceButtons : UserControl
    {
        private readonly VoicePlugin _plugin;
        private readonly ChatComposer _composer;
        private VoiceService Voice => _plugin.Voice;

        private bool _transcribing, _micHeld, _micAway, _micTip;
        private DateTime _micDownAt;
        private Storyboard? _micPulse;
        /// <summary>Una pulsación más corta que esto se toma como clic: se explica que hay que mantenerlo pulsado.</summary>
        private static readonly TimeSpan MicMinHold = TimeSpan.FromMilliseconds(400);

        // Aviso que tapa la caja de texto mientras se graba o transcribe
        private readonly StackPanel _hint;
        private readonly Ellipse _hintDot;
        private readonly TextBlock _hintText;

        public VoiceButtons(VoicePlugin plugin, ChatComposer composer)
        {
            InitializeComponent();
            _plugin = plugin;
            _composer = composer;
            _hintDot = new Ellipse { Width = 8, Height = 8, Fill = (Brush)FindResource("ErrorFg"), VerticalAlignment = VerticalAlignment.Center };
            _hintText = new TextBlock
            {
                Margin = new Thickness(8, 0, 0, 0), FontSize = 13.5, Foreground = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xF2)),
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
            };
            _hint = new StackPanel { Orientation = Orientation.Horizontal, Children = { _hintDot, _hintText } };
            Voice.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(Update);
            Loaded += (_, _) => Update();
        }

        /// <summary>Esc en la caja de texto mientras se graba: cancela.</summary>
        public bool HandleKey(Key key)
        {
            if (key != Key.Escape || !Voice.IsRecording) return false;
            CancelMic();
            return true;
        }

        /// <summary>Altavoz (solo visible mientras el agente habla): lo calla.</summary>
        private void Speaker_Click(object sender, RoutedEventArgs e) => Voice.StopSpeaking();

        /// <summary>Micrófono pulsado: empieza a escuchar mientras se mantenga pulsado.</summary>
        private void Mic_Down(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var target = _composer.Selected;
            if (_transcribing || _micHeld || target == null) return;
            try { Voice.StartRecording(); }
            catch (Exception ex) { target.AddNotice(ex.Message, error: true); return; }
            _micHeld = true;
            _micAway = false;
            _micTip = false;
            _micDownAt = DateTime.UtcNow;
            MicButton.CaptureMouse();
            _composer.Focus(); // para que Esc cancele
            Update();
        }

        /// <summary>Con el micrófono pulsado, alejar el cursor avisa de que al soltar se cancela.</summary>
        private void Mic_Move(object sender, MouseEventArgs e)
        {
            if (!_micHeld) return;
            var away = !OverMic(e);
            if (away == _micAway) return;
            _micAway = away;
            Update();
        }

        /// <summary>Micrófono soltado: envía lo dicho, o cancela si el cursor ya no estaba encima o fue solo un clic.</summary>
        private async void Mic_Up(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (!_micHeld) return;
            _micHeld = false;
            MicButton.ReleaseMouseCapture();
            if (!OverMic(e)) { CancelMic(); return; }
            if (DateTime.UtcNow - _micDownAt < MicMinHold) { CancelMic(); ShowMicTip(); return; }
            await SendRecording();
        }

        /// <summary>Se perdió el ratón con el micrófono pulsado (p. ej. otra ventana tomó el foco): se cancela.</summary>
        private void Mic_LostCapture(object sender, MouseEventArgs e)
        {
            if (_micHeld) CancelMic();
        }

        private bool OverMic(MouseEventArgs e)
        {
            const double margin = 12; // un poco de holgura alrededor del botón
            var p = e.GetPosition(MicButton);
            return p.X >= -margin && p.Y >= -margin && p.X <= MicButton.ActualWidth + margin && p.Y <= MicButton.ActualHeight + margin;
        }

        private void CancelMic()
        {
            var held = _micHeld;
            _micHeld = false;
            _micAway = false;
            if (held) MicButton.ReleaseMouseCapture();
            Voice.CancelRecording();
            Update();
        }

        /// <summary>Tras un clic corto en el micrófono, explica unos segundos en la caja de texto cómo se usa.</summary>
        private async void ShowMicTip()
        {
            _micTip = true;
            Update();
            await Task.Delay(2800);
            if (!_micTip) return;
            _micTip = false;
            Update();
        }

        /// <summary>Transcribe lo grabado y lo envía; la respuesta se leerá en voz alta.</summary>
        private async Task SendRecording()
        {
            var target = _composer.Selected;
            if (target == null) { CancelMic(); return; }
            _transcribing = true;
            _micAway = false;
            Update();
            try
            {
                var hotwords = string.Join(",", (_plugin.Host?.Agents.Select(a => a.Name) ?? []).Append("Agent Manager"));
                var text = await Voice.StopRecordingAsync(hotwords);
                if (text.Length == 0) { target.AddNotice("No te he entendido; vuelve a intentarlo."); return; }
                _composer.Text = (_composer.Text.TrimEnd() + " " + text).Trim();
                if (_composer.Selected == target && target.CanType)
                {
                    _plugin.ReplyByVoice(target);
                    await _composer.SendAsync();
                }
            }
            catch (Exception ex) { target.AddNotice(ex.Message, error: true); }
            finally
            {
                _transcribing = false;
                Update();
            }
        }

        private void Update()
        {
            var rec = Voice.IsRecording;
            SpeakerButton.Visibility = Voice.IsSpeaking ? Visibility.Visible : Visibility.Collapsed;
            MicButton.Foreground = rec && !_micAway ? Brushes.White : (Brush)FindResource("TextPrimary");
            MicButton.IsEnabled = !_transcribing;

            // Mientras se graba o transcribe, un aviso tapa la caja de texto y dice qué pasa al soltar
            var busy = rec || _transcribing;
            _hintText.Text = _transcribing ? "Transcribiendo…" : _micAway ? "Suelta para cancelar" : "Escuchando… suelta para enviar";
            _hintText.Opacity = _micAway ? 0.6 : 1;
            _hintDot.Visibility = rec && !_micAway ? Visibility.Visible : Visibility.Collapsed;
            _composer.SetOverlay(busy ? _hint : null);
            _composer.SetHighlight(rec && !_micAway ? (Brush)FindResource("ErrorFg") : null);
            _composer.SetPlaceholder(_micTip ? "Mantén pulsado el micrófono mientras hablas" : null);

            // Halo rojo que late detrás del micrófono mientras escucha
            if (rec && !_micAway)
            {
                if (_micPulse != null) return;
                var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
                void Anim(string prop, double from, double to)
                {
                    var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(650)) { EasingFunction = new SineEase() };
                    Storyboard.SetTarget(a, MicPulse);
                    Storyboard.SetTargetProperty(a, new PropertyPath(prop));
                    sb.Children.Add(a);
                }
                Anim("Opacity", 0.85, 0.45);
                Anim("RenderTransform.ScaleX", 1.0, 1.25);
                Anim("RenderTransform.ScaleY", 1.0, 1.25);
                _micPulse = sb;
                sb.Begin(this, true);
            }
            else if (_micPulse != null)
            {
                _micPulse.Stop(this);
                _micPulse = null;
                MicPulse.Opacity = 0;
            }
        }
    }
}
