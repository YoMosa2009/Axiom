using System;
using System.Linq;
using Malx_AI.Agent;
using Xunit;

namespace Malx_AI.Tests
{
    public class AgentEditMatcherTests
    {
        private static string Apply(string content, AgentEditMatcher.Result r) =>
            content[..r.Start] + r.Replacement + content[(r.Start + r.Length)..];

        [Fact]
        public void AnExactUniqueMatchIsUsedAsIs()
        {
            var r = AgentEditMatcher.Find("a\nb\nc\n", "b", "B");
            Assert.True(r.Found);
            Assert.Equal("exact", r.Tier);
            Assert.Equal("a\nB\nc\n", Apply("a\nb\nc\n", r));
        }

        [Fact]
        public void AnExactMatchThatAppearsTwiceFailsClosed()
        {
            var r = AgentEditMatcher.Find("x = 1;\nx = 1;\n", "x = 1;", "x = 2;");
            Assert.False(r.Found);
            Assert.Contains("2 times", r.Error);
        }

        [Fact]
        public void LfOldTextMatchesACrlfFileAndTheReplacementKeepsCrlf()
        {
            string file = "one\r\ntwo\r\nthree\r\n";
            var r = AgentEditMatcher.Find(file, "one\ntwo", "uno\ndos");
            Assert.True(r.Found);
            Assert.Equal("uno\r\ndos\r\nthree\r\n", Apply(file, r));
        }

        [Fact]
        public void TrailingSpacesInTheFileDoNotBlockTheMatch()
        {
            string file = "if (ok)   \n{\n    run();\n}\n";
            var r = AgentEditMatcher.Find(file, "if (ok)\n{\n    run();", "if (ready)\n{\n    run();");
            Assert.True(r.Found);
            Assert.Equal("ignoring trailing whitespace", r.Tier);
            Assert.Equal("if (ready)\n{\n    run();\n}\n", Apply(file, r));
        }

        [Fact]
        public void WrongIndentationMatchesAndTheNewTextIsShiftedToTheFilesIndentation()
        {
            string file = "class A\n{\n        void Run()\n        {\n            Go();\n        }\n}\n";
            var r = AgentEditMatcher.Find(file, "void Run()\n{\n    Go();\n}", "void Run()\n{\n    Stop();\n}");
            Assert.True(r.Found);
            Assert.Equal("ignoring indentation", r.Tier);
            Assert.Equal("class A\n{\n        void Run()\n        {\n            Stop();\n        }\n}\n", Apply(file, r));
        }

        [Fact]
        public void CurlyQuotesAndLongDashesFromTheModelStillMatchPlainText()
        {
            string file = "title = \"Axiom - fast\";\n";
            var r = AgentEditMatcher.Find(file, "title = “Axiom — fast”;", "title = \"Axiom\";");
            Assert.True(r.Found);
            Assert.Equal("title = \"Axiom\";\n", Apply(file, r));
        }

        [Fact]
        public void AFuzzyMatchInTwoPlacesFailsClosed()
        {
            string file = "  a();\n  b();\n\n    a();\n    b();\n";
            var r = AgentEditMatcher.Find(file, "a();\nb();", "c();");
            Assert.False(r.Found);
            Assert.Contains("2 places", r.Error);
        }

        [Fact]
        public void AMissShowsTheClosestLinesWithLineNumbers()
        {
            string file = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"line {i} value")) + "\n";
            var r = AgentEditMatcher.Find(file, "line 17 valu\nline 18 valeu", "x");
            Assert.False(r.Found);
            Assert.Contains("lines 17-18", r.Error);
            Assert.Contains("line 17 value", r.Error);
        }

        [Fact]
        public void HeadTailKeepsTheEndOfALongLog()
        {
            string log = string.Join("\n", Enumerable.Range(1, 5000).Select(i => $"compiling unit {i}")) + "\nerror CS1002: ; expected\nBuild FAILED.";
            string clipped = AgentOutputClip.HeadTail(log, 2000);
            Assert.True(clipped.Length <= 2000);
            Assert.StartsWith("compiling unit 1\n", clipped);
            Assert.EndsWith("Build FAILED.", clipped);
            Assert.Contains("error CS1002", clipped);
            Assert.Contains("omitted from the middle", clipped);
        }

        [Fact]
        public void HeadTailLeavesShortTextAlone()
        {
            Assert.Equal("ok", AgentOutputClip.HeadTail("ok", 100));
        }
    }
}
