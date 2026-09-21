using Malx_AI;
using Xunit;

namespace Malx_AI.Tests
{
    public class UpdateStorageSpaceTests
    {
        [Fact]
        public void RequiredSpaceCoversTheZipItsStagingAndABackup()
        {
            // A 400 MB package unpacks to roughly three times that, plus the replaced files are
            // backed up, so the allowance has to be several times the download.
            long required = UpdateStoragePaths.RequiredFreeBytes(400L * 1024 * 1024);

            Assert.True(required > 400L * 1024 * 1024 * 3,
                $"allowance {UpdateStoragePaths.FormatBytes(required)} is too small for a 400 MB package");
        }

        [Fact]
        public void RequiredSpaceIsSaneForAnUnknownPackageSize()
        {
            // GitHub does not always report a size; the allowance must not collapse to zero.
            Assert.True(UpdateStoragePaths.RequiredFreeBytes(0) > 0);
            Assert.True(UpdateStoragePaths.RequiredFreeBytes(-1) > 0);
        }

        [Fact]
        public void FreeSpaceIsReadableForTheCurrentDrive()
        {
            long? free = UpdateStoragePaths.TryGetFreeBytes(System.IO.Path.GetTempPath());
            Assert.NotNull(free);
            Assert.True(free!.Value > 0);
        }

        [Fact]
        public void AMissingDriveReportsUnknownRatherThanThrowing()
        {
            // The exact failure here: AXIOM_UPDATE_DIR pointed at a drive that was not usable.
            Assert.Null(UpdateStoragePaths.TryGetFreeBytes(@"Q:\nope\missing"));
        }

        [Theory]
        [InlineData(500L * 1024 * 1024, "500 MB")]
        [InlineData(2L * 1024 * 1024 * 1024, "2 GB")]
        [InlineData(1536L * 1024 * 1024, "1.5 GB")]
        public void SizesAreFormattedForAnErrorMessage(long bytes, string expected)
        {
            Assert.Equal(expected, UpdateStoragePaths.FormatBytes(bytes));
        }

        [Fact]
        public void AnUnsetVariableUsesTheAppDataProfile()
        {
            string resolved = UpdateStoragePaths.ResolveRoot(null, @"C:\fallback\Updates");
            Assert.Equal(@"C:\fallback\Updates", resolved);
        }

        [Fact]
        public void ARelativeConfiguredPathIsIgnored()
        {
            // A relative redirect would land somewhere unpredictable, so the default wins.
            Assert.Equal(@"C:\fallback\Updates", UpdateStoragePaths.ResolveRoot("some/relative/dir", @"C:\fallback\Updates"));
        }
    }
}
