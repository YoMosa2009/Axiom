using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Malx_AI.Agent;

namespace Malx_AI
{
    /// <summary>
    /// "Undo these changes" for the most recent Agent Access run: a bar above the composer that
    /// puts every file the run wrote back the way it was.
    /// </summary>
    /// <remarks>
    /// Only the latest run can be undone. An earlier run's files may since have been changed by a
    /// later one, so undoing it out of order could not be trusted to leave a sensible state.
    /// </remarks>
    public partial class WorkplaceView
    {
        private AgentRunJournal? _agentUndoJournal;

        /// <summary>A new undo record for the run about to start, attached to the task's backup.</summary>
        private AgentRunJournal BeginAgentRunJournal(AgentFileBackup fileBackup)
        {
            ClearAgentUndo();
            var journal = new AgentRunJournal(Path.Combine(fileBackup.Root, "runs", DateTime.Now.ToString("HHmmss_fff")));
            fileBackup.Journal = journal;
            return journal;
        }

        private static void EndAgentRunJournal(AgentFileBackup fileBackup, AgentRunJournal journal)
        {
            if (ReferenceEquals(fileBackup.Journal, journal))
                fileBackup.Journal = null;
            journal.Seal();
        }

        /// <summary>Shows the undo bar when the run changed or created files.</summary>
        private void OfferAgentUndo(AgentRunJournal journal)
        {
            IReadOnlyList<string> files = journal.ChangedFiles;
            if (files.Count == 0)
                return;

            int created = journal.CreatedCount;
            int changed = files.Count - created;
            var parts = new List<string>();
            if (changed > 0)
                parts.Add($"changed {changed} file{(changed == 1 ? string.Empty : "s")}");
            if (created > 0)
                parts.Add($"created {created} file{(created == 1 ? string.Empty : "s")}");

            var tip = new StringBuilder();
            tip.AppendLine("Puts these back the way they were before this run:");
            foreach (string file in files.Take(12))
                tip.AppendLine("• " + file);
            if (files.Count > 12)
                tip.AppendLine($"• and {files.Count - 12} more");
            tip.AppendLine();
            tip.Append("Files the run created are moved aside, not deleted. Changes made by commands it ran (installs, files a command deleted) can't be undone here.");

            Dispatcher.Invoke(() =>
            {
                _agentUndoJournal = journal;
                AgentUndoText.Text = "The agent " + string.Join(" and ", parts) + ".";
                AgentUndoText.ToolTip = tip.ToString();
                AgentUndoButton.ToolTip = tip.ToString();
                AgentUndoButton.IsEnabled = true;
                AgentUndoBar.Visibility = Visibility.Visible;
            });
        }

        private void ClearAgentUndo()
        {
            _agentUndoJournal = null;
            if (Dispatcher.CheckAccess())
                HideAgentUndoBar();
            else
                Dispatcher.Invoke(HideAgentUndoBar);
        }

        private void HideAgentUndoBar()
        {
            if (AgentUndoBar != null)
                AgentUndoBar.Visibility = Visibility.Collapsed;
        }

        private void AgentUndoDismiss_Click(object sender, RoutedEventArgs e) => ClearAgentUndo();

        private async void AgentUndo_Click(object sender, RoutedEventArgs e)
        {
            AgentRunJournal? journal = _agentUndoJournal;
            if (journal == null)
                return;

            if (_isProcessing)
            {
                AppendVisibleNotice("Wait for the current run to finish, then undo.");
                return;
            }

            AgentUndoButton.IsEnabled = false;
            AgentUndoResult result = await Task.Run(journal.Undo);
            ClearAgentUndo();

            string message = DescribeAgentUndo(result);
            AppendVisibleNotice(message);
            LogActivity($"Agent Access: undo restored {result.Restored.Count}, removed {result.Removed.Count}, skipped {result.Skipped.Count}.");

            // The conversation and a follow-up run must know the files went back, or the agent
            // would carry on as if its work were still there.
            _chatHistory.Add(("assistant", message));
            if (_activeAgentTaskState != null)
            {
                _activeAgentTaskState.LastStatusMessage +=
                    "\n\n(The user then undid this run: its file changes were reverted, so files it wrote are back to their earlier state or gone.)";
                foreach (string removed in result.Removed)
                    _activeAgentTaskState.TouchedFiles.Remove(removed);
            }

            RequestWorkspaceStateSave();
        }

        private static string DescribeAgentUndo(AgentUndoResult result)
        {
            var done = new List<string>();
            if (result.Restored.Count > 0)
                done.Add($"put back {result.Restored.Count} file{(result.Restored.Count == 1 ? string.Empty : "s")}");
            if (result.Removed.Count > 0)
                done.Add($"removed {result.Removed.Count} file{(result.Removed.Count == 1 ? string.Empty : "s")} it created");

            var text = new StringBuilder();
            text.Append(done.Count == 0
                ? "Nothing was undone."
                : "Undid the agent's last run: " + string.Join(" and ", done) + ".");

            if (result.Skipped.Count > 0)
            {
                text.Append("\n\nLeft as they are:");
                foreach (string skipped in result.Skipped.Take(8))
                    text.Append("\n- ").Append(skipped);
                if (result.Skipped.Count > 8)
                    text.Append($"\n- and {result.Skipped.Count - 8} more");
            }

            if (result.Restored.Count + result.Removed.Count > 0)
                text.Append($"\n\nThe versions the run wrote are kept in `{result.UndoneFolder}` if you want them back.");
            return text.ToString();
        }
    }
}
