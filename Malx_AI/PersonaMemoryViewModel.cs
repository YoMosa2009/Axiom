using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Malx_AI
{
    public sealed class PersonaMemoryViewModel : INotifyPropertyChanged
    {
        private readonly PersonaMemoryService _service;
        private string _personaText = string.Empty;
        private string _status = "Persona memory ready.";

        public PersonaMemoryViewModel(PersonaMemoryService service)
        {
            _service = service;
            SaveCommand = new RelayCommand(async () => await SaveAsync());
        }

        public string PersonaText
        {
            get => _personaText;
            set
            {
                if (_personaText == value) return;
                _personaText = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(WordCount));
                OnPropertyChanged(nameof(WordCountLabel));
                OnPropertyChanged(nameof(IsPersonaEmpty));
            }
        }

        public string Status
        {
            get => _status;
            private set
            {
                if (_status == value) return;
                _status = value;
                OnPropertyChanged();
            }
        }

        public int WordCount => string.IsNullOrWhiteSpace(_personaText)
            ? 0
            : Regex.Matches(_personaText, @"\S+").Count;

        public string WordCountLabel => WordCount == 0 ? string.Empty : $"{WordCount} words";

        public bool IsPersonaEmpty => string.IsNullOrWhiteSpace(_personaText);

        public ICommand SaveCommand { get; }

        public async Task InitializeAsync()
        {
            PersonaText = await _service.LoadAsync();
            Status = string.IsNullOrWhiteSpace(PersonaText)
                ? "Empty — add context about yourself and save."
                : "Loaded.";
        }

        public async Task SaveAsync()
        {
            await _service.SaveAsync(PersonaText);
            Status = "Saved.";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
