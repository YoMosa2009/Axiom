using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI
{
    public sealed class ChatWorkspaceSnapshot
    {
        public string ModelName { get; set; } = "No Model Loaded";
        public int CurrentChatId { get; set; } = -1;
        public List<ChatMessageState> Messages { get; set; } = new();
        public List<ChatDocumentAttachment> AttachedDocuments { get; set; } = new();
        public DateTime SavedAt { get; set; } = DateTime.Now;
    }

    public sealed class ChatMessageState
    {
        public string Role { get; set; } = "system";
        public string Content { get; set; } = "";
        public string ModelLabel { get; set; } = "";
        public string ThinkingContent { get; set; } = "";
        public string ThinkingHeaderText { get; set; } = "Thinking";
        public Guid Id { get; set; } = Guid.NewGuid();
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string Importance { get; set; } = "Low";
        public bool IsCompactionProtected { get; set; }
    }

    public sealed class ChatWorkspaceStatePersistence
    {
        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
        private readonly string _filePath;
        public string LastLoadStatusMessage { get; private set; } = string.Empty;
        public bool LastLoadRecovered { get; private set; }

        public ChatWorkspaceStatePersistence()
        {
            Directory.CreateDirectory("ChatHistory");
            _filePath = Path.Combine("ChatHistory", "chat_workspace_state.json");
        }

        public async Task SaveAsync(ChatWorkspaceSnapshot snapshot, CancellationToken token = default)
        {
            string json = JsonSerializer.Serialize(snapshot, WriteOptions);
            await AtomicFileWriter.WriteAllTextAsync(_filePath, json, token);
        }

        public void Save(ChatWorkspaceSnapshot snapshot)
        {
            string json = JsonSerializer.Serialize(snapshot, WriteOptions);
            AtomicFileWriter.WriteAllText(_filePath, json);
        }

        public async Task<ChatWorkspaceSnapshot?> LoadAsync(CancellationToken token = default)
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();

            var result = JsonPersistenceRecovery.Load<ChatWorkspaceSnapshot>(_filePath);
            LastLoadStatusMessage = result.StatusMessage;
            LastLoadRecovered = result.WasRecovered;
            return result.Value;
        }
    }
}
