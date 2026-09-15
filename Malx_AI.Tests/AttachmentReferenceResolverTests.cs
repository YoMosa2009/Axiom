using System.Collections.Generic;
using System.Linq;
using Malx_AI;
using Xunit;

namespace Malx_AI.Tests
{
    public class AttachmentReferenceResolverTests
    {
        private static IReadOnlyList<AttachmentReferenceEntry> Index(
            params (string Name, bool IsImage, string KindLabel)[] attachments)
            => AttachmentReferenceResolver.BuildIndex(attachments);

        private static AttachmentReferenceEntry? Resolve(string message, IReadOnlyList<AttachmentReferenceEntry> index)
            => AttachmentReferenceResolver.Resolve(message, index).FirstOrDefault()?.Target;

        [Fact]
        public void BuildIndex_NumbersOverallAndWithinKind()
        {
            var index = Index(
                ("notes.pdf", false, "PDF document"),
                ("dog.png", true, "image"),
                ("cat.png", true, "image"));

            Assert.Equal(new[] { 1, 2, 3 }, index.Select(e => e.Number));
            Assert.Equal("file 1 of 1", index[0].PositionLabel);
            Assert.Equal("image 1 of 2", index[1].PositionLabel);
            Assert.Equal("image 2 of 2", index[2].PositionLabel);
        }

        [Theory]
        [InlineData("In the first attached image, what breed is the dog?", "a.png")]
        [InlineData("In the 3rd attached image, what is written?", "c.png")]
        [InlineData("compare the second picture with the third", "b.png")]
        [InlineData("look at image 2 please", "b.png")]
        [InlineData("what about the last image?", "c.png")]
        [InlineData("describe the most recent photo", "c.png")]
        public void Resolve_MapsOrdinalPhrasesToTheRightImage(string message, string expected)
        {
            var index = Index(
                ("a.png", true, "image"),
                ("b.png", true, "image"),
                ("c.png", true, "image"));

            Assert.Equal(expected, Resolve(message, index)?.Name);
        }

        [Fact]
        public void Resolve_CountsImagesSeparatelyFromOtherFiles()
        {
            var index = Index(
                ("report.pdf", false, "PDF document"),
                ("budget.xlsx", false, "spreadsheet"),
                ("chart.png", true, "image"),
                ("logo.png", true, "image"));

            // Second image is the fourth attachment overall.
            AttachmentReferenceEntry? image = Resolve("summarize the second attached image", index);
            Assert.Equal("logo.png", image?.Name);
            Assert.Equal(4, image?.Number);

            // Second document skips the images entirely.
            Assert.Equal("budget.xlsx", Resolve("open the 2nd document", index)?.Name);
        }

        [Fact]
        public void Resolve_GenericNounsCountAcrossEveryAttachment()
        {
            var index = Index(
                ("report.pdf", false, "PDF document"),
                ("chart.png", true, "image"));

            Assert.Equal("chart.png", Resolve("use the second attachment", index)?.Name);
            Assert.Equal("report.pdf", Resolve("check the first file", index)?.Name);
        }

        [Fact]
        public void Resolve_DocumentNounFallsBackToImagesWhenNoOtherFilesExist()
        {
            var index = Index(("scan.png", true, "image"));

            Assert.Equal("scan.png", Resolve("read the first document", index)?.Name);
        }

        [Fact]
        public void Resolve_OutOfRangeReferenceReportsTheShortfallInsteadOfGuessing()
        {
            var index = Index(
                ("a.png", true, "image"),
                ("b.png", true, "image"));

            AttachmentReferenceMatch match = AttachmentReferenceResolver.Resolve("what is in the 5th attached image?", index).Single();

            Assert.False(match.IsResolved);
            Assert.Contains("does not exist", match.Explanation);
            Assert.Contains("only 2 images are attached", match.Explanation);
        }

        [Fact]
        public void Resolve_ImagePhraseWithNoImagesAttachedIsReportedAsMissing()
        {
            var index = Index(("notes.pdf", false, "PDF document"));

            AttachmentReferenceMatch match = AttachmentReferenceResolver.Resolve("describe the first image", index).Single();

            Assert.False(match.IsResolved);
            Assert.Contains("no image is attached", match.Explanation);
        }

        [Fact]
        public void Resolve_IgnoresMessagesWithoutPositionalReferences()
        {
            var index = Index(("a.png", true, "image"));

            Assert.Empty(AttachmentReferenceResolver.Resolve("what does this show?", index));
            Assert.Empty(AttachmentReferenceResolver.Resolve("summarize the attached image", index));
        }

        [Fact]
        public void Resolve_ReturnsNothingWhenNoAttachmentsExist()
        {
            Assert.Empty(AttachmentReferenceResolver.Resolve("the first attached image", Index()));
            Assert.Empty(AttachmentReferenceResolver.Resolve(null, Index(("a.png", true, "image"))));
        }

        [Fact]
        public void Resolve_DeduplicatesRepeatedPhrases()
        {
            var index = Index(("a.png", true, "image"), ("b.png", true, "image"));

            IReadOnlyList<AttachmentReferenceMatch> matches =
                AttachmentReferenceResolver.Resolve("the first image and the first image again", index);

            Assert.Single(matches);
        }

        [Fact]
        public void BuildResolutionBlock_NamesTheResolvedFile()
        {
            var index = Index(
                ("notes.pdf", false, "PDF document"),
                ("dog.png", true, "image"),
                ("cat.png", true, "image"));

            string block = AttachmentReferenceResolver.BuildResolutionBlock("in the 2nd attached image, what is it?", index);

            Assert.Contains("[ATTACHMENT REFERENCE]", block);
            Assert.Contains("attachment 3: cat.png", block);
            Assert.Contains("image 2 of 2", block);
        }

        [Fact]
        public void BuildResolutionBlock_IsEmptyWithoutAPositionalPhrase()
        {
            var index = Index(("dog.png", true, "image"));

            Assert.Equal(string.Empty, AttachmentReferenceResolver.BuildResolutionBlock("what breed is this?", index));
        }

        [Fact]
        public void BuildManifest_ListsEveryAttachmentInAttachOrder()
        {
            var index = Index(
                ("notes.pdf", false, "PDF document"),
                ("dog.png", true, "image"));

            string manifest = AttachmentReferenceResolver.BuildManifest(index);

            Assert.Contains("1. notes.pdf", manifest);
            Assert.Contains("2. dog.png", manifest);
            Assert.Contains("image 1 of 1", manifest);
        }

        [Fact]
        public void BuildManifest_IsEmptyWithoutAttachments()
        {
            Assert.Equal(string.Empty, AttachmentReferenceResolver.BuildManifest(Index()));
        }

        [Fact]
        public void BuildVisionOrderNote_MapsSuppliedImagesToPositions()
        {
            string note = AttachmentReferenceResolver.BuildVisionOrderNote(["a.png", "b.png"]);

            Assert.Contains("image 1 = a.png", note);
            Assert.Contains("image 2 = b.png", note);
        }
    }
}
