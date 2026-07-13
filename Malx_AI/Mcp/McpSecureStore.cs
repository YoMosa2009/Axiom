using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Malx_AI.Mcp
{
    /// <summary>
    /// DPAPI-backed persistence for MCP connector tokens.
    /// Writes both SQLite settings and a side-car file so connections survive restarts
    /// even if one store fails.
    /// </summary>
    internal sealed class McpSecureStore
    {
        private const string StateSettingKey = "mcp_connector_state_v2";
        private readonly DatabaseService? _database;
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false,
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = null // keep PascalCase property names stable
        };

        private static string SideCarPath => Path.Combine(AppDataPaths.Root, "mcp_connector_state.dpapi");

        public McpSecureStore(DatabaseService? database)
        {
            _database = database;
        }

        public McpConnectorStateFile Load()
        {
            // Prefer the richer of DB vs side-car (whichever has more tokens).
            McpConnectorStateFile fromDb = TryLoadFromDatabase();
            McpConnectorStateFile fromFile = TryLoadFromSideCar();

            int dbCount = fromDb.Tokens?.Count ?? 0;
            int fileCount = fromFile.Tokens?.Count ?? 0;

            McpConnectorStateFile chosen;
            if (fileCount > dbCount)
                chosen = fromFile;
            else if (dbCount > 0)
                chosen = fromDb;
            else
                chosen = fromFile.Tokens?.Count > 0 ? fromFile : fromDb;

            chosen.Tokens ??= new Dictionary<string, McpTokenBundle>(StringComparer.OrdinalIgnoreCase);

            // Heal: if one store is behind, write the winner to both.
            if (chosen.Tokens.Count > 0)
            {
                try { Save(chosen); }
                catch { /* best-effort heal */ }
            }

            return chosen;
        }

        public void Save(McpConnectorStateFile state)
        {
            if (state == null)
                return;

            state.Tokens ??= new Dictionary<string, McpTokenBundle>(StringComparer.OrdinalIgnoreCase);
            string json = JsonSerializer.Serialize(state, JsonOptions);
            string protectedPayload = Protect(json);

            // Side-car file (primary durability for reconnect-across-restart).
            try
            {
                Directory.CreateDirectory(AppDataPaths.Root);
                File.WriteAllText(SideCarPath, protectedPayload, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"McpSecureStore.Save side-car error: {ex.Message}");
                _ = BackendLogService.LogErrorAsync("McpSecureStore.SaveSideCar", ex);
            }

            // SQLite settings (secondary).
            if (_database != null && _database.IsReady)
            {
                try
                {
                    _database.SaveSetting(StateSettingKey, protectedPayload);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"McpSecureStore.Save database error: {ex.Message}");
                    _ = BackendLogService.LogErrorAsync("McpSecureStore.SaveDatabase", ex);
                }
            }
        }

        private McpConnectorStateFile TryLoadFromDatabase()
        {
            if (_database == null || !_database.IsReady)
                return Empty();

            try
            {
                string stored = _database.GetSetting(StateSettingKey);
                // Migrate from v1 key if present.
                if (string.IsNullOrWhiteSpace(stored))
                    stored = _database.GetSetting("mcp_connector_state");
                return DeserializeProtected(stored);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"McpSecureStore.Load database error: {ex.Message}");
                _ = BackendLogService.LogErrorAsync("McpSecureStore.LoadDatabase", ex);
                return Empty();
            }
        }

        private McpConnectorStateFile TryLoadFromSideCar()
        {
            try
            {
                if (!File.Exists(SideCarPath))
                    return Empty();
                string stored = File.ReadAllText(SideCarPath, Encoding.UTF8);
                return DeserializeProtected(stored);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"McpSecureStore.Load side-car error: {ex.Message}");
                _ = BackendLogService.LogErrorAsync("McpSecureStore.LoadSideCar", ex);
                return Empty();
            }
        }

        private static McpConnectorStateFile DeserializeProtected(string? stored)
        {
            if (string.IsNullOrWhiteSpace(stored))
                return Empty();

            string json = Unprotect(stored);
            if (string.IsNullOrWhiteSpace(json))
                return Empty();

            McpConnectorStateFile? state = JsonSerializer.Deserialize<McpConnectorStateFile>(json, JsonOptions);
            if (state == null)
                return Empty();

            // Rebuild dictionary with case-insensitive keys.
            var tokens = new Dictionary<string, McpTokenBundle>(StringComparer.OrdinalIgnoreCase);
            if (state.Tokens != null)
            {
                foreach (var kv in state.Tokens)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null)
                        continue;
                    tokens[kv.Key] = kv.Value;
                }
            }
            state.Tokens = tokens;
            return state;
        }

        private static McpConnectorStateFile Empty()
            => new() { Tokens = new Dictionary<string, McpTokenBundle>(StringComparer.OrdinalIgnoreCase) };

        private static string Protect(string plainText)
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText ?? string.Empty);
            byte[] protectedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        private static string Unprotect(string protectedBase64)
        {
            try
            {
                byte[] protectedBytes = Convert.FromBase64String(protectedBase64.Trim());
                byte[] plainBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch
            {
                if (protectedBase64.TrimStart().StartsWith("{", StringComparison.Ordinal))
                    return protectedBase64;
                throw;
            }
        }
    }
}
