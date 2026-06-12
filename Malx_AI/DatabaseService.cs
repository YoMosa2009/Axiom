using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace Malx_AI
{
    public class DatabaseService : IDisposable
    {
        private const string DatabasePath = "axiom_data.db";
        private const string OpenRouterApiKeySettingKey = "openrouter_api_key";
        private readonly SQLiteConnection _connection;
        private readonly object _gate = new();
        private bool _isInitialized;
        private bool _disposed;

        public bool IsReady => _isInitialized && !_disposed;

        public DatabaseService()
        {
            try
            {
                if (!File.Exists(DatabasePath))
                {
                    SQLiteConnection.CreateFile(DatabasePath);
                }

                string connectionString = $"Data Source={DatabasePath};Version=3;Journal Mode=WAL;Synchronous=Normal;";
                _connection = new SQLiteConnection(connectionString);
                _connection.Open();
                InitializeDatabase();
                _isInitialized = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DatabaseService init error: {ex.Message}");
                _isInitialized = false;
            }
        }

        public void DeleteChat(int chatId)
        {
            if (!IsReady || chatId <= 0) return;
            try
            {
                lock (_gate)
                {
                    using var command = _connection.CreateCommand();
                    command.CommandText = "DELETE FROM Chats WHERE Id = @id";
                    command.Parameters.AddWithValue("@id", chatId);
                    command.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DeleteChat error: {ex.Message}");
            }
        }

        private void InitializeDatabase()
        {
            lock (_gate)
            {
                using var command = _connection.CreateCommand();

                // Create Chats table
                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Chats (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        ChatName TEXT NOT NULL,
                        Content TEXT,
                        CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                        UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
                    )";
                command.ExecuteNonQuery();

                // Create UserFacts table
                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS UserFacts (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        FactKey TEXT UNIQUE NOT NULL,
                        FactValue TEXT,
                        CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                        UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
                    )";
                command.ExecuteNonQuery();

                // Create Settings table
                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Settings (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        SettingKey TEXT UNIQUE NOT NULL,
                        SettingValue TEXT,
                        UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
                    )";
                command.ExecuteNonQuery();
            }
        }

        public void SaveChat(int chatId, string chatName, string content)
        {
            if (!IsReady) return;
            try
            {
                lock (_gate)
                {
                    using var command = _connection.CreateCommand();

                    if (chatId == 0)
                    {
                        command.CommandText = @"
                            INSERT INTO Chats (ChatName, Content, CreatedAt, UpdatedAt)
                            VALUES (@chatName, @content, @now, @now)";
                    }
                    else
                    {
                        command.CommandText = @"
                            UPDATE Chats SET Content = @content, UpdatedAt = @now
                            WHERE Id = @id";
                        command.Parameters.AddWithValue("@id", chatId);
                    }

                    command.Parameters.AddWithValue("@chatName", chatName);
                    command.Parameters.AddWithValue("@content", content ?? "");
                    command.Parameters.AddWithValue("@now", DateTime.Now);
                    command.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SaveChat error: {ex.Message}");
            }
        }

        public List<(int Id, string ChatName)> GetAllChats()
        {
            var chats = new List<(int, string)>();
            if (!IsReady) return chats;

            try
            {
                lock (_gate)
                {
                    using var command = _connection.CreateCommand();
                    command.CommandText = "SELECT Id, ChatName FROM Chats ORDER BY CreatedAt DESC";

                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        chats.Add((reader.GetInt32(0), reader.GetString(1)));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetAllChats error: {ex.Message}");
            }

            return chats;
        }

        public string GetChatContent(int chatId)
        {
            if (!IsReady) return "";
            try
            {
                lock (_gate)
                {
                    using var command = _connection.CreateCommand();
                    command.CommandText = "SELECT Content FROM Chats WHERE Id = @id";
                    command.Parameters.AddWithValue("@id", chatId);
                    var result = command.ExecuteScalar();
                    return result?.ToString() ?? "";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetChatContent error: {ex.Message}");
                return "";
            }
        }

        public void SaveUserFact(string key, string value)
        {
            if (!IsReady) return;
            try
            {
                lock (_gate)
                {
                    using var command = _connection.CreateCommand();
                    command.CommandText = @"
                        INSERT OR REPLACE INTO UserFacts (FactKey, FactValue, CreatedAt, UpdatedAt)
                        VALUES (@key, @value, COALESCE((SELECT CreatedAt FROM UserFacts WHERE FactKey = @key), @now), @now)";

                    command.Parameters.AddWithValue("@key", key);
                    command.Parameters.AddWithValue("@value", value);
                    command.Parameters.AddWithValue("@now", DateTime.Now);
                    command.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SaveUserFact error: {ex.Message}");
            }
        }

        public string GetUserFact(string key)
        {
            if (!IsReady) return "";
            try
            {
                lock (_gate)
                {
                    using var command = _connection.CreateCommand();
                    command.CommandText = "SELECT FactValue FROM UserFacts WHERE FactKey = @key";
                    command.Parameters.AddWithValue("@key", key);
                    var result = command.ExecuteScalar();
                    return result?.ToString() ?? "";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetUserFact error: {ex.Message}");
                return "";
            }
        }

        public void SaveSetting(string key, string value)
        {
            if (!IsReady) return;
            try
            {
                lock (_gate)
                {
                    using var command = _connection.CreateCommand();
                    command.CommandText = @"
                        INSERT OR REPLACE INTO Settings (SettingKey, SettingValue, UpdatedAt)
                        VALUES (@key, @value, @now)";

                    command.Parameters.AddWithValue("@key", key);
                    command.Parameters.AddWithValue("@value", value);
                    command.Parameters.AddWithValue("@now", DateTime.Now);
                    command.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SaveSetting error: {ex.Message}");
            }
        }

        public string GetSetting(string key)
        {
            if (!IsReady) return "";
            try
            {
                lock (_gate)
                {
                    using var command = _connection.CreateCommand();
                    command.CommandText = "SELECT SettingValue FROM Settings WHERE SettingKey = @key";
                    command.Parameters.AddWithValue("@key", key);
                    var result = command.ExecuteScalar();
                    return result?.ToString() ?? "";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetSetting error: {ex.Message}");
                return "";
            }
        }

        public void SaveOpenRouterApiKey(string apiKey)
        {
            if (!IsReady) return;

            try
            {
                string normalized = (apiKey ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    SaveSetting(OpenRouterApiKeySettingKey, string.Empty);
                    return;
                }

                byte[] plainBytes = System.Text.Encoding.UTF8.GetBytes(normalized);
                byte[] protectedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
                string base64 = Convert.ToBase64String(protectedBytes);
                SaveSetting(OpenRouterApiKeySettingKey, base64);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SaveOpenRouterApiKey error: {ex.Message}");
                _ = BackendLogService.LogErrorAsync("DatabaseService.SaveOpenRouterApiKey", ex);
            }
        }

        public string? LoadOpenRouterApiKey()
        {
            if (!IsReady) return null;

            try
            {
                string stored = GetSetting(OpenRouterApiKeySettingKey);
                if (string.IsNullOrWhiteSpace(stored))
                    return null;

                byte[] protectedBytes = Convert.FromBase64String(stored);
                byte[] plainBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                string key = System.Text.Encoding.UTF8.GetString(plainBytes).Trim();
                return string.IsNullOrWhiteSpace(key) ? null : key;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoadOpenRouterApiKey error: {ex.Message}");
                _ = BackendLogService.LogErrorAsync("DatabaseService.LoadOpenRouterApiKey", ex);
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _connection?.Close();
                _connection?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DatabaseService.Dispose error: {ex.Message}");
            }
        }
    }
}
