using Malx_AI;
using Xunit;

namespace Malx_AI.Tests
{
    public class CanvasFileArtifactTests
    {
        [Fact]
        public void ReadsAFileNamedByAFileHeader()
        {
            string reply = "Here you go.\n\nFILE: requirements.txt\n```text\nflask==3.0.0\nrequests==2.32.0\n```";

            Assert.True(CanvasFileArtifact.TryParse(reply, null, out CanvasFile? file));
            Assert.Equal("requirements.txt", file!.FileName);
            Assert.Equal(".txt", file.Extension);
            Assert.Equal("flask==3.0.0\nrequests==2.32.0", file.Content.Replace("\r\n", "\n"));
        }

        [Theory]
        [InlineData("FILENAME: notes.md")]
        [InlineData("PATH: notes.md")]
        [InlineData("**FILE:** notes.md")]
        [InlineData("# FILE: notes.md")]
        public void AcceptsTheWaysModelsWriteTheHeader(string header)
        {
            Assert.True(CanvasFileArtifact.TryParse(header + "\n```md\n# Title\n```", null, out CanvasFile? file));
            Assert.Equal("notes.md", file!.FileName);
        }

        [Theory]
        [InlineData("```json name=config.json")]
        [InlineData("```json title=\"config.json\"")]
        [InlineData("```config.json")]
        [InlineData("```json config.json")]
        public void ReadsAFileNamedInTheFenceInfoString(string fence)
        {
            Assert.True(CanvasFileArtifact.TryParse(fence + "\n{\"a\":1}\n```", null, out CanvasFile? file));
            Assert.Equal("config.json", file!.FileName);
            Assert.Equal(".json", file.Extension);
        }

        [Fact]
        public void UsesTheRequestedExtensionWhenTheModelNamesNothing()
        {
            // "make me a requirements.txt" answered with a bare fence is still that file.
            Assert.True(CanvasFileArtifact.TryParse("```\nflask\n```", ".txt", out CanvasFile? file));
            Assert.Equal(".txt", file!.Extension);
            Assert.Equal("untitled.txt", file.FileName);
        }

        [Fact]
        public void FallsBackToTheFenceLanguageWhenThereIsNoHint()
        {
            Assert.True(CanvasFileArtifact.TryParse("```python\nprint(1)\n```", null, out CanvasFile? file));
            Assert.Equal(".py", file!.Extension);
        }

        [Fact]
        public void StripsDirectoriesFromTheName()
        {
            Assert.True(CanvasFileArtifact.TryParse("FILE: src/app/config.json\n```json\n{}\n```", null, out CanvasFile? file));
            Assert.Equal("config.json", file!.FileName);
        }

        [Fact]
        public void OnlyTheHeaderDirectlyAboveTheFenceCounts()
        {
            string reply = "FILE: first.txt\n```text\nfirst\n```\n\nFILE: second.md\n```md\n# second\n```";

            Assert.True(CanvasFileArtifact.TryParse(reply, null, out CanvasFile? file));
            Assert.Equal("first.txt", file!.FileName);
            Assert.Equal("first", file.Content.Trim());
        }

        [Theory]
        [InlineData("FILE: logo.png")]
        [InlineData("FILE: report.pdf")]
        [InlineData("FILE: archive.zip")]
        [InlineData("FILE: sheet.xlsx")]
        public void BinaryFormatsAreNotPresentedAsFiles(string header)
        {
            Assert.False(CanvasFileArtifact.TryParse(header + "\n```\ndata\n```", null, out _));
        }

        [Fact]
        public void ProseWithNoFenceIsNotAFile()
        {
            Assert.False(CanvasFileArtifact.TryParse("I can write that file for you, just say the word.", null, out _));
        }

        [Fact]
        public void ArtifactCarriesTheRealNameAndExtension()
        {
            CanvasFileArtifact.TryParse("FILE: data.csv\n```csv\na,b\n1,2\n```", null, out CanvasFile? file);
            ArtifactRenderInfo artifact = CanvasFileArtifact.ToArtifact(file!);

            Assert.Equal("data.csv", artifact.DisplayTitle);
            Assert.Equal(".csv", artifact.SuggestedFileExtension);
            Assert.True(artifact.SupportsPreview);
            Assert.Equal("a,b\n1,2", artifact.SaveContent.Replace("\r\n", "\n"));
        }

        [Fact]
        public void CsvRendersAsATable()
        {
            CanvasFileArtifact.TryParse("FILE: data.csv\n```csv\nname,qty\nbolt,4\n```", null, out CanvasFile? file);
            string html = CanvasFileArtifact.ToArtifact(file!).RenderSource;

            Assert.Contains("<table>", html);
            Assert.Contains("<th>name</th>", html);
            Assert.Contains("<td>bolt</td>", html);
        }

        [Fact]
        public void QuotedCsvValuesSurviveSplitting()
        {
            var values = CanvasFileArtifact.SplitDelimited("a,\"b,c\",d", ',');
            Assert.Equal(3, values.Count);
            Assert.Equal("b,c", values[1]);
        }

        [Fact]
        public void PlainTextIsEscapedNotInterpreted()
        {
            CanvasFileArtifact.TryParse("FILE: notes.txt\n```text\n<script>alert(1)</script>\n```", null, out CanvasFile? file);
            string html = CanvasFileArtifact.ToArtifact(file!).RenderSource;

            Assert.DoesNotContain("<script>alert(1)</script>", html);
            Assert.Contains("&lt;script&gt;", html);
        }

        [Fact]
        public void TheFileNameIsShownInThePreviewHeader()
        {
            CanvasFileArtifact.TryParse("FILE: setup.cfg\n```ini\n[x]\n```", null, out CanvasFile? file);
            Assert.Contains("setup.cfg", CanvasFileArtifact.ToArtifact(file!).RenderSource);
        }

        [Fact]
        public void HtmlFilesStillRenderVisually()
        {
            CanvasFileArtifact.TryParse("FILE: page.html\n```html\n<h1>Hi</h1>\n```", null, out CanvasFile? file);
            ArtifactRenderInfo artifact = CanvasFileArtifact.ToArtifact(file!);

            Assert.Equal("page.html", artifact.DisplayTitle);
            Assert.Contains("<h1>Hi</h1>", artifact.RenderSource);
        }
    }

    public class CanvasFileRequestTests
    {
        [Theory]
        [InlineData("@ProjectCanvas make me a requirements.txt", "requirements.txt", ".txt")]
        [InlineData("create docker-compose.yml for this stack", "docker-compose.yml", ".yml")]
        [InlineData("write App.config please", "App.config", ".config")]
        public void FindsAnExplicitlyNamedFile(string query, string name, string extension)
        {
            Assert.True(CanvasFileRequest.TryDetect(query, out CanvasFileRequestInfo? request, out _));
            Assert.Equal(name, request!.FileName);
            Assert.Equal(extension, request.Extension);
            Assert.True(request.HasExplicitName);
        }

        [Theory]
        [InlineData("give me a csv file of the results", ".csv")]
        [InlineData("write it as markdown", ".md")]
        [InlineData("produce the output in json format", ".json")]
        [InlineData("make a .yaml for the config", ".yaml")]
        public void FindsABareFormatRequest(string query, string extension)
        {
            Assert.True(CanvasFileRequest.TryDetect(query, out CanvasFileRequestInfo? request, out _));
            Assert.Equal(extension, request!.Extension);
            Assert.False(request.HasExplicitName);
        }

        [Theory]
        [InlineData("make me a logo.png")]
        [InlineData("export this as a pdf")]
        [InlineData("give me an xlsx")]
        public void ImageAndBinaryFormatsAreReportedNotSilentlyMissed(string query)
        {
            Assert.False(CanvasFileRequest.TryDetect(query, out _, out string? rejected));
            Assert.NotNull(rejected);
            Assert.Contains("binary", CanvasFileRequest.DescribeBinaryRefusal(rejected!));
        }

        [Theory]
        [InlineData("summarise the meeting notes")]
        [InlineData("what does this function do, e.g. the parser")]
        [InlineData("explain the architecture")]
        public void OrdinaryRequestsAskForNoFile(string query)
        {
            Assert.False(CanvasFileRequest.TryDetect(query, out CanvasFileRequestInfo? request, out _));
            Assert.Null(request);
        }

        [Fact]
        public void TheInstructionNamesTheFileAndItsFence()
        {
            var request = new CanvasFileRequestInfo("notes.md", ".md");
            string instruction = CanvasFileRequest.BuildInstruction(request, compactModel: false);

            Assert.Contains("FILE: notes.md", instruction);
            Assert.Contains("```md", instruction);
            Assert.Contains("No placeholders", instruction);
        }

        [Fact]
        public void TheCompactInstructionStaysShort()
        {
            var request = new CanvasFileRequestInfo("notes.md", ".md");
            string compact = CanvasFileRequest.BuildInstruction(request, compactModel: true);

            Assert.Contains("FILE: notes.md", compact);
            Assert.True(compact.Length < 420, $"compact instruction was {compact.Length} chars");
        }
    }
}
