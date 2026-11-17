using System;
using System.IO;
using Malx_AI.Agent;
using Xunit;

namespace Malx_AI.Tests
{
    public sealed class WorkplaceTaskTrackerFileTests
    {
        [Fact]
        public void ResolveTrackerPath_UsesProjectFolderWhenSpecifiedAndFallbackWhenNull()
        {
            string projDir = Path.Combine(Path.GetTempPath(), "AxiomTrackerProj_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(projDir);
            try
            {
                string scopedPath = WorkplaceTaskTrackerFileService.ResolveTrackerPath(projDir, 42);
                Assert.StartsWith(projDir, scopedPath, StringComparison.OrdinalIgnoreCase);
                Assert.EndsWith("AXIOM_TASK_TRACKER.md", scopedPath, StringComparison.OrdinalIgnoreCase);

                string fallbackPath = WorkplaceTaskTrackerFileService.ResolveTrackerPath(null, 42, fallbackRoot: projDir);
                Assert.Contains("workplace_42_tracker.md", fallbackPath, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                if (Directory.Exists(projDir))
                    Directory.Delete(projDir, recursive: true);
            }
        }

        [Fact]
        public void InitializeOrUpdateAndRead_PersistsProgressAndSurvivesCompactionReload()
        {
            string projDir = Path.Combine(Path.GetTempPath(), "AxiomTrackerRw_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(projDir);
            try
            {
                string trackerPath = WorkplaceTaskTrackerFileService.ResolveTrackerPath(projDir, 7);
                WorkplaceTaskTrackerFileService.InitializeOrUpdateTracker(
                    trackerPath,
                    sessionTitle: "Fix auth redirect",
                    userGoal: "Update callback URL handling in AuthService",
                    completedSteps: ["Inspected AuthService.cs", "Patched callback validation"],
                    touchedFiles: [Path.Combine(projDir, "AuthService.cs")],
                    backgroundTasks: [new AgentBackgroundTaskSnapshot("bg-1", "dotnet test", "dotnet test", projDir, DateTime.UtcNow.AddSeconds(-4), DateTime.UtcNow, 10, false, false, 0, "Passed")],
                    latestTurnSummary: "All unit tests pass after fix.");

                Assert.True(File.Exists(trackerPath));
                string reloaded = WorkplaceTaskTrackerFileService.ReadTrackerForCompactionContext(trackerPath);
                Assert.Contains("Fix auth redirect", reloaded);
                Assert.Contains("Update callback URL handling in AuthService", reloaded);
                Assert.Contains("AuthService.cs", reloaded);
                Assert.Contains("Patched callback validation", reloaded);
                Assert.Contains("All unit tests pass after fix.", reloaded);
            }
            finally
            {
                if (Directory.Exists(projDir))
                    Directory.Delete(projDir, recursive: true);
            }
        }
    }
}
