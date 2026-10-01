using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AgentManagerNotch.Models;
using AgentManagerNotch.Services;

namespace AgentManagerNotch.Views
{
    public partial class RemindersWindow : Window
    {
        public RemindersWindow()
        {
            InitializeComponent();
            Icon = NotificationService.WindowIcon;
            foreach (var (r, label) in new[]
                     {
                         (Recurrence.None, "Una vez"), (Recurrence.Hourly, "Cada hora"), (Recurrence.Daily, "Cada día"),
                         (Recurrence.Weekdays, "De lunes a viernes"), (Recurrence.Weekly, "Cada semana")
                     })
                RecurrenceBox.Items.Add(new ComboBoxItem { Content = label, Tag = r });
            RecurrenceBox.SelectedIndex = 0;

            KindBox.Items.Add(new ComboBoxItem { Content = "Solo avisarme", Tag = ReminderKind.Notify });
            KindBox.Items.Add(new ComboBoxItem { Content = "Que el agente lo ejecute", Tag = ReminderKind.RunPrompt });
            KindBox.SelectedIndex = 0;

            AgentBox.ItemsSource = App.Current.Agents;
            AgentBox.SelectedItem = App.Current.Agents.FirstOrDefault(a => a.IsDefault) ?? App.Current.Agents.FirstOrDefault();

            App.Current.Reminders.Changed += Refresh;
            Closed += (_, _) => App.Current.Reminders.Changed -= Refresh;
            Refresh();
            When_Changed(this, null!);
            Loaded += (_, _) => WhatBox.Focus();
        }

        private void Refresh()
        {
            var items = App.Current.Store.Config.Reminders.OrderBy(r => r.DueAt).ToList();
            List.ItemsSource = items;
            Empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void When_Changed(object sender, TextChangedEventArgs e)
        {
            if (WhenPreview == null) return;
            WhenPreview.Text = TimeParser.TryParse(WhenBox.Text, out var due) ? $"→ {due:dddd dd MMM yyyy, HH:mm}" : "No entiendo esa fecha";
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(WhatBox.Text)) { WhatBox.Focus(); return; }
            if (!TimeParser.TryParse(WhenBox.Text, out var due)) { WhenBox.Focus(); return; }
            var r = new Reminder
            {
                Text = WhatBox.Text.Trim(),
                DueAt = due,
                Recurrence = RecurrenceBox.SelectedItem is ComboBoxItem rc ? (Recurrence)rc.Tag : Recurrence.None,
                Kind = KindBox.SelectedItem is ComboBoxItem k ? (ReminderKind)k.Tag : ReminderKind.Notify,
                AgentId = (AgentBox.SelectedItem as AgentEntry)?.Profile.Id
            };
            App.Current.Reminders.Add(r);
            WhatBox.Clear();
            WhatBox.Focus();
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string id }) App.Current.Reminders.Remove(id);
        }
    }
}
