using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AgentManagerNotch.Controls;
using AgentManagerNotch.Services;

namespace AgentManagerNotch.Views
{
    /// <summary>
    /// Panel de git de los agentes de código: si la carpeta del workspace es un repositorio, la barra superior muestra
    /// la rama y el nº de cambios; al pulsarla se abre el panel con los cambios (preparar / quitar), el cambio de rama,
    /// el mensaje del commit, Commit y Push. Pulsar el personaje con el panel abierto hace que el agente escriba el commit.
    /// </summary>
    public partial class NotchWindow
    {
        private GitStatus? _git;
        private string _gitSignature = "";
        private int _gitSeq;
        private bool _gitBusy;
        private CancellationTokenSource? _gitWriting;
        private DispatcherTimer? _gitTimer;

        private string? GitDir => _selected is { HasFolder: true } s ? s.WorkDir : null;

        /// <summary>Al cambiar de pestaña: el panel se cierra y se mira si la nueva carpeta es un repositorio.</summary>
        private void GitOnSelect()
        {
            _gitWriting?.Cancel();
            _git = null;
            _gitSignature = "";
            GitView.Visibility = Visibility.Collapsed;
            GitNewBranchBar.Visibility = Visibility.Collapsed;
            GitButton.Visibility = Visibility.Collapsed;
            GitMessage.Text = "";
            GitSay("");
            if (_gitTimer == null)
            {
                // Mientras el chat de código está abierto, el estado se refresca solo (el agente edita archivos)
                _gitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _gitTimer.Tick += (_, _) => { if (_mode == Mode.Chat && GitDir != null && !_gitBusy) _ = RefreshGitAsync(); };
                _gitTimer.Start();
            }
            _ = RefreshGitAsync();
        }

        private async Task RefreshGitAsync()
        {
            var dir = GitDir;
            int seq = ++_gitSeq;
            var st = dir == null ? null : await GitService.StatusAsync(dir);
            if (seq != _gitSeq || dir != GitDir) return; // llegó tarde: ya se cambió de pestaña o hay otra consulta
            _git = st;
            UpdateGitUi();
        }

        private void UpdateGitUi()
        {
            var st = _git;
            if (st == null)
            {
                GitButton.Visibility = Visibility.Collapsed;
                GitView.Visibility = Visibility.Collapsed;
                return;
            }
            int n = st.Changes.Count;
            GitButton.Visibility = Visibility.Visible;
            GitButtonBranch.Text = st.Branch;
            GitButtonCount.Text = n > 0 ? $" ●{n}" : "";
            GitBranchName.Text = st.Branch;

            var sync = new List<string>();
            if (st.Upstream == null) sync.Add(st.Detached ? "sin rama" : "sin publicar");
            if (st.Ahead > 0) sync.Add($"↑{st.Ahead}");
            if (st.Behind > 0) sync.Add($"↓{st.Behind}");
            GitSync.Text = string.Join("  ", sync);
            GitSync.ToolTip = st.Upstream != null ? $"Sigue a {st.Upstream}" : null;
            GitPushLabel.Text = st.Upstream == null ? "  Publicar" : st.Ahead > 0 ? $"  Push ↑{st.Ahead}" : "  Push";
            SetGitEnabled(!_gitBusy);

            var sig = st.Branch + "|" + string.Join("|", st.Changes.Select(c => $"{c.Index}{c.WorkTree}{c.Path}"));
            if (sig == _gitSignature) return;
            _gitSignature = sig;
            BuildGitList(st);
        }

        private void BuildGitList(GitStatus st) => FillGitList(GitChanges, st,
            (c, staged) => _ = GitOpAsync(staged ? "Quitando…" : "Preparando…",
                d => staged ? GitService.UnstageAsync(d, new[] { c.Path }) : GitService.StageAsync(d, new[] { c.Path })),
            c =>
            {
                // Doble clic: abre el archivo con su programa
                if (GitDir is not { } dir) return;
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(System.IO.Path.Combine(dir, c.Path)) { UseShellExecute = true }); }
                catch (Exception ex) { GitSay(ex.Message, error: true); }
            });

        /// <summary>
        /// Pinta los cambios en dos grupos (preparados y sin preparar), cada fila con su letra de estado, el archivo y su
        /// botón para preparar / quitar. Estático para que el GIF de la versión use las mismas filas.
        /// </summary>
        internal static void FillGitList(Panel target, GitStatus st, Action<GitChange, bool> toggle, Action<GitChange>? open)
        {
            target.Children.Clear();
            var staged = st.Changes.Where(c => c.IsStaged).ToList();
            var unstaged = st.Changes.Where(c => c.IsUnstaged).ToList();
            if (staged.Count == 0 && unstaged.Count == 0)
            {
                target.Children.Add(new TextBlock
                {
                    Text = "No hay cambios: el árbol de trabajo está limpio.", FontSize = 12, Margin = new Thickness(2, 6, 0, 0),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x7C))
                });
                return;
            }
            if (staged.Count > 0)
            {
                target.Children.Add(GitHeader($"PREPARADOS · {staged.Count}"));
                foreach (var c in staged) target.Children.Add(GitRow(c, true, toggle, open));
            }
            if (unstaged.Count > 0)
            {
                target.Children.Add(GitHeader($"CAMBIOS · {unstaged.Count}", staged.Count > 0 ? 10 : 0));
                foreach (var c in unstaged) target.Children.Add(GitRow(c, false, toggle, open));
            }
        }

        private static TextBlock GitHeader(string text, double top = 0) => new()
        {
            Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, top, 0, 4),
            Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x7C))
        };

        private static FrameworkElement GitRow(GitChange c, bool staged, Action<GitChange, bool> toggle, Action<GitChange>? open)
        {
            char code = c.IsConflict ? '!' : c.IsUntracked ? 'U' : staged ? c.Index : c.WorkTree;
            var color = code switch
            {
                'M' => Color.FromRgb(0xE2, 0xC0, 0x8D),
                'A' or 'U' => Color.FromRgb(0x81, 0xB8, 0x8B),
                'D' => Color.FromRgb(0xE0, 0x6C, 0x75),
                'R' or 'C' => Color.FromRgb(0x73, 0xA9, 0xE6),
                '!' => Color.FromRgb(0xFF, 0x8A, 0x8A),
                _ => Color.FromRgb(0x9A, 0x9A, 0xA8)
            };
            var slash = c.Path.TrimEnd('/').LastIndexOf('/');
            var name = slash >= 0 ? c.Path[(slash + 1)..] : c.Path;
            var folder = slash >= 0 ? c.Path[..slash] : "";

            var row = new Grid { Margin = new Thickness(0, 0, 0, 1), Background = Brushes.Transparent };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ToolTip = c.OldPath != null ? $"{c.OldPath} → {c.Path}" : c.Path;

            var letter = new TextBlock
            {
                Text = code.ToString(), FontFamily = (FontFamily)Application.Current.FindResource("MonoFont"), FontSize = 11.5,
                FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0)
            };
            var label = new TextBlock { FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            label.Inlines.Add(new System.Windows.Documents.Run(name)
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xF2)),
                TextDecorations = code == 'D' ? TextDecorations.Strikethrough : null
            });
            if (folder != "") label.Inlines.Add(new System.Windows.Documents.Run("  " + folder) { FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x7C)) });
            Grid.SetColumn(label, 1);

            var btn = new Button
            {
                Style = (Style)Application.Current.FindResource("IconButton"), Width = 22, Height = 22, FontSize = 10,
                Content = staged ? "" : "", ToolTip = staged ? "Quitar de lo preparado" : "Preparar (stage)"
            };
            btn.Click += (_, _) => toggle(c, staged);
            Grid.SetColumn(btn, 2);

            row.Children.Add(letter);
            row.Children.Add(label);
            row.Children.Add(btn);
            row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2 && code != 'D') open?.Invoke(c); };
            return row;
        }

        private void SetGitEnabled(bool on)
        {
            foreach (var b in new[] { GitCommitButton, GitPushButton, GitBranchButton })
            {
                b.IsEnabled = on;
                b.Opacity = on ? 1 : 0.45;
            }
        }

        /// <summary>Ejecuta una operación de git mostrando su progreso y su error (si lo hay), y refresca el estado.</summary>
        private async Task<bool> GitOpAsync(string busyText, Func<string, Task<GitResult>> op, string? okText = null)
        {
            if (_gitBusy || GitDir is not { } dir) return false;
            _gitBusy = true;
            SetGitEnabled(false);
            GitSay(busyText);
            GitResult r;
            try { r = await op(dir); }
            catch (Exception ex) { r = new GitResult(-1, "", ex.Message); }
            _gitBusy = false;
            if (r.Ok) GitSay(okText ?? "");
            else
            {
                Log.Info($"[git] {busyText} falló: {r.Message}");
                GitSay(r.Message, error: true);
            }
            await RefreshGitAsync();
            SetGitEnabled(true);
            return r.Ok;
        }

        /// <summary>Línea de estado bajo el mensaje del commit (los errores en rojo y recortados por el final).</summary>
        private void GitSay(string text, bool error = false)
        {
            GitInfo.Foreground = error ? (Brush)FindResource("ErrorFg") : new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA8));
            GitInfo.Text = text.Length > 400 ? "…" + text[^400..] : text;
        }

        // ------------------------------------------------------------------ eventos
        private void GitToggle_Click(object sender, RoutedEventArgs e)
        {
            if (GitView.Visibility == Visibility.Visible)
            {
                GitView.Visibility = Visibility.Collapsed;
                GitNewBranchBar.Visibility = Visibility.Collapsed;
                FocusInput();
                return;
            }
            if (_git == null) return;
            GitView.Visibility = Visibility.Visible;
            _gitSignature = "";
            UpdateGitUi();
            _ = RefreshGitAsync();
        }

        private void GitRefresh_Click(object sender, RoutedEventArgs e) { GitSay(""); _ = RefreshGitAsync(); }

        private async void GitStageAll_Click(object sender, RoutedEventArgs e)
            => await GitOpAsync("Preparando todo…", d => GitService.StageAsync(d, new[] { "." }));

        private async void GitUnstageAll_Click(object sender, RoutedEventArgs e)
        {
            if (_git?.Changes.Any(c => c.IsStaged) != true) return;
            await GitOpAsync("Quitando todo…", d => GitService.UnstageAsync(d, new[] { "." }));
        }

        private void GitMessage_TextChanged(object sender, TextChangedEventArgs e)
            => GitMessageHint.Visibility = GitMessage.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        private async void GitCommit_Click(object sender, RoutedEventArgs e)
        {
            if (_git is not { } st) return;
            if (st.Changes.Count == 0) { GitSay("No hay cambios que confirmar."); return; }
            var msg = GitMessage.Text.Trim();
            if (msg == "")
            {
                GitSay("Escribe el mensaje del commit o pulsa el personaje para que lo escriba el agente.");
                ChatMochi.Poke();
                GitMessage.Focus();
                return;
            }
            // Sin nada preparado se confirma todo (como en VS Code)
            bool all = !st.Changes.Any(c => c.IsStaged);
            var ok = await GitOpAsync(all ? "Preparando todo y confirmando…" : "Confirmando…", async d =>
            {
                if (all)
                {
                    var add = await GitService.StageAsync(d, new[] { "." });
                    if (!add.Ok) return add;
                }
                return await GitService.CommitAsync(d, msg);
            }, "Commit hecho.");
            if (ok) GitMessage.Text = "";
        }

        private async void GitPush_Click(object sender, RoutedEventArgs e)
        {
            if (_git is not { } st) return;
            if (st.Detached) { GitSay("No estás en ninguna rama: cambia a una para hacer push."); return; }
            await GitOpAsync(st.Upstream == null ? $"Publicando {st.Branch}…" : "Haciendo push…", d => GitService.PushAsync(d, st), "Push hecho.");
        }

        private async void GitBranch_Click(object sender, RoutedEventArgs e)
        {
            if (GitDir is not { } dir || _git is not { } st) return;
            var branches = await GitService.BranchesAsync(dir);
            var m = new ContextMenu { PlacementTarget = GitBranchButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var b in branches)
            {
                var it = new MenuItem { Header = b, IsCheckable = false, IsChecked = b == st.Branch, IsEnabled = b != st.Branch };
                var name = b;
                it.Click += async (_, _) => await GitOpAsync($"Cambiando a {name}…", d => GitService.SwitchAsync(d, name), $"Ahora en {name}.");
                m.Items.Add(it);
            }
            if (branches.Count > 0) m.Items.Add(new Separator());
            var add = new MenuItem { Header = "Nueva rama…" };
            add.Click += (_, _) =>
            {
                GitNewBranchInput.Text = "";
                GitNewBranchHint.Visibility = Visibility.Visible;
                GitNewBranchBar.Visibility = Visibility.Visible;
                Dispatcher.BeginInvoke(() => { GitNewBranchInput.Focus(); Keyboard.Focus(GitNewBranchInput); }, DispatcherPriority.Input);
            };
            m.Items.Add(add);
            OpenMenu(m);
        }

        private async void GitNewBranch_KeyDown(object sender, KeyEventArgs e)
        {
            _ = Dispatcher.BeginInvoke(() => GitNewBranchHint.Visibility = GitNewBranchInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed);
            if (e.Key == Key.Escape) { e.Handled = true; GitNewBranchBar.Visibility = Visibility.Collapsed; return; }
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            var name = GitNewBranchInput.Text.Trim().Replace(' ', '-');
            if (name == "") return;
            GitNewBranchBar.Visibility = Visibility.Collapsed;
            await GitOpAsync($"Creando {name}…", d => GitService.SwitchAsync(d, name, create: true), $"Rama {name} creada.");
        }

        /// <summary>Con el panel de git abierto, pulsar el personaje le pide que escriba el mensaje del commit.</summary>
        private async void ChatMochi_Click(object sender, MouseButtonEventArgs e)
        {
            if (GitView.Visibility != Visibility.Visible || _selected is not { } s || GitDir is not { } dir || _git is not { } st) return;
            if (_gitWriting != null) { _gitWriting.Cancel(); return; } // segundo clic: cancelar
            if (st.Changes.Count == 0) { GitSay("No hay cambios para describir."); return; }

            using var cts = new CancellationTokenSource();
            _gitWriting = cts;
            var hint = GitMessageHint.Text;
            GitMessageHint.Text = $"{s.Profile.Name} está escribiendo el commit… (pulsa otra vez para cancelar)";
            GitMessage.Text = "";
            GitMessage.IsReadOnly = true;
            BindingOperations.ClearBinding(ChatMochi, MochiView.StateProperty);
            ChatMochi.State = MochiState.Thinking;
            var final = MochiState.Done;
            try
            {
                var ctx = await GitService.CommitContextAsync(dir, st);
                var msg = await GitService.WriteCommitMessageAsync(s.Profile, dir, ctx, cts.Token);
                if (!cts.IsCancellationRequested && GitDir == dir)
                {
                    GitMessage.Text = msg;
                    GitSay(st.Changes.Any(c => c.IsStaged) ? "Mensaje listo para lo preparado." : "Mensaje listo (no hay nada preparado: se confirmará todo).");
                }
            }
            catch (OperationCanceledException) { final = MochiState.Idle; GitSay("Cancelado."); }
            catch (Exception ex)
            {
                final = MochiState.Error;
                Log.Error("[git] no se pudo escribir el commit", ex);
                if (GitDir == dir) GitSay(ex.Message, error: true);
            }
            finally
            {
                _gitWriting = null;
                GitMessage.IsReadOnly = false;
                GitMessageHint.Text = hint;
                GitMessage_TextChanged(GitMessage, null!);
            }
            // Un momento de celebración (o de error) y el personaje vuelve a reflejar el estado del agente
            ChatMochi.State = final;
            await Task.Delay(final == MochiState.Idle ? 0 : 1800);
            ChatMochi.SetBinding(MochiView.StateProperty, new Binding(nameof(AgentEntry.State)));
        }
    }
}
