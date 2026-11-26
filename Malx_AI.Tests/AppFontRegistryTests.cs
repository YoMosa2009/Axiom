using System;
using System.IO;
using Xunit;

namespace Malx_AI.Tests
{
    public class AppFontRegistryTests
    {
        [Fact]
        public void AFontRegisteredFromAnotherDriveIsFoundByItsFullPath()
        {
            string folder = Path.Combine(Path.GetTempPath(), "AxiomFontReg", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string font = Path.Combine(folder, "AnthropicSans-Text-Regular-Static.otf");
                File.WriteAllBytes(font, [0]);

                Assert.Equal(folder, AppFontPolicy.FolderFromRegisteredFontPaths(
                    ["segoeui.ttf", Path.Combine(folder, "missing.otf"), font]));
                Assert.Null(AppFontPolicy.FolderFromRegisteredFontPaths(["AnthropicSans-Text-Regular-Static.otf"]));
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
