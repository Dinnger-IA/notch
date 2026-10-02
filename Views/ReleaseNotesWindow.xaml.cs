using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AgentManagerNotch.Services;

namespace AgentManagerNotch.Views
{
    /// <summary>Notas de todas las versiones: una pestaña por versión en la barra lateral, con su GIF si lo tiene.</summary>
    public partial class ReleaseNotesWindow : Window
    {
        public ReleaseNotesWindow(Version? select = null)
        {
            InitializeComponent();
            Icon = NotificationService.WindowIcon;
            VersionList.ItemsSource = ReleaseNotes.All;
            Select(select);
        }

        /// <summary>Muestra una versión (por defecto, la instalada o la más nueva).</summary>
        public void Select(Version? version)
        {
            var all = ReleaseNotes.All;
            VersionList.SelectedItem = all.FirstOrDefault(n => n.Version == version)
                                       ?? all.FirstOrDefault(n => n.IsCurrent)
                                       ?? all.FirstOrDefault();
            if (VersionList.SelectedItem != null) VersionList.ScrollIntoView(VersionList.SelectedItem);
        }

        private void VersionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (VersionList.SelectedItem is not ReleaseNote n) return;
            TitleText.Text = $"Versión {n.Name}" + (n.IsCurrent ? " · instalada" : "");
            DateText.Text = n.Date;
            DateText.Visibility = n.Date == "" ? Visibility.Collapsed : Visibility.Visible;
            Gif.Load(n.Gif);
            GifFrame.Visibility = n.Gif == null ? Visibility.Collapsed : Visibility.Visible;
            Markdown.SetText(Body, n.Body);
            ContentScroll.ScrollToTop();
        }
    }
}
