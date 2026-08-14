using Malx_AI;
using System.Diagnostics;
using Xunit;

namespace Malx_AI.Tests;

public sealed class UpdateProcessGuardTests
{
    [Fact]
    public void MatchesExpectedProcess_AcceptsSamePathIgnoringCase()
    {
        long startTicks = DateTime.UtcNow.Ticks;

        bool matches = UpdateProcessGuard.MatchesExpectedProcess(
            @"C:\Apps\Axiom\Malx_AI.exe",
            @"c:\apps\axiom\MALX_AI.EXE",
            startTicks,
            startTicks);

        Assert.True(matches);
    }

    [Fact]
    public void MatchesExpectedProcess_RejectsDifferentExecutable()
    {
        bool matches = UpdateProcessGuard.MatchesExpectedProcess(
            @"C:\Apps\Axiom\Malx_AI.exe",
            @"C:\Windows\System32\notepad.exe",
            null,
            DateTime.UtcNow.Ticks);

        Assert.False(matches);
    }

    [Fact]
    public void MatchesExpectedProcess_RejectsReusedPidStartTime()
    {
        long expectedTicks = DateTime.UtcNow.Ticks;

        bool matches = UpdateProcessGuard.MatchesExpectedProcess(
            @"C:\Apps\Axiom\Malx_AI.exe",
            @"C:\Apps\Axiom\Malx_AI.exe",
            expectedTicks,
            expectedTicks + TimeSpan.FromMinutes(1).Ticks);

        Assert.False(matches);
    }

    [Fact]
    public void WaitForExitOrTerminate_StopsVerifiedStalledProcess()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "-NoProfile", "-Command", "Start-Sleep -Seconds 30" }
        })!;

        try
        {
            Thread.Sleep(250);
            Assert.False(process.HasExited);
            string executable = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            long startTicks = process.StartTime.ToUniversalTime().Ticks;

            UpdateProcessGuard.WaitForExitOrTerminate(
                process,
                executable,
                startTicks,
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromSeconds(5));

            Assert.True(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }
}
