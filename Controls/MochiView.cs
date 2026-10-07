using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AgentManagerNotch.Controls
{
    public enum MochiState { Idle, Thinking, Working, Waiting, Done, Error, Sleeping, Alarm }

    /// <summary>
    /// Personaje "mochi": un squircle blandito dibujado a mano a 60 fps.
    /// Respira, parpadea, sigue el cursor, se queja si lo pinchas, se marea si insistes,
    /// se convierte en caja al recibir archivos y salta cuando termina una tarea.
    /// </summary>
    public class MochiView : FrameworkElement
    {
        // ------------------------------------------------------------------ DPs
        public static readonly DependencyProperty BodyColorProperty = DependencyProperty.Register(
            nameof(BodyColor), typeof(Color), typeof(MochiView),
            new FrameworkPropertyMetadata(Color.FromRgb(0xFF, 0x8A, 0x65), FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((MochiView)d).RebuildBrushes()));

        public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
            nameof(State), typeof(MochiState), typeof(MochiView),
            new FrameworkPropertyMetadata(MochiState.Idle, (d, e) => ((MochiView)d).OnStateChanged((MochiState)e.OldValue, (MochiState)e.NewValue)));

        public static readonly DependencyProperty IsBoxProperty = DependencyProperty.Register(
            nameof(IsBox), typeof(bool), typeof(MochiView), new FrameworkPropertyMetadata(false));

        public static readonly DependencyProperty InteractiveProperty = DependencyProperty.Register(
            nameof(Interactive), typeof(bool), typeof(MochiView), new FrameworkPropertyMetadata(true));

        /// <summary>Está hablando: abre y cierra la boca al ritmo de <see cref="SpeechLevel"/> y saca ondas de sonido.</summary>
        public static readonly DependencyProperty IsSpeakingProperty = DependencyProperty.Register(
            nameof(IsSpeaking), typeof(bool), typeof(MochiView), new FrameworkPropertyMetadata(false));

        /// <summary>Volumen de la voz que suena ahora (0..1); lo da el plugin de voz, si hay alguno.</summary>
        public static Func<double>? SpeechLevel { get; set; }

        public Color BodyColor { get => (Color)GetValue(BodyColorProperty); set => SetValue(BodyColorProperty, value); }
        public bool IsSpeaking { get => (bool)GetValue(IsSpeakingProperty); set => SetValue(IsSpeakingProperty, value); }
        public MochiState State { get => (MochiState)GetValue(StateProperty); set => SetValue(StateProperty, value); }
        public bool IsBox { get => (bool)GetValue(IsBoxProperty); set => SetValue(IsBoxProperty, value); }
        public bool Interactive { get => (bool)GetValue(InteractiveProperty); set => SetValue(InteractiveProperty, value); }

        // ------------------------------------------------------------------ estado interno
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private readonly Random _rng = new();
        private double _t, _lastT;
        private double _phase;                 // desfase para que no respiren todos a la vez
        private double _nextBlink, _blinkStart = -10;
        private double _squash, _squashVel;    // muelle de aplastamiento
        private double _jumpStart = -10;
        private double _annoyedUntil, _dizzyUntil, _stateStart;
        private readonly List<double> _clicks = new();
        private double _lookX, _lookY, _lookTX, _lookTY;
        private double _boxAmount;             // 0..1 transición a caja
        private double _speakAmount, _mouth;   // 0..1 entrada/salida del modo hablar y apertura de la boca
        private double _lastCursorMove;
        private Point _lastCursor;
        private double _glanceUntil; private double _glanceX, _glanceY;
        // Mirada al ratón "de vez en cuando": cada personaje tiene su propia curiosidad
        private double _curiosity = -1;          // 0..1, se calcula a partir del color (estable por agente)
        private double _watchUntil = -1, _nextWatchCheck;
        private readonly List<Particle> _particles = new();
        private bool _hooked;

        private Brush _bodyBrush = Brushes.Coral, _shadowBrush = Brushes.Black;
        private Pen _eyePen = new(Brushes.Black, 2);
        private static readonly Brush EyeBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1D, 0x1B, 0x22)));
        private static readonly Brush WhiteBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush GlossBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush BubbleBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF7)));
        private static readonly Brush AlertBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x20)));
        private static readonly Brush BoxBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC8, 0x9B, 0x6D)));
        private static readonly Brush BoxDarkBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xA6, 0x7C, 0x52)));
        private static readonly Brush TongueBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0x6F, 0x86)));
        private static readonly Brush TapeBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x90, 0xF2, 0xE2, 0xC4)));

        private record struct Particle(double X, double Y, double VX, double VY, double Life, double Max, Color C, double Rot);

        public MochiView()
        {
            _phase = _rng.NextDouble() * 10;
            _nextBlink = 1 + _rng.NextDouble() * 3;
            SnapsToDevicePixels = false;
            Cursor = Cursors.Hand;
            RebuildBrushes();
            Loaded += (_, _) => Hook(true);
            Unloaded += (_, _) => Hook(false);
            IsVisibleChanged += (_, _) => Hook(IsVisible && IsLoaded);
        }

        private void Hook(bool on)
        {
            if (on == _hooked) return;
            _hooked = on;
            if (on) { _lastT = Clock.Elapsed.TotalSeconds; CompositionTarget.Rendering += OnFrame; }
            else CompositionTarget.Rendering -= OnFrame;
        }

        private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

        private void RebuildBrushes()
        {
            var c = BodyColor;
            var g = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.32, 0.22),
                Center = new Point(0.42, 0.36),
                RadiusX = 0.85, RadiusY = 0.9
            };
            g.GradientStops.Add(new GradientStop(Mix(c, Colors.White, 0.45), 0));
            g.GradientStops.Add(new GradientStop(c, 0.55));
            g.GradientStops.Add(new GradientStop(Mix(c, Colors.Black, 0.28), 1));
            _bodyBrush = Frozen(g);
            _shadowBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)));
            _eyePen = new Pen(EyeBrush, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            _eyePen.Freeze();
        }

        private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

        // ------------------------------------------------------------------ API pública
        public void Poke()
        {
            _squashVel += 9;
            _annoyedUntil = _t + 0.65;
            _clicks.Add(_t);
            _clicks.RemoveAll(c => _t - c > 2.0);
            if (_clicks.Count >= 5) { _dizzyUntil = _t + 2.8; _clicks.Clear(); }
        }

        public void Celebrate()
        {
            _jumpStart = _t;
            _squashVel += 6;
            var palette = new[] { Color.FromRgb(255, 209, 102), Color.FromRgb(6, 214, 160), Color.FromRgb(239, 71, 111), Color.FromRgb(17, 138, 178), BodyColor };
            for (int i = 0; i < 18; i++)
            {
                double a = -Math.PI / 2 + (_rng.NextDouble() - 0.5) * 2.4;
                double sp = 60 + _rng.NextDouble() * 90;
                _particles.Add(new Particle(0, -0.4, Math.Cos(a) * sp, Math.Sin(a) * sp, 0, 0.9 + _rng.NextDouble() * 0.5,
                    palette[_rng.Next(palette.Length)], _rng.NextDouble() * 360));
            }
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (Interactive) Poke();
        }

        private void OnStateChanged(MochiState old, MochiState now)
        {
            _stateStart = _t;
            if (now == MochiState.Done) Celebrate();
            if (now == MochiState.Waiting || now == MochiState.Alarm) _squashVel += 5;
            if (now == MochiState.Error) _squashVel -= 4;
        }

        // ------------------------------------------------------------------ animación
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
        [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        private static double _idleCheckedAt = -1, _idleSeconds;
        /// <summary>
        /// Segundos sin teclado ni ratón en todo el sistema (compartido por todos los personajes, así la píldora
        /// y la vista general coinciden aunque unos hayan estado ocultos).
        /// </summary>
        private static double SystemIdleSeconds()
        {
            var now = Clock.Elapsed.TotalSeconds;
            if (now - _idleCheckedAt < 0.5) return _idleSeconds;
            _idleCheckedAt = now;
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            _idleSeconds = GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.dwTime) / 1000.0 : 0;
            return _idleSeconds;
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            _t = Clock.Elapsed.TotalSeconds;
            double dt = Math.Clamp(_t - _lastT, 0, 0.05);
            _lastT = _t;

            // Muelle de aplastamiento
            double acc = -260 * _squash - 11 * _squashVel;
            _squashVel += acc * dt;
            _squash += _squashVel * dt;

            // Parpadeo
            if (_t >= _nextBlink)
            {
                _blinkStart = _t;
                _nextBlink = _t + 2.2 + _rng.NextDouble() * 4.5;
                if (_rng.NextDouble() < 0.18) _nextBlink = _t + 0.28; // doble parpadeo
            }

            // Mirada: cursor global → coordenadas locales
            UpdateLookTarget();
            double k = 1 - Math.Exp(-dt * 12);
            _lookX += (_lookTX - _lookX) * k;
            _lookY += (_lookTY - _lookY) * k;

            // Caja
            double boxTarget = IsBox ? 1 : 0;
            _boxAmount += (boxTarget - _boxAmount) * (1 - Math.Exp(-dt * 10));

            // Hablar: la boca sigue el volumen (abre rápido, cierra algo más despacio)
            _speakAmount += ((IsSpeaking ? 1 : 0) - _speakAmount) * (1 - Math.Exp(-dt * 8));
            double level = IsSpeaking ? Math.Clamp(SpeechLevel?.Invoke() ?? 0, 0, 1) : 0;
            _mouth += (level - _mouth) * (1 - Math.Exp(-dt * (level > _mouth ? 30 : 14)));

            // Partículas
            for (int i = _particles.Count - 1; i >= 0; i--)
            {
                var p = _particles[i];
                p.Life += dt;
                if (p.Life >= p.Max) { _particles.RemoveAt(i); continue; }
                p.VY += 260 * dt;
                p.X += p.VX * dt; p.Y += p.VY * dt / 100;
                p.Rot += 400 * dt;
                _particles[i] = p;
            }

            InvalidateVisual();
        }

        private void UpdateLookTarget()
        {
            var st = EffectiveState();
            switch (st)
            {
                case MochiState.Thinking: _lookTX = 0.55 + Math.Sin(_t * 1.3) * 0.1; _lookTY = -0.75; return;
                case MochiState.Working: _lookTX = Math.Sin(_t * 2.6) * 0.65; _lookTY = 0.55; return;
                case MochiState.Sleeping: _lookTX = 0; _lookTY = 0.3; return;
                case MochiState.Error: _lookTX = 0; _lookTY = 0.6; return;
            }

            if (!GetCursorPos(out var pt) || PresentationSource.FromVisual(this) == null) return;
            var screen = new Point(pt.X, pt.Y);
            if ((screen - _lastCursor).Length > 2) { _lastCursorMove = _t; _lastCursor = screen; }

            Point local;
            try { local = PointFromScreen(screen); } catch { return; }
            double cx = ActualWidth / 2, cy = ActualHeight * 0.55;
            double size = Math.Max(10, Math.Min(ActualWidth, ActualHeight));
            double dx = (local.X - cx) / (size * 3.5), dy = (local.Y - cy) / (size * 3.5);
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len > 1) { dx /= len; dy /= len; }

            // Si pasas el ratón muy cerca (o encima), siempre te mira
            double dist = Math.Sqrt(Math.Pow(local.X - cx, 2) + Math.Pow(local.Y - cy, 2));
            bool near = dist < size * 1.6;

            // Solo de vez en cuando se fija en el ratón: según su curiosidad, al moverse el cursor
            // decide si mirarlo durante 1.5–3.5 s; el resto del tiempo mira al frente o a su alrededor.
            if (_curiosity < 0) _curiosity = CuriosityFor(BodyColor);
            bool cursorMoving = _t - _lastCursorMove < 0.4;
            if (!near && _t >= _watchUntil && cursorMoving && _t >= _nextWatchCheck)
            {
                _nextWatchCheck = _t + 1.5 + _rng.NextDouble() * 2.5;
                if (_rng.NextDouble() < _curiosity) _watchUntil = _t + 1.5 + _rng.NextDouble() * 2.0;
            }
            bool watching = near || _t < _watchUntil;

            if (!watching)
            {
                // Mirada propia: casi al frente, con vistazos ocasionales a otro lado
                if (_t > _glanceUntil + 1.5 && _rng.NextDouble() < 0.012)
                {
                    _glanceUntil = _t + 0.8 + _rng.NextDouble() * 1.2;
                    _glanceX = (_rng.NextDouble() - 0.5) * 1.4; _glanceY = (_rng.NextDouble() - 0.5) * 0.7;
                }
                if (_t < _glanceUntil) { dx = _glanceX; dy = _glanceY; }
                else { dx = Math.Sin(_t * 0.31 + _phase) * 0.12; dy = 0.05; }
            }
            _lookTX = dx; _lookTY = dy;
        }

        /// <summary>Curiosidad estable por agente (derivada de su color): unos miran mucho, otros casi nunca.</summary>
        private static double CuriosityFor(Color c)
        {
            int h = (c.R * 73856093) ^ (c.G * 19349663) ^ (c.B * 83492791);
            double u = (Math.Abs(h) % 1000) / 1000.0;
            return 0.15 + u * 0.6; // entre 15 % y 75 % de probabilidad de fijarse cada vez
        }

        private MochiState EffectiveState()
        {
            var s = State;
            // Se duerme si está ocioso y nadie toca el teclado ni el ratón durante 2 minutos
            // (mientras habla no: quien lo escucha no suele tocar nada)
            if (s == MochiState.Idle && !IsSpeaking && SystemIdleSeconds() > 120) return MochiState.Sleeping;
            return s;
        }

        // ------------------------------------------------------------------ dibujo
        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            // Superficie de hit-test transparente para recibir clics en todo el rectángulo
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

            double size = Math.Min(w, h);
            double cx = w / 2;
            double ground = h * 0.93;
            var st = EffectiveState();
            bool dizzy = _t < _dizzyUntil;
            bool annoyed = _t < _annoyedUntil && !dizzy;
            double sinceState = _t - _stateStart;

            // --- cuerpo: respiración + muelle + saltos
            double breathSpeed = st == MochiState.Sleeping ? 1.2 : st is MochiState.Working or MochiState.Thinking ? 3.2 : 2.1;
            double breathe = Math.Sin((_t + _phase) * breathSpeed) * (st == MochiState.Sleeping ? 0.04 : 0.025);
            double sq = Math.Clamp(_squash * 0.045, -0.25, 0.3);
            double jumpY = 0, stretch = 0;

            double jp = (_t - _jumpStart) / 0.75;
            if (jp is >= 0 and < 1)
            {
                jumpY = -Math.Sin(jp * Math.PI) * size * 0.42;
                stretch = Math.Sin(jp * Math.PI) * 0.12;
                if (jp < 0.12 || jp > 0.88) stretch = -0.12;
            }
            else if (st == MochiState.Done && sinceState < 3.2 && sinceState > 0.8)
            {
                // saltitos de alegría después del salto grande
                double hop = Math.Abs(Math.Sin((sinceState - 0.8) * 7));
                jumpY = -hop * size * 0.1;
            }

            if (st == MochiState.Working) jumpY += -Math.Abs(Math.Sin(_t * 6.5)) * size * 0.05;

            double rot = 0, offX = 0;
            if (dizzy) rot = Math.Sin(_t * 7) * 11;
            else if (st == MochiState.Thinking) rot = Math.Sin(_t * 1.6) * 4;
            else if (st == MochiState.Alarm) rot = Math.Sin(_t * 42) * 9 * (0.6 + 0.4 * Math.Abs(Math.Sin(_t * 2)));
            else if (st == MochiState.Waiting)
            {
                double cyc = (_t % 1.6);
                if (cyc < 0.45) offX = Math.Sin(cyc * 40) * size * 0.04 * (1 - cyc / 0.45);
            }
            else if (st == MochiState.Sleeping) rot = 6;

            // al hablar el cuerpo se estira un poco con cada sílaba
            double talk = _mouth * _speakAmount;
            double sx = 1 + breathe + sq - stretch * 0.6 - talk * 0.025;
            double sy = 1 - breathe - sq + stretch + talk * 0.05;

            double bodyW = size * 0.84 * sx;
            double bodyH = size * 0.74 * sy;
            double baseY = ground + jumpY;
            double bcx = cx + offX, bcy = baseY - bodyH / 2;

            // sombra
            double air = Math.Clamp(-jumpY / (size * 0.42), 0, 1);
            dc.PushOpacity(1 - air * 0.6);
            dc.DrawEllipse(_shadowBrush, null, new Point(cx, ground + size * 0.02), bodyW * 0.42 * (1 - air * 0.4), size * 0.045);
            dc.Pop();

            dc.PushTransform(new RotateTransform(rot, bcx, baseY));

            // --- cuerpo: squircle normal o, en modo caja, una caja de cartón con las mismas animaciones
            double box = Math.Clamp(_boxAmount, 0, 1);
            if (box < 0.98)
            {
                dc.PushOpacity(1 - box);
                var body = Squircle(bcx, bcy, bodyW / 2, bodyH / 2, 3.2);
                dc.DrawGeometry(_bodyBrush, null, body);
                dc.DrawEllipse(GlossBrush, null, new Point(bcx - bodyW * 0.2, bcy - bodyH * 0.27), bodyW * 0.13, bodyH * 0.07);
                dc.Pop();
            }
            if (box > 0.02)
            {
                dc.PushOpacity(box);
                DrawBoxBody(dc, bcx, bcy, bodyW, bodyH);
                dc.Pop();
            }

            // --- posición de los ojos (sin rubor en las mejillas)
            double eyeY = bcy - bodyH * 0.04 + _lookY * bodyH * 0.07;
            double spacing = bodyW * 0.2;
            double lx = _lookX * bodyW * 0.13;

            // --- ojos
            double eyeRX = size * 0.05, eyeRY = size * 0.085;
            var pen = new Pen(EyeBrush, Math.Max(1.4, size * 0.035)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            for (int side = -1; side <= 1; side += 2)
            {
                double ex = bcx + side * spacing + lx;
                // proyección sobre esfera: el ojo que va hacia el borde se estrecha
                double edge = Math.Clamp((ex - bcx) / (bodyW * 0.5), -1, 1);
                double squeeze = 1 - 0.45 * edge * edge;
                var c = new Point(ex, eyeY);

                if (dizzy)
                {
                    DrawSpiral(dc, pen, c, eyeRX * 1.5, _t * 9 * side);
                }
                else if (annoyed)
                {
                    // > <
                    double a = eyeRX * 1.2, b = eyeRY * 0.6;
                    dc.DrawGeometry(null, pen, Chevron(c, a, b, pointRight: side < 0));
                }
                else if (st == MochiState.Done)
                {
                    // ^ ^ ojos felices
                    dc.DrawGeometry(null, pen, Arc(c, eyeRX * 1.2, eyeRY * 0.7, up: true));
                }
                else if (st == MochiState.Sleeping)
                {
                    dc.DrawGeometry(null, pen, Arc(new Point(c.X, c.Y + eyeRY * 0.3), eyeRX * 1.2, eyeRY * 0.45, up: false));
                }
                else
                {
                    double open = BlinkFactor();
                    double ry = eyeRY;
                    if (st == MochiState.Working) ry *= 0.7;
                    if (st is MochiState.Waiting or MochiState.Alarm) ry *= 1.15;
                    if (st == MochiState.Error) ry *= 0.85;
                    ry *= open;
                    double rx = eyeRX * squeeze * (st is MochiState.Waiting or MochiState.Alarm ? 1.12 : 1);
                    if (open < 0.2)
                    {
                        dc.DrawLine(pen, new Point(c.X - rx, c.Y), new Point(c.X + rx, c.Y));
                    }
                    else
                    {
                        dc.DrawEllipse(EyeBrush, null, c, rx, ry);
                        // brillo del ojo
                        dc.DrawEllipse(WhiteBrush, null, new Point(c.X - rx * 0.3 + _lookX * rx * 0.2, c.Y - ry * 0.42), rx * 0.34, ry * 0.22);
                    }
                    if (st == MochiState.Error)
                    {
                        // cejas tristes
                        // la punta interior sube, la exterior baja → expresión triste
                        double by = c.Y - eyeRY * 1.35;
                        var inner = new Point(c.X - side * eyeRX * 1.3, by - eyeRY * 0.35);
                        var outer = new Point(c.X + side * eyeRX * 1.3, by + eyeRY * 0.2);
                        dc.DrawLine(pen, inner, outer);
                    }
                }
            }

            // --- boca (solo en algunos estados; en reposo es "sin boca" como el original)
            double my = eyeY + bodyH * 0.2;
            var mouthPen = new Pen(EyeBrush, Math.Max(1.2, size * 0.028)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (_speakAmount > 0.05 && (_t - _jumpStart) >= 0.8 && !dizzy)
            {
                // hablando: boca ovalada que se abre con el volumen (y lengua cuando está bien abierta)
                double mw = size * (0.045 + 0.022 * _mouth) * (0.6 + 0.4 * _speakAmount);
                double mh = size * (0.012 + 0.075 * _mouth) * _speakAmount;
                dc.DrawEllipse(EyeBrush, null, new Point(bcx + lx, my), mw, Math.Max(mh, size * 0.008));
                if (mh > size * 0.035)
                    dc.DrawEllipse(TongueBrush, null, new Point(bcx + lx, my + mh * 0.5), mw * 0.6, mh * 0.35);
            }
            else if (st == MochiState.Done || (_t - _jumpStart) < 0.8)
            {
                // boca abierta feliz (D)
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(bcx + lx - size * 0.07, my - size * 0.01), true, true);
                    ctx.ArcTo(new Point(bcx + lx + size * 0.07, my - size * 0.01), new Size(size * 0.07, size * 0.07), 0, false, SweepDirection.Counterclockwise, true, false);
                }
                g.Freeze();
                dc.DrawGeometry(EyeBrush, null, g);
            }
            else if (st is MochiState.Waiting or MochiState.Alarm)
            {
                dc.DrawEllipse(EyeBrush, null, new Point(bcx + lx, my), size * 0.03, size * 0.04);
            }
            else if (st == MochiState.Error || dizzy)
            {
                dc.DrawGeometry(null, mouthPen, Wave(new Point(bcx + lx, my), size * 0.07, size * 0.018, dizzy ? 3 : 1, dizzy ? _t * 8 : Math.PI));
            }
            else if (annoyed)
            {
                dc.DrawGeometry(null, mouthPen, Wave(new Point(bcx + lx, my), size * 0.06, size * 0.015, 2, 0));
            }
            else if (st == MochiState.Working)
            {
                dc.DrawLine(mouthPen, new Point(bcx + lx - size * 0.03, my), new Point(bcx + lx + size * 0.03, my));
            }

            dc.Pop(); // rotación

            // --- caja (al arrastrar archivos)

            // --- extras por encima (no rotan)
            double headY = baseY - bodyH - size * 0.02;
            switch (st)
            {
                case MochiState.Thinking: DrawThinkingDots(dc, bcx + bodyW * 0.42, headY + size * 0.08, size); break;
                case MochiState.Working: DrawSpinner(dc, bcx + bodyW * 0.46, headY + size * 0.1, size); break;
                case MochiState.Waiting: DrawBadge(dc, bcx + bodyW * 0.42, headY + size * 0.06, size, "!", AlertBrush); break;
                case MochiState.Alarm: DrawBadge(dc, bcx + bodyW * 0.44, headY + size * 0.06, size, "⏰", AlertBrush); break;
                case MochiState.Sleeping: DrawZzz(dc, bcx + bodyW * 0.35, headY + size * 0.1, size); break;
            }
            if (dizzy) DrawStars(dc, bcx, headY + size * 0.02, size);
            if (_speakAmount > 0.05) DrawSoundWaves(dc, bcx + bodyW * 0.56, bcy - bodyH * 0.05, size);

            // confeti
            foreach (var p in _particles)
            {
                double a = 1 - p.Life / p.Max;
                var b = new SolidColorBrush(Color.FromArgb((byte)(255 * a), p.C.R, p.C.G, p.C.B));
                double px = cx + p.X * size / 60, py = ground + p.Y * size;
                dc.PushTransform(new RotateTransform(p.Rot, px, py));
                dc.DrawRectangle(b, null, new Rect(px - size * 0.03, py - size * 0.015, size * 0.06, size * 0.03));
                dc.Pop();
            }
        }

        private double BlinkFactor()
        {
            double b = (_t - _blinkStart) / 0.13;
            if (b < 0 || b > 1) return 1;
            return Math.Abs(1 - 2 * b) * 0.95 + 0.05;
        }

        // ------------------------------------------------------------------ geometrías
        private static Geometry Squircle(double cx, double cy, double a, double b, double n)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                const int steps = 72;
                double e = 2.0 / n;
                for (int i = 0; i < steps; i++)
                {
                    double th = i * 2 * Math.PI / steps;
                    double c = Math.Cos(th), s = Math.Sin(th);
                    double x = cx + a * Math.Sign(c) * Math.Pow(Math.Abs(c), e);
                    double y = cy + b * Math.Sign(s) * Math.Pow(Math.Abs(s), e);
                    if (i == 0) ctx.BeginFigure(new Point(x, y), true, true);
                    else ctx.LineTo(new Point(x, y), true, true);
                }
            }
            g.Freeze();
            return g;
        }

        private static Geometry Arc(Point c, double rx, double ry, bool up)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(c.X - rx, c.Y + (up ? ry * 0.5 : -ry * 0.5)), false, false);
                ctx.QuadraticBezierTo(new Point(c.X, c.Y + (up ? -ry * 1.5 : ry * 1.5)), new Point(c.X + rx, c.Y + (up ? ry * 0.5 : -ry * 0.5)), true, true);
            }
            g.Freeze();
            return g;
        }

        private static Geometry Chevron(Point c, double a, double b, bool pointRight)
        {
            double dir = pointRight ? 1 : -1;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(c.X - a * dir * 0.8, c.Y - b), false, false);
                ctx.LineTo(new Point(c.X + a * dir * 0.8, c.Y), true, true);
                ctx.LineTo(new Point(c.X - a * dir * 0.8, c.Y + b), true, true);
            }
            g.Freeze();
            return g;
        }

        private static Geometry Wave(Point c, double halfW, double amp, int waves, double phase)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                const int steps = 16;
                for (int i = 0; i <= steps; i++)
                {
                    double u = i / (double)steps;
                    var p = new Point(c.X - halfW + 2 * halfW * u, c.Y + Math.Sin(u * Math.PI * waves + phase) * amp);
                    if (i == 0) ctx.BeginFigure(p, false, false); else ctx.LineTo(p, true, true);
                }
            }
            g.Freeze();
            return g;
        }

        private static void DrawSpiral(DrawingContext dc, Pen pen, Point c, double r, double rot)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                const int steps = 28;
                for (int i = 0; i <= steps; i++)
                {
                    double u = i / (double)steps;
                    double ang = rot + u * Math.PI * 4;
                    var p = new Point(c.X + Math.Cos(ang) * r * u, c.Y + Math.Sin(ang) * r * u);
                    if (i == 0) ctx.BeginFigure(p, false, false); else ctx.LineTo(p, true, true);
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, new Pen(pen.Brush, pen.Thickness * 0.7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
        }

        /// <summary>
        /// Cuerpo en modo caja: una caja de cartón abierta del tamaño del personaje. Se dibuja con las mismas
        /// transformaciones (respirar, aplastarse, saltar, girar), así que se anima igual que el personaje.
        /// </summary>
        private void DrawBoxBody(DrawingContext dc, double cx, double cy, double w, double h)
        {
            double bw = w * 0.98, bh = h * 0.9;
            var front = new Rect(cx - bw / 2, cy - bh / 2 + h * 0.06, bw, bh);
            dc.DrawRoundedRectangle(BoxBrush, null, front, w * 0.06, w * 0.06);
            // borde superior (boca de la caja) un poco más oscuro
            dc.DrawRectangle(BoxDarkBrush, null, new Rect(front.Left + w * 0.02, front.Top, bw - w * 0.04, h * 0.07));
            // cinta vertical
            dc.DrawRectangle(TapeBrush, null, new Rect(cx - w * 0.07, front.Top, w * 0.14, bh));
            // solapas abiertas
            double flap = h * 0.22;
            var lf = new StreamGeometry();
            using (var ctx = lf.Open())
            {
                ctx.BeginFigure(new Point(front.Left, front.Top), true, true);
                ctx.LineTo(new Point(front.Left - flap * 0.6, front.Top - flap), true, true);
                ctx.LineTo(new Point(front.Left + bw * 0.26, front.Top - flap * 0.85), true, true);
                ctx.LineTo(new Point(front.Left + bw * 0.3, front.Top), true, true);
            }
            var rf = new StreamGeometry();
            using (var ctx = rf.Open())
            {
                ctx.BeginFigure(new Point(front.Right, front.Top), true, true);
                ctx.LineTo(new Point(front.Right + flap * 0.6, front.Top - flap), true, true);
                ctx.LineTo(new Point(front.Right - bw * 0.26, front.Top - flap * 0.85), true, true);
                ctx.LineTo(new Point(front.Right - bw * 0.3, front.Top), true, true);
            }
            lf.Freeze(); rf.Freeze();
            dc.DrawGeometry(BoxDarkBrush, null, lf);
            dc.DrawGeometry(BoxDarkBrush, null, rf);
        }

        private void DrawThinkingDots(DrawingContext dc, double x, double y, double size)
        {
            double r = size * 0.13;
            dc.DrawEllipse(BubbleBrush, null, new Point(x, y), r * 1.5, r);
            dc.DrawEllipse(BubbleBrush, null, new Point(x - r * 1.2, y + r * 1.2), r * 0.3, r * 0.3);
            for (int i = 0; i < 3; i++)
            {
                double bounce = Math.Max(0, Math.Sin(_t * 6 - i * 0.8)) * r * 0.3;
                dc.DrawEllipse(EyeBrush, null, new Point(x - r * 0.75 + i * r * 0.75, y - bounce), r * 0.17, r * 0.17);
            }
        }

        private void DrawSpinner(DrawingContext dc, double x, double y, double size)
        {
            double r = size * 0.09;
            var pen = new Pen(BubbleBrush, Math.Max(1.5, size * 0.035)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            double a0 = _t * 6;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(x + Math.Cos(a0) * r, y + Math.Sin(a0) * r), false, false);
                for (int i = 1; i <= 12; i++)
                {
                    double a = a0 + i * (Math.PI * 1.4 / 12);
                    ctx.LineTo(new Point(x + Math.Cos(a) * r, y + Math.Sin(a) * r), true, true);
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
        }

        private void DrawBadge(DrawingContext dc, double x, double y, double size, string text, Brush bg)
        {
            double pulse = 1 + Math.Sin(_t * 8) * 0.08;
            double r = size * 0.14 * pulse;
            dc.DrawEllipse(bg, null, new Point(x, y), r, r);
            DrawText(dc, text, x, y, r * 1.3, EyeBrush, bold: true);
        }

        private void DrawZzz(DrawingContext dc, double x, double y, double size)
        {
            for (int i = 0; i < 3; i++)
            {
                double u = ((_t * 0.5) + i / 3.0) % 1.0;
                byte alpha = (byte)(255 * Math.Sin(u * Math.PI));
                var b = new SolidColorBrush(Color.FromArgb(alpha, 0xE8, 0xE8, 0xF0));
                DrawText(dc, "z", x + u * size * 0.25, y - u * size * 0.35, size * (0.12 + u * 0.1), b, bold: true);
            }
        }

        /// <summary>Ondas de sonido al lado de la cara mientras habla: laten con el volumen.</summary>
        private void DrawSoundWaves(DrawingContext dc, double x, double y, double size)
        {
            for (int i = 0; i < 3; i++)
            {
                double pulse = 0.5 + 0.5 * Math.Sin(_t * 9 - i * 1.1);
                double alpha = _speakAmount * (0.25 + 0.75 * _mouth) * (0.45 + 0.55 * pulse) * (1 - i * 0.22);
                if (alpha < 0.03) continue;
                double r = size * (0.07 + i * 0.065 + 0.015 * _mouth);
                var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * alpha), 0xF4, 0xF4, 0xF7)), Math.Max(1.1, size * 0.03))
                {
                    StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round
                };
                const double a = 0.75; // medio ángulo del arco (rad)
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(x - size * 0.06 + Math.Cos(-a) * r, y + Math.Sin(-a) * r), false, false);
                    ctx.ArcTo(new Point(x - size * 0.06 + Math.Cos(a) * r, y + Math.Sin(a) * r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                }
                g.Freeze();
                dc.DrawGeometry(null, pen, g);
            }
        }

        private void DrawStars(DrawingContext dc, double cx, double y, double size)
        {
            for (int i = 0; i < 3; i++)
            {
                double a = _t * 4 + i * Math.PI * 2 / 3;
                DrawText(dc, "★", cx + Math.Cos(a) * size * 0.32, y + Math.Sin(a) * size * 0.07, size * 0.16, AlertBrush, false);
            }
        }

        private void DrawText(DrawingContext dc, string text, double cx, double cy, double em, Brush brush, bool bold)
        {
            if (em < 1) return;
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
                em, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
        }
    }
}
