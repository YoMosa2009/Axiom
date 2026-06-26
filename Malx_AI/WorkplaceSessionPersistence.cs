using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Malx_AI
{
    public sealed class WorkplaceSessionSnapshot
    {
        public string ObjectiveText { get; set; } = "";
        public string ProjectCanvasText { get; set; } = "";
        public bool CloudModeEnabled { get; set; }
        public uint GlobalContextSize { get; set; } = 8192;
        public uint ArchitectContextSize { get; set; } = 8192;
        public uint BuilderContextSize { get; set; } = 8192;
        public uint CriticContextSize { get; set; } = 8192;
        public bool AutoOptimizeRoleContexts { get; set; } = true;
        public List<WorkplaceChatMessageDto> ChatCards { get; set; } = new();
        public List<WorkplaceDocumentDto> Documents { get; set; } = new();
        public List<WorkplaceChatMessageDto> SystemNotifications { get; set; } = new();
        public List<CouncilTaskHistoryEntry> TaskHistory { get; set; } = new();
        public List<ModelPerformanceLogEntry> PerformanceLog { get; set; } = new();
        public bool IsRunStateIsolated { get; set; }
        public Dictionary<string, WorkplaceCouncilModelDto> CouncilModels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<SessionHippocampusEntry> HippocampusEntries { get; set; } = new();
        public bool StudySessionCompleted { get; set; }
        public int StudySessionProcessedDocumentCount { get; set; }
        public int CompletedCouncilRunCount { get; set; }
        public string LastSandboxOutput { get; set; } = "";
        public string LastFinalOutput { get; set; } = "";
        public string LastConfidenceLabel { get; set; } = "Moderate Confidence";
        public string CanvasDiffBaseSource { get; set; } = "";
        public string CanvasDiffCurrentSource { get; set; } = "";
        public int CanvasDiffAdditionCount { get; set; }
        public int CanvasDiffRemovalCount { get; set; }
        public DateTime SavedAt { get; set; } = DateTime.Now;
    }

    public sealed class WorkplaceChatMessageDto
    {
        public string Role { get; set; } = "system";
        public string Content { get; set; } = "";
        public DateTime Timestamp { get; set; }
    }

    public sealed class WorkplaceDocumentDto
    {
        public string Name { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string Type { get; set; } = "";
        public string Info { get; set; } = "";
        public int ChunkCount { get; set; }
    }

    public sealed class WorkplaceCouncilModelDto
    {
        public string ModelPath { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Format { get; set; } = "ChatML";
        public bool UseCloud { get; set; }
        public string CloudModelId { get; set; } = OpenRouterChatService.WorkplaceCouncilDefaultModelId;
    }

    public sealed class WorkplaceSessionPersistence
    {
        private static readonly string SessionFolder = AppDataPaths.ChatHistory;
        private const string SessionFile = "workplace_session.json";

        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
        public string LastLoadStatusMessage { get; private set; } = string.Empty;
        public bool LastLoadRecovered { get; private set; }

        public void Save(WorkplaceSessionSnapshot snapshot)
        {
            Directory.CreateDirectory(SessionFolder);
            string path = Path.Combine(SessionFolder, SessionFile);
            string json = JsonSerializer.Serialize(snapshot, WriteOptions);
            AtomicFileWriter.WriteAllText(path, json);
        }

        public WorkplaceSessionSnapshot? Load()
        {
            string path = Path.Combine(SessionFolder, SessionFile);
            var result = JsonPersistenceRecovery.Load<WorkplaceSessionSnapshot>(path);
            LastLoadStatusMessage = result.StatusMessage;
            LastLoadRecovered = result.WasRecovered;
            return result.Value;
        }

        public string ExportArtifacts(string baseName, string architect, string builder, string criticRaw, CriticReport criticReport, string sandboxOutput, string finalOutput)
        {
            string safeName = string.IsNullOrWhiteSpace(baseName) ? "run" : MakeSafe(baseName);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string folder = Path.Combine(AppDataPaths.ChatHistory, "WorkplaceExports", $"{safeName}_{stamp}");
            Directory.CreateDirectory(folder);

            File.WriteAllText(Path.Combine(folder, "architect.txt"), architect ?? "");
            File.WriteAllText(Path.Combine(folder, "builder.txt"), builder ?? "");
            File.WriteAllText(Path.Combine(folder, "critic_raw.txt"), criticRaw ?? "");
            File.WriteAllText(Path.Combine(folder, "sandbox.txt"), sandboxOutput ?? "");
            File.WriteAllText(Path.Combine(folder, "final_output.txt"), finalOutput ?? "");
            File.WriteAllText(Path.Combine(folder, "critic_contract.json"), JsonSerializer.Serialize(criticReport ?? new CriticReport(), WriteOptions));

            return folder;
        }

        private static string MakeSafe(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }

            return name.Length > 40 ? name[..40] : name;
        }
    }
}
