using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace YtToMp3
{
    public enum DownloadStatus
    {
        Aguardando,
        Baixando,
        Convertendo,
        Concluido,
        ConcluidoComErros,
        Erro
    }

    public class DownloadItem : INotifyPropertyChanged
    {
        private string _title;
        private string _artist = "";
        private double _progress;
        private DownloadStatus _status = DownloadStatus.Aguardando;
        private string? _finalFilePath;
        private string? _errorMessage;
        private int _currentIndex;
        private int _totalItems;

        public string Url { get; }
        public bool IsPlaylist { get; set; }
        public string? PlaylistTitle { get; set; }
        public List<string> SkippedItems { get; } = new();

        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }

        public string Artist
        {
            get => _artist;
            set { _artist = value; OnPropertyChanged(); }
        }

        public double Progress
        {
            get => _progress;
            set { _progress = value; OnPropertyChanged(); }
        }

        public int CurrentIndex
        {
            get => _currentIndex;
            set { _currentIndex = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
        }

        public int TotalItems
        {
            get => _totalItems;
            set { _totalItems = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
        }

        public DownloadStatus Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
        }

        public string StatusText
        {
            get
            {
                var playlistSuffix = IsPlaylist && TotalItems > 0 ? $" ({CurrentIndex}/{TotalItems})" : "";
                return Status switch
                {
                    DownloadStatus.Aguardando => "Aguardando",
                    DownloadStatus.Baixando => $"Baixando...{playlistSuffix}",
                    DownloadStatus.Convertendo => $"Convertendo...{playlistSuffix}",
                    DownloadStatus.Concluido => $"Concluído{playlistSuffix}",
                    DownloadStatus.ConcluidoComErros => $"Concluído com {SkippedItems.Count} erro(s){playlistSuffix}",
                    DownloadStatus.Erro => $"Erro: {ErrorMessage}",
                    _ => ""
                };
            }
        }

        public string? FinalFilePath
        {
            get => _finalFilePath;
            set { _finalFilePath = value; OnPropertyChanged(); }
        }

        public string? ErrorMessage
        {
            get => _errorMessage;
            set { _errorMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
        }

        public DownloadItem(string url)
        {
            Url = url;
            _title = url;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
