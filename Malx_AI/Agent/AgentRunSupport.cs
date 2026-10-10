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

        /// <summary>The current run's undo record, when one is being kept.</summary>
        public AgentRunJournal? Journal { get; set; }

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

            // The run's undo record keeps its own "first change in this run" state, separate from
            // the task-wide safety copy below.
            Journal?.BeforeChange(path);

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

    /// <summary>
    /// What one agent run did to files, so the run can be undone: each file's content before the
    /// run first changed it, or the fact that the run created it.
    /// </summary>
    /// <remarks>
    /// Undo never destroys anything. A restored file's run-time version and a removed new file
    /// are both moved into <c>undone\</c> under <see cref="Root"/>, so undoing an undo is a copy
    /// away. A file that changed again after the run (the user edited it) is left alone and
    /// reported, rather than silently losing that edit. Changes made by shell commands are not
    /// recorded and cannot be undone here.
    /// </remarks>
    public sealed class AgentRunJournal
    {
        private const long MaxCopyBytes = 200L * 1024 * 1024;

        private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _order = new();
        private readonly List<string> _createdFolders = new();
        private readonly object _gate = new();

        private sealed class Entry
        {
            public required string Path { get; init; }
            public bool ExistedBefore { get; init; }
            public string? CopyPath { get; init; }
            public bool TooLargeToCopy { get; init; }
            public DateTime? SealedWriteTimeUtc { get; set; }
            public long? SealedLength { get; set; }
        }

        public AgentRunJournal(string root)
        {
            Root = root ?? throw new ArgumentNullException(nameof(root));
        }

        public string Root { get; }

        public bool IsUndone { get; private set; }

        /// <summary>Files this run changed or created, in the order it first touched them.</summary>
        public IReadOnlyList<string> ChangedFiles
        {
            get
            {
                lock (_gate)
                    return _order.ToList();
            }
        }

        public int CreatedCount
        {
            get
            {
                lock (_gate)
                    return _entries.Values.Count(e => !e.ExistedBefore);
            }
        }

        /// <summary>Call before the run changes <paramref name="path"/>; only the first call per file counts.</summary>
        public void BeforeChange(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            string full;
            try
            {
                full = System.IO.Path.GetFullPath(path);
            }
            catch
            {
                return;
            }

            lock (_gate)
            {
                if (_entries.ContainsKey(full))
                    return;
            }

            Entry entry;
            try
            {
                var info = new FileInfo(full);
                if (!info.Exists)
                {
                    entry = new Entry { Path = full, ExistedBefore = false };
                    RecordFoldersAboutToBeCreated(info.DirectoryName);
                }
                else if (info.Length > MaxCopyBytes)
                {
                    entry = new Entry { Path = full, ExistedBefore = true, TooLargeToCopy = true };
                }
                else
                {
                    string copy = System.IO.Path.Combine(Root, "before", AgentFileBackup.RelativeLocationFor(full));
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(copy)!);
                    File.Copy(full, copy, overwrite: true);
                    entry = new Entry { Path = full, ExistedBefore = true, CopyPath = copy };
                }
            }
            catch
            {
                // Without a copy the file can't be put back; record it so undo says so.
                entry = new Entry { Path = full, ExistedBefore = true, TooLargeToCopy = true };
            }

            lock (_gate)
            {
                if (_entries.TryAdd(full, entry))
                    _order.Add(full);
            }
        }

        /// <summary>
        /// Call when the run ends. Remembers each file's state at that moment, so a later undo can
        /// tell whether something else changed the file since.
        /// </summary>
        public void Seal()
        {
            lock (_gate)
            {
                foreach (Entry entry in _entries.Values)
                {
                    try
                    {
                        var info = new FileInfo(entry.Path);
                        entry.SealedWriteTimeUtc = info.Exists ? info.LastWriteTimeUtc : null;
                        entry.SealedLength = info.Exists ? info.Length : null;
                    }
                    catch
                    {
                        entry.SealedWriteTimeUtc = null;
                        entry.SealedLength = null;
                    }
                }
            }
        }

        /// <summary>Puts every file back the way it was before the run. Runs once.</summary>
        public AgentUndoResult Undo()
        {
            var restored = new List<string>();
            var removed = new List<string>();
            var skipped = new List<string>();

            List<Entry> entries;
            lock (_gate)
            {
                if (IsUndone)
                    return new AgentUndoResult(restored, removed, skipped, System.IO.Path.Combine(Root, "undone"));
                IsUndone = true;
                entries = _order.Select(path => _entries[path]).Reverse().ToList();
            }

            foreach (Entry entry in entries)
            {
                try
                {
                    if (ChangedSinceSeal(entry))
                    {
                        skipped.Add($"{entry.Path} (changed after the run, left as it is)");
                        continue;
                    }

                    if (entry.ExistedBefore)
                    {
                        if (entry.CopyPath == null || !File.Exists(entry.CopyPath))
                        {
                            skipped.Add($"{entry.Path} (no saved copy: the file was too large or couldn't be copied)");
                            continue;
                        }

                        MoveAside(entry.Path);
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(entry.Path)!);
                        File.Copy(entry.CopyPath, entry.Path, overwrite: true);
                        restored.Add(entry.Path);
                    }
                    else if (File.Exists(entry.Path))
                    {
                        MoveAside(entry.Path);
                        removed.Add(entry.Path);
                    }
                }
                catch (Exception ex)
                {
                    skipped.Add($"{entry.Path} ({ex.Message})");
                }
            }

            RemoveCreatedFoldersIfEmpty();
            return new AgentUndoResult(restored, removed, skipped, System.IO.Path.Combine(Root, "undone"));
        }

        /// <summary>
        /// Remembers the top-most missing folder above a file the run is creating, so undo can
        /// remove a project folder the run made, but only once nothing is left in it.
        /// </summary>
        private void RecordFoldersAboutToBeCreated(string? folder)
        {
            string? topMissing = null;
            for (string? current = folder; !string.IsNullOrEmpty(current) && !Directory.Exists(current);
                 current = System.IO.Path.GetDirectoryName(current))
            {
                topMissing = current;
            }

            if (topMissing == null)
                return;

            lock (_gate)
            {
                if (!_createdFolders.Contains(topMissing, StringComparer.OrdinalIgnoreCase))
                    _createdFolders.Add(topMissing);
            }
        }

        private void RemoveCreatedFoldersIfEmpty()
        {
            List<string> folders;
            lock (_gate)
                folders = _createdFolders.ToList();

            foreach (string folder in folders)
            {
                try
                {
                    if (Directory.Exists(folder)
                        && !Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any())
                    {
                        Directory.Delete(folder, recursive: true);
                    }
                }
                catch
                {
                    // An empty folder left behind is harmless.
                }
            }
        }

        private static bool ChangedSinceSeal(Entry entry)
        {
            var info = new FileInfo(entry.Path);
            if (entry.SealedWriteTimeUtc == null)
                return info.Exists;
            return !info.Exists
                || info.LastWriteTimeUtc != entry.SealedWriteTimeUtc
                || info.Length != entry.SealedLength;
        }

        /// <summary>Moves the run's version of a file into <c>undone\</c> instead of deleting it.</summary>
        private void MoveAside(string path)
        {
            if (!File.Exists(path))
                return;

            string target = System.IO.Path.Combine(Root, "undone", AgentFileBackup.RelativeLocationFor(path));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            if (File.Exists(target))
                File.Delete(target);
            File.Move(path, target);
        }
    }

    public sealed record AgentUndoResult(
        IReadOnlyList<string> Restored,
        IReadOnlyList<string> Removed,
        IReadOnlyList<string> Skipped,
        string UndoneFolder);
}
