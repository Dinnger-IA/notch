using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using AgentManagerNotch.Models;
using WinForms = System.Windows.Forms;

namespace AgentManagerNotch.Services
{
    /// <summary>Icono en la bandeja del sistema + notificaciones nativas de Windows + sonidos.</summary>
    public sealed class NotificationService : IDisposable
    {
        private readonly WinForms.NotifyIcon _icon;
        private string? _lastAgentId;
        private string? _lastToast;
        private DateTime _lastToastAt;
        private readonly Func<AppSettings> _settings;

        public event Action<string?>? NotificationClicked;
        public event Action? ShowRequested, NewAgentRequested, RemindersRequested, ReleaseNotesRequested, ExitRequested;

        public NotificationService(Func<AppSettings> settings)
        {
            _settings = settings;
            _icon = new WinForms.NotifyIcon
            {
                Icon = CreateMochiIcon(Color.FromArgb(0xFF, 0x8A, 0x65)),
                Text = "Agent Manager Notch · tus agentes",
                Visible = true
            };
            _icon.BalloonTipClicked += (_, _) => NotificationClicked?.Invoke(_lastAgentId);
            _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) ShowRequested?.Invoke(); };

            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Mostrar panel  (Ctrl+Alt+Espacio)", null, (_, _) => ShowRequested?.Invoke());
            menu.Items.Add("Nuevo agente…", null, (_, _) => NewAgentRequested?.Invoke());
            menu.Items.Add("Recordatorios y tareas…", null, (_, _) => RemindersRequested?.Invoke());
            menu.Items.Add("Notas de la versión…", null, (_, _) => ReleaseNotesRequested?.Invoke());
            menu.Items.Add(new WinForms.ToolStripSeparator());

            var sounds = new WinForms.ToolStripMenuItem("Sonidos") { CheckOnClick = true, Checked = settings().Sounds };
            sounds.CheckedChanged += (_, _) => { settings().Sounds = sounds.Checked; App.Current.Store.Save(); };
            menu.Items.Add(sounds);

            var notify = new WinForms.ToolStripMenuItem("Avisar cuando un agente termina") { CheckOnClick = true, Checked = settings().NotifyOnDone };
            notify.CheckedChanged += (_, _) => { settings().NotifyOnDone = notify.Checked; App.Current.Store.Save(); };
            menu.Items.Add(notify);

            var hover = new WinForms.ToolStripMenuItem("Abrir al pasar el ratón") { CheckOnClick = true, Checked = settings().ExpandOnHover };
            hover.CheckedChanged += (_, _) => { settings().ExpandOnHover = hover.Checked; App.Current.Store.Save(); };
            menu.Items.Add(hover);

            var clickOut = new WinForms.ToolStripMenuItem("Cerrar el panel solo al hacer clic fuera") { CheckOnClick = true, Checked = settings().CloseOnClickOutsideOnly };
            clickOut.CheckedChanged += (_, _) => { settings().CloseOnClickOutsideOnly = clickOut.Checked; App.Current.Store.Save(); };
            menu.Items.Add(clickOut);
            // Los ajustes también se cambian desde el notch: refrescar las marcas al abrir el menú
            menu.Opening += (_, _) =>
            {
                sounds.Checked = settings().Sounds; notify.Checked = settings().NotifyOnDone;
                hover.Checked = settings().ExpandOnHover; clickOut.Checked = settings().CloseOnClickOutsideOnly;
            };

            var claude = new WinForms.ToolStripMenuItem("Avisarme de mis sesiones de Claude Code")
            {
                CheckOnClick = true, Checked = ControlChannel.IsClaudeIntegrationInstalled(),
                ToolTipText = "Añade hooks Stop/Notification a ~/.claude/settings.json (con copia de seguridad)"
            };
            claude.CheckedChanged += (_, _) =>
            {
                try { ControlChannel.SetClaudeIntegration(claude.Checked); }
                catch (Exception ex) { Log.Error("Integración Claude", ex); Toast("No se pudo cambiar la integración", ex.Message, null, WinForms.ToolTipIcon.Error); }
            };
            menu.Items.Add(claude);

            var startup = new WinForms.ToolStripMenuItem("Iniciar con Windows") { CheckOnClick = true, Checked = IsStartupEnabled() };
            startup.CheckedChanged += (_, _) => SetStartup(startup.Checked);
            menu.Items.Add(startup);

            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Abrir carpeta de datos", null, (_, _) =>
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", ConfigStore.Root) { UseShellExecute = true }));
            menu.Items.Add("Salir", null, (_, _) => ExitRequested?.Invoke());
            _icon.ContextMenuStrip = menu;
        }

        /// <summary>Muestra una notificación nativa (en Windows 10/11 aparece como toast).</summary>
        public void Toast(string title, string body, string? agentId, WinForms.ToolTipIcon icon = WinForms.ToolTipIcon.None)
        {
            // El mismo aviso puede llegar dos veces (notch oculto → notificación, y la notificación propia del evento)
            var key = title + "\n" + body;
            if (key == _lastToast && DateTime.Now - _lastToastAt < TimeSpan.FromSeconds(3)) return;
            _lastToast = key;
            _lastToastAt = DateTime.Now;
            _lastAgentId = agentId;
            if (body.Length > 240) body = body[..240] + "…";
            _icon.ShowBalloonTip(6000, title, string.IsNullOrWhiteSpace(body) ? " " : body, icon);
        }

        public void Play(SoundKind kind)
        {
            if (!_settings().Sounds) return;
            switch (kind)
            {
                case SoundKind.Done: SystemSounds.Asterisk.Play(); break;
                case SoundKind.Attention: SystemSounds.Exclamation.Play(); break;
                case SoundKind.Error: SystemSounds.Hand.Play(); break;
                case SoundKind.Reminder: SystemSounds.Beep.Play(); break;
            }
        }

        public enum SoundKind { Done, Attention, Error, Reminder }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
        }

        // ---------------- Inicio con Windows (HKCU\...\Run) ----------------
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        /// <summary>Si estaba activado «Iniciar con Windows» con el nombre anterior, lo pasa al nuevo.</summary>
        public static void MigrateStartupEntry()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                if (k?.GetValue("Coucou" + "Win") == null) return;
                k.DeleteValue("Coucou" + "Win", false);
                k.SetValue("AgentManagerNotch", $"\"{Environment.ProcessPath}\"");
            }
            catch (Exception ex) { Log.Error("Migrar inicio con Windows", ex); }
        }

        public static bool IsStartupEnabled()
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("AgentManagerNotch") != null;
        }
        public static void SetStartup(bool on)
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue("AgentManagerNotch", $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue("AgentManagerNotch", false);
        }

        private static System.Windows.Media.ImageSource? _windowIcon;
        /// <summary>Icono de Mochi para las ventanas WPF.</summary>
        public static System.Windows.Media.ImageSource? WindowIcon
        {
            get
            {
                if (_windowIcon != null) return _windowIcon;
                try
                {
                    using var ico = CreateMochiIcon(Color.FromArgb(0xFF, 0x8A, 0x65));
                    _windowIcon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(ico.Handle,
                        System.Windows.Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    _windowIcon.Freeze();
                }
                catch { }
                return _windowIcon;
            }
        }

        // ---------------- Icono dibujado en tiempo de ejecución ----------------
        public static Icon CreateMochiIcon(Color c)
        {
            using var bmp = new Bitmap(64, 64);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var path = new GraphicsPath();
                var pts = Enumerable.Range(0, 64).Select(i =>
                {
                    double th = i * Math.PI * 2 / 64, co = Math.Cos(th), si = Math.Sin(th);
                    double e = 2.0 / 4.2;
                    return new PointF((float)(32 + 29 * Math.Sign(co) * Math.Pow(Math.Abs(co), e)),
                                      (float)(35 + 25 * Math.Sign(si) * Math.Pow(Math.Abs(si), e)));
                }).ToArray();
                path.AddPolygon(pts);
                using var brush = new LinearGradientBrush(new Rectangle(0, 8, 64, 56),
                    Color.FromArgb(255, Math.Min(255, c.R + 50), Math.Min(255, c.G + 50), Math.Min(255, c.B + 50)), c, 70f);
                g.FillPath(brush, path);
                using var eye = new SolidBrush(Color.FromArgb(0x1D, 0x1B, 0x22));
                g.FillEllipse(eye, 19, 26, 8, 13);
                g.FillEllipse(eye, 37, 26, 8, 13);
                using var shine = new SolidBrush(Color.White);
                g.FillEllipse(shine, 20, 27, 3, 3);
                g.FillEllipse(shine, 38, 27, 3, 3);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    /// <summary>Revisa periódicamente los recordatorios y dispara los vencidos.</summary>
    public sealed class ReminderService
    {
        private readonly ConfigStore _store;
        private readonly DispatcherTimer _timer;
        public event Action<Reminder>? Fired;
        public event Action? Changed;

        public ReminderService(ConfigStore store)
        {
            _store = store;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += (_, _) => Check();
        }

        public void Start() { _timer.Start(); Check(); }

        public void Add(Reminder r)
        {
            _store.Config.Reminders.Add(r);
            _store.Save();
            Changed?.Invoke();
        }

        public void Remove(string id)
        {
            _store.Config.Reminders.RemoveAll(r => r.Id == id);
            _store.Save();
            Changed?.Invoke();
        }

        private void Check()
        {
            var now = DateTime.Now;
            var due = _store.Config.Reminders.Where(r => r.DueAt <= now).ToList();
            if (due.Count == 0) return;
            foreach (var r in due)
            {
                // Si el PC estuvo apagado y la alarma pasó hace mucho, se avisa igual (una sola vez)
                var next = r.NextOccurrence();
                if (next is { } n) r.DueAt = n;
                else _store.Config.Reminders.Remove(r);
                try { Fired?.Invoke(r); } catch (Exception ex) { Log.Error("Reminder fire", ex); }
            }
            _store.Save();
            Changed?.Invoke();
        }
    }
}
