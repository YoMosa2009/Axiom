using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Malx_AI
{
    public sealed class CouncilTaskHistoryEntry
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid? ParentId { get; set; }
        public string UserPrompt { get; set; } = "";
        public string Objective { get; set; } = "";
        public string TaskType { get; set; } = "General";
        public string Complexity { get; set; } = "Moderate";
        public string ArchitectOutput { get; set; } = "";
        public string BuilderOutput { get; set; } = "";
        public string CriticFindings { get; set; } = "";
        public string FinalResult { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public bool RevisionTriggered { get; set; }
        public int CriticFindingCount { get; set; }
        public string ConfidenceLabel { get; set; } = "Moderate Confidence";

        public override string ToString()
        {
            string shortPrompt = string.IsNullOrWhiteSpace(UserPrompt) ? "(empty)" : (UserPrompt.Length > 40 ? UserPrompt[..40] + "..." : UserPrompt);
            return $"{Timestamp:HH:mm:ss} | {TaskType} | {shortPrompt}";
        }
    }

    public sealed class ModelPerformanceLogEntry
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string ArchitectModel { get; set; } = "";
        public string BuilderModel { get; set; } = "";
        public string CriticModel { get; set; } = "";
        public int ArchitectTokens { get; set; }
        public int BuilderTokens { get; set; }
        public int CriticTokens { get; set; }
        public double ArchitectDurationSeconds { get; set; }
        public double BuilderDurationSeconds { get; set; }
        public double CriticDurationSeconds { get; set; }
        public bool RevisionTriggered { get; set; }
        public int CriticFindingCountBeforeRevision { get; set; }
        public string FinalConfidenceLabel { get; set; } = "Moderate Confidence";
        public string TaskType { get; set; } = "General";

        public override string ToString() => $"{Timestamp:HH:mm:ss} | {TaskType} | {FinalConfidenceLabel} | {(RevisionTriggered ? "Revised" : "Direct")}";
    }

    public sealed class WorkspaceTemplateEntry
    {
        public string Name { get; set; } = "";
        public string SystemPrompt { get; set; } = "";
        public string Objective { get; set; } = "";
        public string TaskTypeOverride { get; set; } = "";
        public string ArchitectModelPath { get; set; } = "";
        public string BuilderModelPath { get; set; } = "";
        public string CriticModelPath { get; set; } = "";
        public string ArchitectDisplayName { get; set; } = "";
        public string BuilderDisplayName { get; set; } = "";
        public string CriticDisplayName { get; set; } = "";

        public override string ToString() => Name;
    }

    public sealed class PinnedMessageEntry
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Role { get; set; } = "assistant";
        public string Content { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    public sealed class PromptTemplateEntry
    {
        public string Category { get; set; } = "General";
        public string Text { get; set; } = "";
        public bool IsCustom { get; set; }
    }

    public sealed class SystemPromptPresetEntry
    {
        public string Name { get; set; } = "";
        public string Prompt { get; set; } = "";
        public bool IsBuiltIn { get; set; }
    }

    public sealed class WorkspaceAdvancedStateSnapshot
    {
        public List<CouncilTaskHistoryEntry> TaskHistory { get; set; } = new();
        public List<WorkspaceTemplateEntry> Templates { get; set; } = new();
        public string CriticSensitivity { get; set; } = "Standard";
        public List<ModelPerformanceLogEntry> PerformanceLog { get; set; } = new();
    }

    public sealed class ChatBranch
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "Main";
        public int ForkMessageIndex { get; set; }
        public List<ChatMessageState> Messages { get; set; } = new();

        public override string ToString() => Name;
    }

    public sealed class ChatAdvancedStateSnapshot
    {
        public List<ChatBranch> Branches { get; set; } = new();
        public Guid ActiveBranchId { get; set; }
        public List<PinnedMessageEntry> PinnedMessages { get; set; } = new();
        public List<PromptTemplateEntry> PromptTemplates { get; set; } = new();
        public List<SystemPromptPresetEntry> SystemPromptPresets { get; set; } = new();
        public string ActiveSystemPrompt { get; set; } = "";
        public List<WorkplaceChatStateEntry> WorkplaceChats { get; set; } = new();
        public int CurrentWorkplaceChatId { get; set; }
        public int WorkplaceChatCounter { get; set; }
    }

    public sealed class WorkplaceChatStateEntry
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public WorkplaceSessionSnapshot Snapshot { get; set; } = new();
    }

    public sealed class WorkspaceAdvancedStatePersistence
    {
        private readonly string _path = Path.Combine(AppDataPaths.ChatHistory, "workplace_advanced_state.json");
        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
        public string LastLoadStatusMessage { get; private set; } = string.Empty;
        public bool LastLoadRecovered { get; private set; }

        public void Save(WorkspaceAdvancedStateSnapshot snapshot)
        {
            Directory.CreateDirectory(AppDataPaths.ChatHistory);
            AtomicFileWriter.WriteAllText(_path, JsonSerializer.Serialize(snapshot, WriteOptions));
        }

        public WorkspaceAdvancedStateSnapshot? Load()
        {
            var result = JsonPersistenceRecovery.Load<WorkspaceAdvancedStateSnapshot>(_path);
            LastLoadStatusMessage = result.StatusMessage;
            LastLoadRecovered = result.WasRecovered;
            return result.Value;
        }
    }

    public sealed class ChatAdvancedStatePersistence
    {
        private readonly string _path = Path.Combine(AppDataPaths.ChatHistory, "chat_advanced_state.json");
        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
        public string LastLoadStatusMessage { get; private set; } = string.Empty;
        public bool LastLoadRecovered { get; private set; }

        public void Save(ChatAdvancedStateSnapshot snapshot)
        {
            Directory.CreateDirectory(AppDataPaths.ChatHistory);
            AtomicFileWriter.WriteAllText(_path, JsonSerializer.Serialize(snapshot, WriteOptions));
        }

        public ChatAdvancedStateSnapshot? Load()
        {
            var result = JsonPersistenceRecovery.Load<ChatAdvancedStateSnapshot>(_path);
            LastLoadStatusMessage = result.StatusMessage;
            LastLoadRecovered = result.WasRecovered;
            return result.Value;
        }

        public static List<PromptTemplateEntry> GetDefaultPromptTemplates() => new()
        {
            new() { Category = "Coding", Text = "explain this code" },
            new() { Category = "Coding", Text = "find the bug in this code" },
            new() { Category = "Coding", Text = "write a function that does X" },
            new() { Category = "Coding", Text = "refactor this for readability" },
            new() { Category = "Coding", Text = "add error handling to this code" },
            new() { Category = "Analysis", Text = "summarize this text" },
            new() { Category = "Analysis", Text = "compare these two options" },
            new() { Category = "Analysis", Text = "list the pros and cons of X" },
            new() { Category = "Analysis", Text = "identify the key points in this document" },
            new() { Category = "Writing", Text = "improve the clarity of this text" },
            new() { Category = "Writing", Text = "make this more concise" },
            new() { Category = "Writing", Text = "rewrite this in a more formal tone" },
            new() { Category = "Research", Text = "explain this concept simply" },
            new() { Category = "Research", Text = "what are the key differences between X and Y" },
            new() { Category = "Research", Text = "give me an overview of X" }
        };

        public static List<SystemPromptPresetEntry> GetBuiltInSystemPromptPresets() => new()
        {
            new()
            {
                Name = "Coding Assistant",
                Prompt = "You are a precise coding assistant. Provide technically correct, runnable solutions and concise implementation-focused reasoning.",
                IsBuiltIn = true
            },
            new()
            {
                Name = "Research Assistant",
                Prompt = "You are a research assistant. Provide thorough factual analysis, clearly structured responses, and avoid unsupported claims.",
                IsBuiltIn = true
            },
            new()
            {
                Name = "Concise Mode",
                Prompt = "You are concise. Keep responses as brief as possible without sacrificing factual accuracy.",
                IsBuiltIn = true
            },
            new()
            {
                Name = "Creative Mode",
                Prompt = "You are creative and exploratory while remaining useful and coherent. Offer varied ideas and directions.",
                IsBuiltIn = true
            }
        };

        public static string GetMostFrequentModelCombo(IEnumerable<ModelPerformanceLogEntry> entries)
        {
            return entries
                .GroupBy(e => $"A:{e.ArchitectModel}|B:{e.BuilderModel}|C:{e.CriticModel}")
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault() ?? "n/a";
        }
    }
}
