using System.Text;
using Malx_AI;
using Xunit;

namespace Malx_AI.Tests;

public sealed class UpdateDownloadFileTests
{
    [Fact]
    public async Task WriteAsync_ClosesDestinationBeforeReturningForVerification()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"AxiomUpdateTest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "update.zip.partial");

        try
        {
            byte[] payload = Encoding.UTF8.GetBytes("verified update payload");
            await UpdateDownloadFile.WriteAsync(
                new MemoryStream(payload),
                path,
                maximumBytes: 1024,
                expectedBytes: payload.Length,
                progress: null,
                CancellationToken.None);

            using (var verificationStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Equal(payload.Length, verificationStream.Length);
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
