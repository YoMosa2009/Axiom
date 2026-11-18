using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Malx_AI.Tests
{
    public sealed class AtomicFileWriterTests : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("axiom-atomic-writer-").FullName;

        [Fact]
        public void WorkspaceWritePreservesAnExistingTemporarySibling()
        {
            string path = Path.Combine(_directory, "notes.txt");
            File.WriteAllText(path, "original");
            File.WriteAllText(path + ".tmp", "user's separate file");

            AtomicFileWriter.WriteAllText(path, "edited", keepBackup: false);

            Assert.Equal("edited", File.ReadAllText(path));
            Assert.Equal("user's separate file", File.ReadAllText(path + ".tmp"));
            Assert.Equal(2, Directory.GetFiles(_directory).Length);
        }

        [Fact]
        public async Task CancelledSavePreservesPrimaryAndBackup()
        {
            string path = Path.Combine(_directory, "chat.json");
            File.WriteAllText(path, "current");
            File.WriteAllText(path + ".bak", "previous");

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AtomicFileWriter.WriteAllTextAsync(path, "cancelled", new CancellationToken(true)));

            Assert.Equal("current", File.ReadAllText(path));
            Assert.Equal("previous", File.ReadAllText(path + ".bak"));
            Assert.Equal(2, Directory.GetFiles(_directory).Length);
        }

        [Fact]
        public async Task OverlappingSavesKeepCompletePrimaryAndBackup()
        {
            string path = Path.Combine(_directory, "chat.json");
            string first = new('a', 128 * 1024);
            string second = new('b', 128 * 1024);

            await Task.WhenAll(
                AtomicFileWriter.WriteAllTextAsync(path, first),
                AtomicFileWriter.WriteAllTextAsync(path, second));

            Assert.Equal(
                new[] { first, second },
                new[] { File.ReadAllText(path), File.ReadAllText(path + ".bak") }.Order(StringComparer.Ordinal));
            Assert.Equal(2, Directory.GetFiles(_directory).Length);
        }

        [Fact]
        public void FailedCommitRemovesItsStagedFile()
        {
            string path = Path.Combine(_directory, "chat.json");
            File.WriteAllText(path, "current");
            Directory.CreateDirectory(path + ".bak");

            Exception? failure = Record.Exception(() => AtomicFileWriter.WriteAllText(path, "next"));

            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.Equal("current", File.ReadAllText(path));
            Assert.True(Directory.Exists(path + ".bak"));
            Assert.Single(Directory.GetFiles(_directory));
        }

        [Fact]
        public void RecoveryConsumesTheLegacyTemporarySnapshot()
        {
            string path = Path.Combine(_directory, "chat.json");
            File.WriteAllText(path + ".tmp", "\"legacy snapshot\"");

            var result = JsonPersistenceRecovery.Load<string>(path);

            Assert.True(result.WasRecovered);
            Assert.Equal("legacy snapshot", result.Value);
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal("\"legacy snapshot\"", File.ReadAllText(path));
        }

        [Fact]
        public void RecoveryUsesThePreviousSaveWhenThePrimaryIsCorrupted()
        {
            string path = Path.Combine(_directory, "chat.json");
            AtomicFileWriter.WriteAllText(path, "\"initial\"");
            AtomicFileWriter.WriteAllText(path, "\"previous\"");
            AtomicFileWriter.WriteAllText(path, "\"current\"");
            File.WriteAllText(path, "corrupted");

            var result = JsonPersistenceRecovery.Load<string>(path);

            Assert.True(result.WasRecovered);
            Assert.Equal("previous", result.Value);
            Assert.Equal("\"previous\"", File.ReadAllText(path));
            Assert.Equal("\"previous\"", File.ReadAllText(path + ".bak"));
        }

        public void Dispose()
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
