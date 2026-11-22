using System.ComponentModel;
using System;
using System.IO;
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
                Status = "Unsaved changes.";
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

        private bool _shareWithModel = true;

        /// <summary>Lets Normal Chat give the saved notes to the model. Changing it saves at once.</summary>
        public bool ShareWithModel
        {
            get => _shareWithModel;
            set
            {
                if (_shareWithModel == value) return;
                _shareWithModel = value;
                OnPropertyChanged();
                _ = SaveShareSettingAsync(value);
            }
        }

        private async Task SaveShareSettingAsync(bool share)
        {
            try
            {
                await _service.SetShareWithModelAsync(share);
                Status = share ? "Normal Chat will use your saved notes." : "Normal Chat will not use your notes.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Status = "Could not save that setting.";
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
            _shareWithModel = _service.ShareWithModel;
            OnPropertyChanged(nameof(ShareWithModel));
            Status = !string.IsNullOrEmpty(_service.LoadStatus) ? _service.LoadStatus : string.IsNullOrWhiteSpace(PersonaText)
                ? "Empty — add context about yourself and save."
                : "Loaded.";
        }

        public async Task SaveAsync()
        {
            string notes = PersonaText;
            try
            {
                await _service.SaveAsync(notes);
                if (PersonaText == notes)
                    Status = "Saved.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Status = "Could not save your notes. Your edits are still here.";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
