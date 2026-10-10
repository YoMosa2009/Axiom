using System;
using System.IO;
using System.Linq;
using Malx_AI.Agent;
using Xunit;

namespace Malx_AI.Tests
{
    public sealed class AgentRunJournalTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "axiom-journal-tests-" + Guid.NewGuid().ToString("N"));

        public AgentRunJournalTests()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { }
        }

        private string Work(string relative) => Path.Combine(_root, "work", relative);

        private AgentRunJournal NewJournal() => new(Path.Combine(_root, "journal"));

        private static void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        [Fact]
        public void UndoRestoresAFileTheRunChanged()
        {
            var journal = NewJournal();
            string path = Work("a.txt");
            Write(path, "before");
            journal.BeforeChange(path);
            Write(path, "after");
            journal.Seal();
            var result = journal.Undo();
            Assert.Equal("before", File.ReadAllText(path));
            Assert.Single(result.Restored);
            Assert.Empty(result.Removed);
            Assert.Empty(result.Skipped);
        }

        [Fact]
        public void UndoRemovesAFileTheRunCreated()
        {
            var journal = NewJournal();
            string path = Work("new.txt");
            journal.BeforeChange(path);
            Write(path, "made by the run");
            journal.Seal();
            var result = journal.Undo();
            Assert.False(File.Exists(path));
            Assert.Single(result.Removed);
            Assert.Single(Directory.GetFiles(result.UndoneFolder, "new.txt", SearchOption.AllDirectories));
        }

        [Fact]
        public void UndoKeepsTheRunsVersionInTheUndoneFolder()
        {
            var journal = NewJournal();
            string path = Work("b.txt");
            Write(path, "before");
            journal.BeforeChange(path);
            Write(path, "run version");
            journal.Seal();
            var result = journal.Undo();
            var undoneFile = Assert.Single(Directory.GetFiles(result.UndoneFolder, "b.txt", SearchOption.AllDirectories));
            Assert.Equal("run version", File.ReadAllText(undoneFile));
        }

        [Fact]
        public void AFileEditedAfterTheRunIsLeftAlone()
        {
            var journal = NewJournal();
            string path = Work("c.txt");
            Write(path, "before");
            journal.BeforeChange(path);
            Write(path, "run version");
            journal.Seal();
            File.WriteAllText(path, "user edited this later, longer text");
            var result = journal.Undo();
            Assert.Equal("user edited this later, longer text", File.ReadAllText(path));
            Assert.Single(result.Skipped);
            Assert.Empty(result.Restored);
        }

        [Fact]
        public void OnlyTheFirstChangeInARunCounts()
        {
            var journal = NewJournal();
            string path = Work("d.txt");
            Write(path, "original");
            journal.BeforeChange(path);
            Write(path, "first edit");
            journal.BeforeChange(path);
            Write(path, "second edit");
            journal.Seal();
            var result = journal.Undo();
            Assert.Equal("original", File.ReadAllText(path));
            Assert.Single(journal.ChangedFiles);
        }

        [Fact]
        public void AFolderTheRunCreatedIsRemovedWhenEmpty()
        {
            Directory.CreateDirectory(Path.Combine(_root, "work"));
            var journal = NewJournal();
            string path = Work(Path.Combine("project", "src", "app.js"));
            journal.BeforeChange(path);
            Write(path, "code");
            journal.Seal();
            var result = journal.Undo();
            Assert.True(Directory.Exists(Path.Combine(_root, "work")));
            Assert.False(Directory.Exists(Work("project")));
        }

        [Fact]
        public void AFolderWithOtherFilesIsKept()
        {
            Directory.CreateDirectory(Path.Combine(_root, "work"));
            var journal = NewJournal();
            string path = Work(Path.Combine("site", "index.html"));
            journal.BeforeChange(path);
            Write(path, "page");
            journal.Seal();
            Write(Work(Path.Combine("site", "notes.txt")), "user file");
            var result = journal.Undo();
            Assert.True(Directory.Exists(Work("site")));
            Assert.True(File.Exists(Work(Path.Combine("site", "notes.txt"))));
            Assert.False(File.Exists(path));
        }

        [Fact]
        public void UndoRunsOnlyOnce()
        {
            var journal = NewJournal();
            string path = Work("e.txt");
            Write(path, "before");
            journal.BeforeChange(path);
            Write(path, "after");
            journal.Seal();
            var first = journal.Undo();
            Write(path, "changed again");
            var second = journal.Undo();
            Assert.True(journal.IsUndone);
            Assert.Single(first.Restored);
            Assert.Empty(second.Restored);
            Assert.Equal("changed again", File.ReadAllText(path));
        }

        [Fact]
        public void CreatedCountCountsNewFilesOnly()
        {
            var journal = NewJournal();
            Write(Work("old.txt"), "x");
            journal.BeforeChange(Work("old.txt"));
            journal.BeforeChange(Work("new1.txt"));
            journal.BeforeChange(Work("new2.txt"));
            Assert.Equal(2, journal.CreatedCount);
            Assert.Equal(3, journal.ChangedFiles.Count);
        }
    }
}
