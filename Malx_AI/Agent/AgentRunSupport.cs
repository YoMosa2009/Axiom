using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Malx_AI.Agent
{
    /// <summary>
    /// Messages the user sends while a run is working, delivered to the agent at its next step.
    /// </summary>
    /// <remarks>
    /// Correcting a running agent used to mean pressing Stop and sending again. On a Hybrid Local
    /// server that is the worst possible move: the server keeps generating the abandoned reply
    /// after the connection closes, so the new run sat behind it on "Starting..." for minutes.
    /// Like a terminal agent, the message now joins the conversation between steps instead.
    /// </remarks>
    public sealed class AgentUserMessageInbox
    {
        private readonly ConcurrentQueue<string> _pending = new();

        public void Post(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
                _pending.Enqueue(message.Trim());
        }

        public bool HasPending => !_pending.IsEmpty;

        /// <summary>Takes every waiting message, oldest first.</summary>
        public IReadOnlyList<string> TakeAll()
        {
            var messages = new List<string>();
            while (_pending.TryDequeue(out string? message))
                messages.Add(message);
            return messages;
        }

        /// <summary>How a user's mid-run message is put to the model.</summary>
        internal static string FormatForModel(IReadOnlyList<string> messages)
        {
            string quoted = messages.Count == 1
                ? $"\"{messages[0]}\""
                : string.Join("\n", messages.Select(message => $"- \"{message}\""));
            return "The user sent a new message while you were working:\n"
                + quoted + "\n"
                + "It takes priority over earlier instructions where they conflict. Adjust what you are doing to match it "
                + "(for example, move or redo work in the place they now ask for), then carry on until the whole task is done.";
        }
    }

    /// <summary>
    /// Keeps a copy of every file that existed before the agent changed it, once per task.
    /// </summary>
    /// <remarks>
    /// A "make me an animation on F:" run wrote its index.html, style.css and README.md over an
    /// unrelated project that happened to be on that drive. The prompt now says to build new
    /// work in a new folder, but a prompt is a request, not a guarantee; this makes an overwrite
    /// recoverable no matter what the model decides.
    /// </remarks>
    public sealed class AgentFileBackup
    {
        private const long MaxBackupBytes = 200L * 1024 * 1024;

        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<AgentBackedUpFile> _saved = new();
        private readonly object _gate = new();

        public AgentFileBackup(string root)
        {
            Root = root ?? throw new ArgumentNullException(nameof(root));
        }

        /// <summary>The folder this task's copies go into.</summary>
        public string Root { get; }

        public IReadOnlyList<AgentBackedUpFile> Saved
        {
            get
            {
                lock (_gate)
                    return _saved.ToList();
            }
        }

        /// <summary>
        /// Call before changing <paramref name="path"/>. The first change in a task decides: a file
        /// that already existed is copied, a file the agent is creating is not, and later changes
        /// to either are the agent's own work. Returns the copy's path when one was made.
        /// </summary>
        public string? BeforeChange(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            lock (_gate)
            {
                if (!_seen.Add(path))
                    return null;
            }

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxBackupBytes)
                    return null;

                string copy = Path.Combine(Root, RelativeLocationFor(path));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(path, copy, overwrite: true);
                lock (_gate)
                    _saved.Add(new AgentBackedUpFile(path, copy));
                return copy;
            }
            catch
            {
                // A copy that cannot be made must not block the change the user asked for.
                return null;
            }
        }

        /// <summary>F:\site\index.html becomes F\site\index.html under the backup root.</summary>
        internal static string RelativeLocationFor(string path)
        {
            string full = Path.GetFullPath(path);
            string withoutColon = full.Replace(":", string.Empty, StringComparison.Ordinal);
            return withoutColon.TrimStart('\\', '/');
        }

        /// <summary>Removes backup folders older than <paramref name="maxAge"/>; never throws.</summary>
        public static void PruneOld(string backupsFolder, TimeSpan maxAge)
        {
            try
            {
                if (!Directory.Exists(backupsFolder))
                    return;

                DateTime cutoff = DateTime.UtcNow - maxAge;
                foreach (string folder in Directory.EnumerateDirectories(backupsFolder))
                {
                    if (Directory.GetCreationTimeUtc(folder) < cutoff)
                    {
                        try { Directory.Delete(folder, recursive: true); }
                        catch { /* in use or locked: try again next time */ }
                    }
                }
            }
            catch
            {
                // Housekeeping only.
            }
        }
    }

    public sealed record AgentBackedUpFile(string OriginalPath, string CopyPath);
}
