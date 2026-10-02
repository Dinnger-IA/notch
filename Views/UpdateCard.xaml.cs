using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace AgentManagerNotch.Views
{
    /// <summary>Tarjeta «Versión X disponible» con sus novedades y los botones Actualizar ahora / Más tarde.</summary>
    public partial class UpdateCard : UserControl
    {
        public event Action? NowClicked, LaterClicked;

        public UpdateCard() => InitializeComponent();

        public void Show(string version, IEnumerable<string> notes, string info)
        {
            var list = notes.Take(5).ToList();
            TitleText.Text = $"Versión {version} disponible";
            NotesText.Text = string.Join("\n", list.Select(n => "•  " + n));
            NotesText.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            InfoText.Text = info;
            NowButton.IsEnabled = true;
        }

        /// <summary>Mientras se prepara la actualización.</summary>
        public void ShowBusy(string info)
        {
            NowButton.IsEnabled = false;
            InfoText.Text = info;
        }

        private void Now_Click(object sender, RoutedEventArgs e) => NowClicked?.Invoke();
        private void Later_Click(object sender, RoutedEventArgs e) => LaterClicked?.Invoke();
    }
}
