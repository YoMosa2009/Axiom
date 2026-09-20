using Malx_AI.Agent;
using Xunit;

namespace Malx_AI.Tests
{
    public class AgentToolCallParserTests
    {
        [Fact]
        public void ParsesAFencedJsonCall()
        {
            string reply = "I'll check the build.\n```json\n{\"tool\": \"run_command\", \"arguments\": {\"command\": \"dotnet build\"}}\n```";

            Assert.True(AgentToolCallParser.TryParse(reply, out AgentToolCall? call, out _));
            Assert.Equal(AgentToolNames.RunCommand, call!.Tool);
            Assert.Equal("dotnet build", call.Arg("command"));
        }

        [Fact]
        public void ParsesABareJsonObject()
        {
            Assert.True(AgentToolCallParser.TryParse(
                "{\"tool\":\"read_file\",\"arguments\":{\"path\":\"a.cs\"}}", out AgentToolCall? call, out _));
            Assert.Equal("a.cs", call!.Arg("path"));
        }

        [Fact]
        public void ParsesFlatArgumentsWithoutAnArgumentsObject()
        {
            Assert.True(AgentToolCallParser.TryParse(
                "{\"tool\":\"read_file\",\"path\":\"a.cs\"}", out AgentToolCall? call, out _));
            Assert.Equal("a.cs", call!.Arg("path"));
        }

        [Fact]
        public void ParsesTheFlatProtocolSmallModelsUse()
        {
            string reply = "TOOL run_command\ncommand: dir C:\\work\nEND";

            Assert.True(AgentToolCallParser.TryParse(reply, out AgentToolCall? call, out _));
            Assert.Equal(AgentToolNames.RunCommand, call!.Tool);
            Assert.Equal(@"dir C:\work", call.Arg("command"));
        }

        [Fact]
        public void ParsesTheFlatProtocolWithoutTheEndMarker()
        {
            // Small models drop the terminator constantly; the call is still unambiguous.
            Assert.True(AgentToolCallParser.TryParse("TOOL list_directory\npath: C:\\work", out AgentToolCall? call, out _));
            Assert.Equal(@"C:\work", call!.Arg("path"));
        }

        [Fact]
        public void ParsesAHeredocFileWrite()
        {
            string reply = "TOOL write_file\npath: a.txt\ncontent: <<<\nline one\nline two\n>>>\nEND";

            Assert.True(AgentToolCallParser.TryParse(reply, out AgentToolCall? call, out _));
            Assert.Equal("a.txt", call!.Arg("path"));
            Assert.Equal("line one\nline two", call.Arg("content").Replace("\r\n", "\n"));
        }

        [Fact]
        public void HeredocBodyIsNotScannedForArguments()
        {
            // A file whose content contains "path: something" must not overwrite the real path.
            string reply = "TOOL write_file\npath: real.txt\ncontent: <<<\npath: decoy.txt\n>>>\nEND";

            Assert.True(AgentToolCallParser.TryParse(reply, out AgentToolCall? call, out _));
            Assert.Equal("real.txt", call!.Arg("path"));
        }

        [Fact]
        public void PlainProseIsAFinalAnswerNotAToolCall()
        {
            Assert.False(AgentToolCallParser.TryParse(
                "The build succeeded and all 12 tests passed.", out AgentToolCall? call, out string? error));
            Assert.Null(call);
            Assert.Null(error);
        }

        [Fact]
        public void AnUnknownToolNameIsReportedBackToTheModel()
        {
            Assert.False(AgentToolCallParser.TryParse(
                "{\"tool\":\"delete_everything\",\"arguments\":{}}", out _, out string? error));
            Assert.NotNull(error);
            Assert.Contains("not an available tool", error!);
        }

        [Fact]
        public void MalformedJsonIsReportedRatherThanTreatedAsAnAnswer()
        {
            Assert.False(AgentToolCallParser.TryParse(
                "```json\n{\"tool\": \"run_command\", \"arguments\": {\"command\": }\n```", out _, out string? error));
            Assert.NotNull(error);
        }

        [Fact]
        public void FinishNeedsNoArguments()
        {
            Assert.True(AgentToolCallParser.TryParse("TOOL finish", out AgentToolCall? call, out _));
            Assert.Equal(AgentToolNames.Finish, call!.Tool);
        }

        [Theory]
        [InlineData(AgentToolNames.RunCommand, "command", "npm test", "Ran npm test")]
        [InlineData(AgentToolNames.ReadFile, "path", @"C:\repo\src\App.xaml", "Read App.xaml")]
        [InlineData(AgentToolNames.WriteFile, "path", "notes.md", "Wrote notes.md")]
        [InlineData(AgentToolNames.EditFile, "path", "Program.cs", "Edited Program.cs")]
        public void ShortDescriptionsReadAsAStatusLine(string tool, string key, string value, string expected)
        {
            var call = new AgentToolCall(tool, new System.Collections.Generic.Dictionary<string, string> { [key] = value });
            Assert.Equal(expected, call.DescribeShort());
        }

        [Fact]
        public void LongCommandsAreTrimmedForTheStatusLine()
        {
            var call = new AgentToolCall(
                AgentToolNames.RunCommand,
                new System.Collections.Generic.Dictionary<string, string>
                {
                    ["command"] = "dotnet test --configuration Release --logger trx --results-directory ./artifacts/testresults"
                });

            string label = call.DescribeShort();
            Assert.True(label.Length <= 45, $"status line was {label.Length} chars: {label}");
            Assert.EndsWith("\u2026", label);
        }
    }
}
