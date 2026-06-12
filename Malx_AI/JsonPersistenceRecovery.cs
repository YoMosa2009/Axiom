using System;
using System.IO;
using System.Text.Json;

namespace Malx_AI
{
    internal sealed class JsonPersistenceRecoveryResult<T>
    {
        public T? Value { get; init; }
        public bool WasRecovered { get; init; }
        public string StatusMessage { get; init; } = string.Empty;
    }

    internal static class JsonPersistenceRecovery
    {
        public static JsonPersistenceRecoveryResult<T> Load<T>(string primaryPath, JsonSerializerOptions? options = null)
        {
            return Load(primaryPath, json => JsonSerializer.Deserialize<T>(json, options));
        }

        public static JsonPersistenceRecoveryResult<T> Load<T>(string primaryPath, Func<string, T?> deserialize)
        {
            string tempPath = primaryPath + ".tmp";
            string backupPath = primaryPath + ".bak";

            if (TryLoadCandidate(primaryPath, deserialize, out T? primaryValue, out _))
            {
                return new JsonPersistenceRecoveryResult<T> { Value = primaryValue };
            }

            bool primaryExists = File.Exists(primaryPath);
            string quarantinedPath = string.Empty;
            if (primaryExists)
                quarantinedPath = QuarantineCorruptedFile(primaryPath);

            if (TryLoadCandidate(tempPath, deserialize, out T? tempValue, out string tempJson))
            {
                AtomicFileWriter.WriteAllText(primaryPath, tempJson);
                return new JsonPersistenceRecoveryResult<T>
                {
                    Value = tempValue,
                    WasRecovered = true,
                    StatusMessage = primaryExists
                        ? $"Recovered persisted state from a temporary snapshot after quarantining a corrupted file to '{Path.GetFileName(quarantinedPath)}'."
                        : "Recovered persisted state from a temporary snapshot."
                };
            }

            if (TryLoadCandidate(backupPath, deserialize, out T? backupValue, out string backupJson))
            {
                AtomicFileWriter.WriteAllText(primaryPath, backupJson);
                return new JsonPersistenceRecoveryResult<T>
                {
                    Value = backupValue,
                    WasRecovered = true,
                    StatusMessage = primaryExists
                        ? $"Recovered persisted state from the last known-good backup after quarantining a corrupted file to '{Path.GetFileName(quarantinedPath)}'."
                        : "Recovered persisted state from the last known-good backup."
                };
            }

            return new JsonPersistenceRecoveryResult<T>
            {
                Value = default,
                WasRecovered = false,
                StatusMessage = primaryExists
                    ? $"Persisted state file was corrupted and could not be recovered. Quarantined copy: '{Path.GetFileName(quarantinedPath)}'."
                    : string.Empty
            };
        }

        private static bool TryLoadCandidate<T>(string path, Func<string, T?> deserialize, out T? value, out string json)
        {
            value = default;
            json = string.Empty;
            if (!File.Exists(path))
                return false;

            try
            {
                json = File.ReadAllText(path);
                value = deserialize(json);
                return value != null;
            }
            catch
            {
                value = default;
                json = string.Empty;
                return false;
            }
        }

        private static string QuarantineCorruptedFile(string path)
        {
            try
            {
                string quarantinePath = path + $".corrupt-{DateTime.Now:yyyyMMdd_HHmmss}";
                File.Move(path, quarantinePath, true);
                return quarantinePath;
            }
            catch
            {
                return path;
            }
        }
    }
}