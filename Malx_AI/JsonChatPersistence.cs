using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI
{
    public class ChatIndexEntry
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public DateTime UpdatedAt { get; set; }
        public bool IsPinned { get; set; }
    }

    public sealed class ChatSearchResult
    {
        public int ChatId { get; init; }
        public string ChatName { get; init; } = string.Empty;
        public Guid? MessageId { get; init; }
        public string Snippet { get; init; } = string.Empty;
        public DateTime UpdatedAt { get; init; }
        public bool IsPinned { get; init; }
    }

    public class JsonChatPersistence
    {
        private const string IndexFile = "chats_index.json";

        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
        private List<ChatIndexEntry>? _cachedIndex;
        private readonly object _indexLock = new();
        private readonly string _chatHistoryFolder;

        public JsonChatPersistence(string? chatHistoryFolder = null)
        {
            _chatHistoryFolder = string.IsNullOrWhiteSpace(chatHistoryFolder)
                ? AppDataPaths.ChatHistory
                : Path.GetFullPath(chatHistoryFolder);
            Directory.CreateDirectory(_chatHistoryFolder);
        }

        public void SaveChat(ChatSession chat)
        {
            try
            {
                var filePath = Path.Combine(_chatHistoryFolder, $"chat_{chat.Id}.json");
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
                var filePath = Path.Combine(_chatHistoryFolder, $"chat_{chatId}.json");
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
                var filePath = Path.Combine(_chatHistoryFolder, $"chat_{chatId}.json");
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
                var filePath = Path.Combine(_chatHistoryFolder, $"chat_{chat.Id}.json");
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
                var filePath = Path.Combine(_chatHistoryFolder, $"chat_{chatId}.json");
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
                var filePath = Path.Combine(_chatHistoryFolder, $"chat_{chatId}.json");
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
                return LoadOrGetCachedIndex().Select(CloneIndexEntry).ToList();
        }

        public void UpdateChatMetadata(int chatId, string? name = null, bool? isPinned = null)
        {
            string indexJson;
            string indexPath = Path.Combine(_chatHistoryFolder, IndexFile);
            lock (_indexLock)
            {
                List<ChatIndexEntry> index = LoadOrGetCachedIndex();
                ChatIndexEntry? entry = index.FirstOrDefault(item => item.Id == chatId);
                if (entry is null)
                {
                    if (string.IsNullOrWhiteSpace(name))
                        return;
                    entry = new ChatIndexEntry
                    {
                        Id = chatId,
                        Name = name.Trim(),
                        UpdatedAt = DateTime.Now
                    };
                    index.Add(entry);
                }

                if (!string.IsNullOrWhiteSpace(name))
                    entry.Name = name.Trim();
                if (isPinned.HasValue)
                    entry.IsPinned = isPinned.Value;
                _cachedIndex = index;
                indexJson = JsonSerializer.Serialize(index, WriteOptions);
            }
            AtomicFileWriter.WriteAllText(indexPath, indexJson);
        }

        public Task<List<ChatSearchResult>> SearchChatsAsync(
            string query,
            int maxResults = 50,
            CancellationToken cancellationToken = default)
        {
            string normalizedQuery = (query ?? string.Empty).Trim();
            if (normalizedQuery.Length == 0 || maxResults <= 0)
                return Task.FromResult(new List<ChatSearchResult>());

            List<ChatIndexEntry> index = GetChatIndex();
            return Task.Run(() =>
            {
                var results = new List<ChatSearchResult>();
                foreach (ChatIndexEntry entry in index
                    .OrderByDescending(item => item.IsPinned)
                    .ThenByDescending(item => item.UpdatedAt))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.Name.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new ChatSearchResult
                        {
                            ChatId = entry.Id,
                            ChatName = entry.Name,
                            Snippet = "Title match",
                            UpdatedAt = entry.UpdatedAt,
                            IsPinned = entry.IsPinned
                        });
                    }

                    ChatSession? session = LoadChat(entry.Id);
                    int matchesInChat = 0;
                    foreach (ChatMessage message in session?.Messages ?? [])
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(message.Content)
                            || !message.Content.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                            continue;

                        results.Add(new ChatSearchResult
                        {
                            ChatId = entry.Id,
                            ChatName = entry.Name,
                            MessageId = message.Id,
                            Snippet = BuildSearchSnippet(message.Content, normalizedQuery),
                            UpdatedAt = entry.UpdatedAt,
                            IsPinned = entry.IsPinned
                        });
                        matchesInChat++;
                        if (matchesInChat >= 3 || results.Count >= maxResults)
                            break;
                    }

                    if (results.Count >= maxResults)
                        break;
                }
                return results.Take(maxResults).ToList();
            }, cancellationToken);
        }

        internal static string BuildSearchSnippet(string content, string query, int maxLength = 120)
        {
            string normalized = Regex.Replace(content ?? string.Empty, @"\s+", " ").Trim();
            if (normalized.Length <= maxLength)
                return normalized;

            int match = normalized.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            int start = Math.Max(0, match - maxLength / 3);
            if (start + maxLength > normalized.Length)
                start = normalized.Length - maxLength;
            string snippet = normalized.Substring(start, maxLength).Trim();
            return (start > 0 ? "..." : string.Empty)
                + snippet
                + (start + maxLength < normalized.Length ? "..." : string.Empty);
        }

        // Must be called under _indexLock.
        private List<ChatIndexEntry> LoadOrGetCachedIndex()
        {
            if (_cachedIndex != null)
                return _cachedIndex;
            try
            {
                var indexPath = Path.Combine(_chatHistoryFolder, IndexFile);
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

        private static ChatIndexEntry CloneIndexEntry(ChatIndexEntry entry) => new()
        {
            Id = entry.Id,
            Name = entry.Name,
            UpdatedAt = entry.UpdatedAt,
            IsPinned = entry.IsPinned
        };

        private void UpdateIndexIncremental(int chatId, string chatName, DateTime updatedAt)
        {
            try
            {
                string indexPath = Path.Combine(_chatHistoryFolder, IndexFile);
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
                string indexPath = Path.Combine(_chatHistoryFolder, IndexFile);
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
                string indexPath = Path.Combine(_chatHistoryFolder, IndexFile);
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
                string indexPath = Path.Combine(_chatHistoryFolder, IndexFile);
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
