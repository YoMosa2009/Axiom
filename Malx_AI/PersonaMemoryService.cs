using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI
{
    public sealed class PersonaMemoryService
    {
        internal static PersonaMemoryService Shared { get; } = new();
        private readonly string _filePath;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private string? _cachedText;

        public PersonaMemoryService() : this(Path.Combine(AppDataPaths.Root, "persona-memory.json")) { }

        internal PersonaMemoryService(string filePath) => _filePath = filePath;

        internal PersonaReplyPresentation Presentation { get; private set; }

        internal string LoadStatus { get; private set; } = string.Empty;

        /// <summary>Whether Normal Chat may give the notes to the model as background. On by default.</summary>
        internal bool ShareWithModel { get; private set; } = true;

        /// <summary>The background block for a Normal Chat turn, or empty when sharing is off or there are no notes.</summary>
        public async Task<string> GetModelContextAsync(CancellationToken token = default)
        {
            string notes = await LoadAsync(token).ConfigureAwait(false);
            return ShareWithModel ? PersonaModelContext.Build(notes) : string.Empty;
        }

        public async Task SetShareWithModelAsync(bool share, CancellationToken token = default)
        {
            string notes = await LoadAsync(token).ConfigureAwait(false);
            await SaveAsync(notes, share, token).ConfigureAwait(false);
        }

        public async Task<string> LoadAsync(CancellationToken token = default)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_cachedText is not null)
                    return _cachedText;

                try
                {
                    var result = await Task.Run(() => JsonPersistenceRecovery.Load<PersonaMemoryDto>(_filePath), token)
                        .ConfigureAwait(false);
                    LoadStatus = result.StatusMessage;
                    _cachedText = result.Value?.Text ?? string.Empty;
                    ShareWithModel = result.Value?.ShareWithModel ?? true;
                    Presentation = PersonaReplyPresentation.FromNotes(_cachedText);
                    return _cachedText;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LoadStatus = "Persona notes could not be read.";
                    return string.Empty;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public Task SaveAsync(string text, CancellationToken token = default)
            => SaveAsync(text, ShareWithModel, token);

        public async Task SaveAsync(string text, bool shareWithModel, CancellationToken token = default)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                string savedText = text ?? string.Empty;
                string json = JsonSerializer.Serialize(new PersonaMemoryDto
                {
                    Text = savedText,
                    ShareWithModel = shareWithModel,
                    UpdatedAt = DateTime.UtcNow
                });
                await AtomicFileWriter.WriteAllTextAsync(_filePath, json, token).ConfigureAwait(false);
                _cachedText = savedText;
                ShareWithModel = shareWithModel;
                Presentation = PersonaReplyPresentation.FromNotes(savedText);
                LoadStatus = string.Empty;
            }
            finally
            {
                _gate.Release();
            }
        }

        private sealed class PersonaMemoryDto
        {
            public string Text { get; set; } = string.Empty;
            public bool ShareWithModel { get; set; } = true;
            public DateTime UpdatedAt { get; set; }
        }
    }
}
