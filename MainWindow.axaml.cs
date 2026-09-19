using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TagFile = TagLib.File;

namespace YtToMp3
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<DownloadItem> Queue { get; } = new();

        private string _outputFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music", "YtToMp3");

        private readonly DispatcherTimer _clipboardTimer;
        private string? _lastClipboardText;

        private static readonly Regex UrlRegex = new(
            @"https?://[^\s]+\.(youtube\.com|youtu\.be|vimeo\.com|soundcloud\.com|dailymotion\.com|bandcamp\.com|mixcloud\.com|bitchute\.com|rumble\.com|uol\.com\.br|vevo\.com|hearthis\.at|audiomack\.com|hypem\.com|play\.fm|naver\.com|brighteon\.com|brandnewtube\.com)[^\s]*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Detecta indícios comuns de playlist na URL (YouTube list=, /playlist, /sets/ do SoundCloud, /album/ do Bandcamp)
        private static readonly Regex PlaylistRegex = new(
            @"[?&]list=|/playlist(\?|$)|/sets/|/album/",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ProgressRegex = new(@"(\d{1,3}(?:\.\d+)?)%", RegexOptions.Compiled);
        private static readonly Regex PlaylistItemRegex = new(
            @"Downloading item (\d+) of (\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ErrorLineRegex = new(
            @"^ERROR:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            Directory.CreateDirectory(_outputFolder);
            OutputBox.Text = _outputFolder;

            AddHandler(DragDrop.DropEvent, OnDrop);

            _clipboardTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clipboardTimer.Tick += async (_, _) => await CheckClipboardAsync();
        }

        private void ClipboardMonitorCheck_Changed(object? sender, RoutedEventArgs e)
        {
            if (ClipboardMonitorCheck.IsChecked == true)
                _clipboardTimer.Start();
            else
                _clipboardTimer.Stop();
        }

        private async Task CheckClipboardAsync()
        {
            try
            {
                var clipboard = Clipboard;
                if (clipboard is null) return;

                var text = await clipboard.TryGetTextAsync();
                if (string.IsNullOrWhiteSpace(text) || text == _lastClipboardText) return;
                _lastClipboardText = text;

                if (UrlRegex.IsMatch(text) && Queue.All(q => q.Url != text))
                {
                    AddToQueue(text.Trim());
                    SetStatus("URL detectada na área de transferência e adicionada à fila.");
                }
            }
            catch { /* clipboard pode falhar se o app perder o foco; ignora */ }
        }

        private void OnDrop(object? sender, DragEventArgs e)
        {
            string? text = null;
            if (e.DataTransfer.Formats.Contains(DataFormat.Text))
                text = e.DataTransfer.TryGetText();

            if (!string.IsNullOrWhiteSpace(text))
            {
                foreach (var url in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (UrlRegex.IsMatch(url) && Queue.All(q => q.Url != url))
                        AddToQueue(url);
                }
            }
        }

        private void AddButton_Click(object? sender, RoutedEventArgs e)
        {
            var url = UrlBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(url)) return;
            AddToQueue(url);
            UrlBox.Text = "";
        }

        private void AddToQueue(string url)
        {
            var isPlaylist = PlaylistRegex.IsMatch(url);
            Dispatcher.UIThread.Post(() =>
            {
                var item = new DownloadItem(url) { IsPlaylist = isPlaylist };
                if (isPlaylist) item.Title = $"Playlist: {url}";
                Queue.Add(item);
            });
        }

        private async void BrowseButton_Click(object? sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Escolha a pasta de destino",
                AllowMultiple = false
            });

            if (folders.Count > 0)
            {
                var path = folders[0].TryGetLocalPath();
                if (!string.IsNullOrEmpty(path))
                {
                    _outputFolder = path;
                    OutputBox.Text = _outputFolder;
                }
            }
        }

        private void ClearFinishedButton_Click(object? sender, RoutedEventArgs e)
        {
            var done = Queue.Where(q => q.Status is DownloadStatus.Concluido or DownloadStatus.ConcluidoComErros or DownloadStatus.Erro).ToList();
            foreach (var item in done) Queue.Remove(item);
        }

        private async void StartQueueButton_Click(object? sender, RoutedEventArgs e)
        {
            var pending = Queue.Where(q => q.Status == DownloadStatus.Aguardando).ToList();
            if (pending.Count == 0) return;

            StartQueueButton.IsEnabled = false;
            var maxConcurrency = (int)(ConcurrencyUpDown.Value ?? 2);
            using var semaphore = new SemaphoreSlim(maxConcurrency);

            var tasks = pending.Select(async item =>
            {
                await semaphore.WaitAsync();
                try { await ProcessItemAsync(item); }
                finally { semaphore.Release(); }
            });

            await Task.WhenAll(tasks);
            StartQueueButton.IsEnabled = true;
            SetStatus("Fila concluída.");
        }

        private async Task ProcessItemAsync(DownloadItem item)
        {
            try
            {
                item.Status = DownloadStatus.Baixando;
                Directory.CreateDirectory(_outputFolder);

                var qualityIndex = QualityCombo.SelectedIndex;
                var keepOriginal = qualityIndex == 4;
                var audioQualityArg = qualityIndex switch
                {
                    1 => "--audio-quality 320K",
                    2 => "--audio-quality 192K",
                    3 => "--audio-quality 128K",
                    _ => "--audio-quality 0"
                };
                var formatArg = keepOriginal ? "--audio-format best" : "--audio-format mp3";

                var cookiesBrowser = (CookiesCombo.SelectedItem as ComboBoxItem)?.Content?.ToString();
                var cookiesArg = (cookiesBrowser is not null && cookiesBrowser != "Nenhum")
                    ? $"--cookies-from-browser {cookiesBrowser}"
                    : "";

                // Playlist: cria subpasta com o nome dela e numera cada arquivo pela posição na lista.
                // Single: salva direto na pasta de destino escolhida.
                string outputTemplate = item.IsPlaylist
                    ? Path.Combine(_outputFolder, "%(playlist_title)s", "%(playlist_index)03d - %(title)s.%(ext)s")
                    : Path.Combine(_outputFolder, "%(title)s.%(ext)s");

                var ffmpegLocation = FfmpegPathBox.Text?.Trim() ?? "ffmpeg";

                // --ignore-errors: um vídeo indisponível/restrito não derruba o resto da playlist.
                // --yes-playlist: se a URL tiver ambiguidade (vídeo dentro de playlist), sempre trata como playlist quando detectado.
                var playlistFlags = item.IsPlaylist ? "--yes-playlist --ignore-errors" : "--no-playlist --ignore-errors";

                var args = $"-x {formatArg} {audioQualityArg} {playlistFlags} " +
                           $"--ffmpeg-location \"{ffmpegLocation}\" " +
                           $"--embed-metadata --add-metadata {cookiesArg} " +
                           $"--print \"after_move:%(title)s|||%(artist)s|||%(filepath)s\" " +
                           $"-o \"{outputTemplate}\" \"{item.Url}\"";

                var ytDlpPath = YtDlpPathBox.Text?.Trim() ?? "yt-dlp";
                var (exitCode, stdout, stderr) = await RunProcessAsync(ytDlpPath, args, (progress, currentIdx, totalIdx) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        item.Progress = progress;
                        if (currentIdx > 0)
                        {
                            item.CurrentIndex = currentIdx;
                            item.TotalItems = totalIdx;
                        }
                    });
                });

                // Captura mensagens de erro por vídeo (indisponível, restrito, privado, etc.) sem abortar a playlist inteira.
                foreach (Match m in ErrorLineRegex.Matches(stderr))
                    item.SkippedItems.Add(FriendlyError(m.Groups[1].Value));

                item.Status = DownloadStatus.Convertendo;

                var lastLine = stdout.Split('\n').LastOrDefault(l => l.Contains("|||") && !item.IsPlaylist);
                if (lastLine is not null)
                {
                    var parts = lastLine.Trim().Split("|||");
                    if (parts.Length == 3)
                    {
                        item.Title = string.IsNullOrWhiteSpace(parts[0]) ? item.Title : parts[0];
                        item.Artist = string.IsNullOrWhiteSpace(parts[1]) || parts[1] == "NA" ? "" : parts[1];
                        item.FinalFilePath = parts[2];
                    }
                    if (!string.IsNullOrEmpty(item.FinalFilePath) && File.Exists(item.FinalFilePath))
                        TryWriteTags(item);
                }
                else if (item.IsPlaylist)
                {
                    var firstPrint = stdout.Split('\n').FirstOrDefault(l => l.Contains("|||"));
                    var folderGuess = firstPrint?.Split("|||").ElementAtOrDefault(2);
                    if (!string.IsNullOrEmpty(folderGuess))
                        item.PlaylistTitle = new DirectoryInfo(Path.GetDirectoryName(folderGuess)!).Name;
                    item.Title = item.PlaylistTitle is not null
                        ? $"Playlist: {item.PlaylistTitle}"
                        : item.Title;
                }

                item.Progress = 100;

                if (exitCode == 0 && item.SkippedItems.Count == 0)
                {
                    item.Status = DownloadStatus.Concluido;
                }
                else if (item.IsPlaylist && item.SkippedItems.Count > 0)
                {
                    // Playlist terminou, mas alguns vídeos foram pulados (indisponíveis/restritos/privados).
                    item.Status = DownloadStatus.ConcluidoComErros;
                    item.ErrorMessage = string.Join(" | ", item.SkippedItems.Take(5));
                }
                else if (exitCode != 0)
                {
                    throw new InvalidOperationException(FriendlyError(TrimError(stderr)));
                }
                else
                {
                    item.Status = DownloadStatus.Concluido;
                }
            }
            catch (Exception ex)
            {
                item.ErrorMessage = ex.Message;
                item.Status = DownloadStatus.Erro;
            }
        }

        // Traduz as mensagens de erro mais comuns do yt-dlp para um texto amigável em PT-BR.
        private static string FriendlyError(string rawMessage)
        {
            var msg = rawMessage.ToLowerInvariant();

            if (msg.Contains("private video") || msg.Contains("this video is private"))
                return "Vídeo privado — sem permissão para acessar.";
            if (msg.Contains("video unavailable") || msg.Contains("unavailable"))
                return "Vídeo indisponível (removido ou não existe mais).";
            if (msg.Contains("sign in to confirm your age") || msg.Contains("age-restricted") || msg.Contains("age restricted"))
                return "Vídeo com restrição de idade — configure 'cookies do navegador' para acessar.";
            if (msg.Contains("copyright"))
                return "Vídeo bloqueado por reivindicação de direitos autorais.";
            if (msg.Contains("this video is not available in your country") || msg.Contains("not available in your country") || msg.Contains("geo"))
                return "Vídeo bloqueado geograficamente (indisponível na sua região).";
            if (msg.Contains("members-only") || msg.Contains("members only"))
                return "Conteúdo exclusivo para membros do canal.";
            if (msg.Contains("live event") || msg.Contains("premiere"))
                return "Vídeo ainda não disponível (é uma live/estreia agendada).";
            if (msg.Contains("http error 403") || msg.Contains("403"))
                return "Acesso negado pelo servidor (403) — o link pode ter expirado.";
            if (msg.Contains("unable to download webpage") || msg.Contains("network"))
                return "Falha de rede ao acessar o link — verifique sua conexão.";

            return rawMessage.Length > 200 ? rawMessage[..200] + "..." : rawMessage;
        }

        private static void TryWriteTags(DownloadItem item)
        {
            try
            {
                using var tagFile = TagFile.Create(item.FinalFilePath!);
                if (!string.IsNullOrWhiteSpace(item.Title)) tagFile.Tag.Title = item.Title;
                if (!string.IsNullOrWhiteSpace(item.Artist)) tagFile.Tag.Performers = new[] { item.Artist };
                tagFile.Save();
            }
            catch { /* formato sem suporte a tags (ex: opus/webm); ignora */ }
        }

        private static string TrimError(string stderr)
        {
            var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" | ", lines.TakeLast(3));
        }

        private static Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(
            string fileName, string arguments, Action<double, int, int> onProgress)
        {
            var tcs = new TaskCompletionSource<(int, string, string)>();
            var stdoutBuilder = new System.Text.StringBuilder();
            var stderrBuilder = new System.Text.StringBuilder();

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                stdoutBuilder.AppendLine(e.Data);

                var playlistMatch = PlaylistItemRegex.Match(e.Data);
                if (playlistMatch.Success)
                {
                    var current = int.Parse(playlistMatch.Groups[1].Value);
                    var total = int.Parse(playlistMatch.Groups[2].Value);
                    onProgress((double)current / total * 100, current, total);
                    return;
                }

                var pctMatch = ProgressRegex.Match(e.Data);
                if (pctMatch.Success && double.TryParse(pctMatch.Groups[1].Value,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var pct))
                {
                    onProgress(Math.Min(pct, 99), 0, 0);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) stderrBuilder.AppendLine(e.Data);
            };

            process.Exited += (_, _) =>
            {
                tcs.TrySetResult((process.ExitCode, stdoutBuilder.ToString(), stderrBuilder.ToString()));
                process.Dispose();
            };

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(new InvalidOperationException(
                    $"Não foi possível iniciar '{fileName}'. Confirme se está instalado e acessível.", ex));
            }

            return tcs.Task;
        }

        private async void QueueItem_DoubleTapped(object? sender, TappedEventArgs e)
        {
            if (sender is not Grid grid || grid.DataContext is not DownloadItem item) return;

            if (item.IsPlaylist && item.SkippedItems.Count > 0)
            {
                await ShowSkippedItemsDialog(item);
                return;
            }

            var titleBox = new TextBox { Text = item.Title, PlaceholderText = "Título" };
            var artistBox = new TextBox { Text = item.Artist, PlaceholderText = "Artista" };
            var okButton = new Button { Content = "Salvar", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };

            var dialog = new Window
            {
                Title = "Editar tags",
                Width = 360,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel
                {
                    Margin = new Avalonia.Thickness(16),
                    Spacing = 10,
                    Children = { new TextBlock { Text = "Título:" }, titleBox,
                                 new TextBlock { Text = "Artista:" }, artistBox, okButton }
                }
            };

            okButton.Click += (_, _) =>
            {
                item.Title = titleBox.Text ?? item.Title;
                item.Artist = artistBox.Text ?? item.Artist;
                if (!string.IsNullOrEmpty(item.FinalFilePath) && File.Exists(item.FinalFilePath))
                    TryWriteTags(item);
                dialog.Close();
            };

            await dialog.ShowDialog(this);
        }

        private async Task ShowSkippedItemsDialog(DownloadItem item)
        {
            var listText = string.Join("\n\n", item.SkippedItems.Select((msg, i) => $"{i + 1}. {msg}"));
            var okButton = new Button { Content = "Fechar", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };

            var dialog = new Window
            {
                Title = $"Vídeos pulados em \"{item.PlaylistTitle ?? item.Title}\"",
                Width = 480,
                Height = 320,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new ScrollViewer
                {
                    Content = new StackPanel
                    {
                        Margin = new Avalonia.Thickness(16),
                        Spacing = 10,
                        Children = { new TextBlock { Text = listText, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, okButton }
                    }
                }
            };

            okButton.Click += (_, _) => dialog.Close();
            await dialog.ShowDialog(this);
        }

        private void SetStatus(string text)
        {
            Dispatcher.UIThread.Post(() => StatusLabel.Text = text);
        }
    }
}
