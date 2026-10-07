using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentManagerNotch.Controls;

namespace AgentManagerNotch.Plugins.AgentManager
{
    /// <summary>
    /// Puerta de acceso de Agent Manager: sin sesión, el notch solo muestra el inicio de sesión (salvo que se haya
    /// instalado sin Agent Manager: entonces está siempre abierta y el notch funciona directamente con los CLI).
    /// </summary>
    public sealed class LoginGate : IAccessGate
    {
        private readonly AuthService _auth;
        private readonly Func<bool> _enabled;
        private LoginView? _view;

        public LoginGate(AuthService auth, Func<bool> enabled)
        {
            _auth = auth;
            _enabled = enabled;
            _auth.Changed += Raise;
        }

        public bool IsOpen => !_enabled() || _auth.IsAuthenticated;
        public event Action? Changed;
        public string Title => "Agent Manager Notch";
        public string LockedLabel => _auth.IsLoggingIn ? "Esperando inicio de sesión…" : "Iniciar sesión";
        public FrameworkElement View => _view ??= new LoginView(this, _auth);
        public void OnShow() => _view?.Refresh(_view.LastError);

        /// <summary>Cambió algo que afecta a la puerta (sesión, inicio de sesión en curso o la opción de usarlo).</summary>
        public void Raise() => Changed?.Invoke();
    }

    public partial class LoginView : UserControl
    {
        private readonly LoginGate _gate;
        private readonly AuthService _auth;
        /// <summary>Motivo del último intento fallido (se mantiene al volver a mostrar la vista).</summary>
        public string? LastError { get; private set; }

        public LoginView(LoginGate gate, AuthService auth)
        {
            InitializeComponent();
            _gate = gate;
            _auth = auth;
        }

        public void Refresh(string? error = null)
        {
            bool waiting = _auth.IsLoggingIn;
            LoginButton.IsEnabled = !waiting;
            LoginButton.Content = error != null ? "Reintentar" : "Iniciar sesión";
            LoginCancelButton.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
            LoginMochi.State = waiting ? MochiState.Thinking : error != null ? MochiState.Error : MochiState.Idle;
            LoginStatus.Foreground = (Brush)FindResource(error != null ? "ErrorFg" : "TextSecondary");
            LoginStatus.Text = error
                ?? (waiting ? "Completa el inicio de sesión en el navegador. Esta ventana se actualizará sola."
                            : "Para ver y usar tus agentes, entra con tu cuenta de Agent Manager. Se abrirá el navegador.");
        }

        private async void Login_Click(object sender, RoutedEventArgs e)
        {
            LastError = null;
            var task = _auth.LoginAsync();
            Refresh();
            _gate.Raise(); // la píldora pasa a «Esperando inicio de sesión…»
            var error = await task;
            if (_gate.IsOpen) return; // la puerta ya abrió los agentes
            LastError = error;
            Refresh(error);
            _gate.Raise();
        }

        private void LoginCancel_Click(object sender, RoutedEventArgs e) => _auth.CancelLogin();
    }
}
