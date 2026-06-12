using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Malx_AI
{
    public class ChatIndexEntry
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public DateTime UpdatedAt { get; set; }
    }

    public class JsonChatPersistence
    {
        private const string ChatHistoryFolder = "ChatHistory";
        private const string IndexFile = "chats_index.json";

        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
        private List<ChatIndexEntry>? _cachedIndex;
        private readonly object _indexLock = new();

        public JsonChatPersistence()
        {
            if (!Directory.Exists(ChatHistoryFolder))
                Directory.CreateDirectory(ChatHistoryFolder);
        }

        public void SaveChat(ChatSession chat)
        {
            try
            {
                var filePath = Path.Combine(ChatHistoryFolder, $"chat_{chat.Id}.json");
                var json = JsonSerializer.Serialize(chat, WriteOptions);
                AtomicFileWriter.WriteAllText(filePath, json);
                UpdateIndexIncremental(chat.Id, chat.Name, chat.UpdatedAt);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving chat: {ex.Message}");
            }
        }

        public void DeleteChat(int chatId)
        {
            try
            {
                var filePath = Path.Combine(ChatHistoryFolder, $"chat_{chatId}.json");
                if (File.Exists(filePath))
                    File.Delete(filePath);

                RemoveFromIndex(chatId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error deleting chat: {ex.Message}");
            }
        }

        public async Task DeleteChatAsync(int chatId)
        {
            try
            {
                var filePath = Path.Combine(ChatHistoryFolder, $"chat_{chatId}.json");
                if (File.Exists(filePath))
                    File.Delete(filePath);

                await RemoveFromIndexAsync(chatId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error deleting chat async: {ex.Message}");
            }
        }

        public async Task SaveChatAsync(ChatSession chat)
        {
            try
            {
                var filePath = Path.Combine(ChatHistoryFolder, $"chat_{chat.Id}.json");
                var json = JsonSerializer.Serialize(chat, WriteOptions);
                await AtomicFileWriter.WriteAllTextAsync(filePath, json);
                await UpdateIndexIncrementalAsync(chat.Id, chat.Name, chat.UpdatedAt);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving chat async: {ex.Message}");
            }
        }

        public ChatSession LoadChat(int chatId)
        {
            try
            {
                var filePath = Path.Combine(ChatHistoryFolder, $"chat_{chatId}.json");
                if (!File.Exists(filePath))
                    return null;

                var json = File.ReadAllText(filePath);
                return JsonSerializer.Deserialize<ChatSession>(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading chat: {ex.Message}");
                return null;
            }
        }

        public async Task<ChatSession?> LoadChatAsync(int chatId)
        {
            try
            {
                var filePath = Path.Combine(ChatHistoryFolder, $"chat_{chatId}.json");
                if (!File.Exists(filePath))
                    return null;

                var json = await File.ReadAllTextAsync(filePath);
                return JsonSerializer.Deserialize<ChatSession>(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading chat async: {ex.Message}");
                return null;
            }
        }

        public List<ChatIndexEntry> GetChatIndex()
        {
            lock (_indexLock)
                return LoadOrGetCachedIndex();
        }

        // Must be called under _indexLock.
        private List<ChatIndexEntry> LoadOrGetCachedIndex()
        {
            if (_cachedIndex != null)
                return _cachedIndex;
            try
            {
                var indexPath = Path.Combine(ChatHistoryFolder, IndexFile);
                if (!File.Exists(indexPath))
                {
                    _cachedIndex = new List<ChatIndexEntry>();
                    return _cachedIndex;
                }
                var json = File.ReadAllText(indexPath);
                _cachedIndex = JsonSerializer.Deserialize<List<ChatIndexEntry>>(json) ?? new List<ChatIndexEntry>();
                return _cachedIndex;
            }
            catch
            {
                _cachedIndex = new List<ChatIndexEntry>();
                return _cachedIndex;
            }
        }

        private void UpdateIndexIncremental(int chatId, string chatName, DateTime updatedAt)
        {
            try
            {
                string indexPath = Path.Combine(ChatHistoryFolder, IndexFile);
                string indexJson;
                lock (_indexLock)
                {
                    var index = LoadOrGetCachedIndex();
                    var existing = index.FindIndex(e => e.Id == chatId);
                    if (existing >= 0) { index[existing].Name = chatName; index[existing].UpdatedAt = updatedAt; }
                    else index.Add(new ChatIndexEntry { Id = chatId, Name = chatName, UpdatedAt = updatedAt });
                    index.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
                    _cachedIndex = index;
                    indexJson = JsonSerializer.Serialize(index, WriteOptions);
                }
                AtomicFileWriter.WriteAllText(indexPath, indexJson);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating index: {ex.Message}");
            }
        }

        private async Task UpdateIndexIncrementalAsync(int chatId, string chatName, DateTime updatedAt)
        {
            try
            {
                string indexPath = Path.Combine(ChatHistoryFolder, IndexFile);
                string indexJson;
                lock (_indexLock)
                {
                    var index = LoadOrGetCachedIndex();
                    var existing = index.FindIndex(e => e.Id == chatId);
                    if (existing >= 0) { index[existing].Name = chatName; index[existing].UpdatedAt = updatedAt; }
                    else index.Add(new ChatIndexEntry { Id = chatId, Name = chatName, UpdatedAt = updatedAt });
                    index.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
                    _cachedIndex = index;
                    indexJson = JsonSerializer.Serialize(index, WriteOptions);
                }
                await AtomicFileWriter.WriteAllTextAsync(indexPath, indexJson);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating index async: {ex.Message}");
            }
        }

        private void RemoveFromIndex(int chatId)
        {
            try
            {
                string indexPath = Path.Combine(ChatHistoryFolder, IndexFile);
                string indexJson;
                lock (_indexLock)
                {
                    var index = LoadOrGetCachedIndex();
                    index.RemoveAll(e => e.Id == chatId);
                    _cachedIndex = index;
                    indexJson = JsonSerializer.Serialize(index, WriteOptions);
                }
                AtomicFileWriter.WriteAllText(indexPath, indexJson);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error removing chat from index: {ex.Message}");
            }
        }

        private async Task RemoveFromIndexAsync(int chatId)
        {
            try
            {
                string indexPath = Path.Combine(ChatHistoryFolder, IndexFile);
                string indexJson;
                lock (_indexLock)
                {
                    var index = LoadOrGetCachedIndex();
                    index.RemoveAll(e => e.Id == chatId);
                    _cachedIndex = index;
                    indexJson = JsonSerializer.Serialize(index, WriteOptions);
                }
                await AtomicFileWriter.WriteAllTextAsync(indexPath, indexJson);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error removing chat from index async: {ex.Message}");
            }
        }
    }
}
