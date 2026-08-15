using System.Diagnostics;
using Malx_AI;
using Xunit;

namespace Malx_AI.Tests;

public sealed class UpdateApplyServiceTests
{
    // FilesAreIdentical gates whether the updater skips replacing a file entirely. A false
    // positive here would silently ship a stale file next to an otherwise-updated app -- these
    // cases exist specifically to catch that before it reaches a real update.
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"AxiomUpdateApplyTest-{Guid.NewGuid():N}");

        public TempDir() => Directory.CreateDirectory(Path);

        public string WriteFile(string name, byte[] content)
        {
            string path = System.IO.Path.Combine(Path, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    [Fact]
    public void FilesAreIdentical_TrueForByteForByteEqualContent()
    {
        using var dir = new TempDir();
        byte[] content = "same content on both sides"u8.ToArray();
        string a = dir.WriteFile("a.dll", content);
        string b = dir.WriteFile("b.dll", content);

        Assert.True(UpdateApplyService.FilesAreIdentical(a, b));
    }

    [Fact]
    public void FilesAreIdentical_FalseForDifferentContentSameLength()
    {
        using var dir = new TempDir();
        string a = dir.WriteFile("a.dll", "AAAAAAAAAA"u8.ToArray());
        string b = dir.WriteFile("b.dll", "AAAAAAAAAB"u8.ToArray());

        Assert.False(UpdateApplyService.FilesAreIdentical(a, b));
    }

    [Fact]
    public void FilesAreIdentical_FalseForDifferentLength()
    {
        using var dir = new TempDir();
        string a = dir.WriteFile("a.dll", "short"u8.ToArray());
        string b = dir.WriteFile("b.dll", "a fair bit longer"u8.ToArray());

        Assert.False(UpdateApplyService.FilesAreIdentical(a, b));
    }

    [Fact]
    public void FilesAreIdentical_FalseWhenTargetMissing()
    {
        using var dir = new TempDir();
        string a = dir.WriteFile("a.dll", "content"u8.ToArray());
        string missing = System.IO.Path.Combine(dir.Path, "does-not-exist.dll");

        Assert.False(UpdateApplyService.FilesAreIdentical(a, missing));
    }

    [Theory]
    [InlineData((1 << 20) - 1)]  // just under one comparison buffer
    [InlineData(1 << 20)]        // exactly one comparison buffer
    [InlineData((1 << 20) + 1)]  // spills into a second buffer
    public void FilesAreIdentical_HandlesContentAcrossBufferBoundary(int length)
    {
        using var dir = new TempDir();
        var random = new Random(12345);
        byte[] content = new byte[length];
        random.NextBytes(content);

        string a = dir.WriteFile("a.bin", content);
        string b = dir.WriteFile("b.bin", (byte[])content.Clone());
        Assert.True(UpdateApplyService.FilesAreIdentical(a, b));

        // Flip the very last byte -- if chunked reads ever misaligned across the two streams,
        // a mismatch this close to the tail is exactly what that bug would miss.
        byte[] differsAtEnd = (byte[])content.Clone();
        differsAtEnd[^1] ^= 0xFF;
        string c = dir.WriteFile("c.bin", differsAtEnd);
        Assert.False(UpdateApplyService.FilesAreIdentical(a, c));
    }

    // TerminateOtherInstancesAtPath is the fix for a failed update's auto-restart silently
    // orphaning itself behind the single-instance dialog and then blocking every later update
    // attempt by holding the install directory's DLLs open forever. Every real failure this
    // session hit traced back to exactly that -- these cases confirm the path-matching that
    // decides what gets killed is neither too broad (kills the process we were told to leave
    // alone) nor too narrow (misses the process actually sitting at the installed path).
    private static Process StartSleepingPowerShell(TimeSpan duration)
    {
        string executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "-NoProfile", "-Command", $"Start-Sleep -Seconds {duration.TotalSeconds}" }
        })!;
        Thread.Sleep(250);
        return process;
    }

    [Fact]
    public void TerminateOtherInstancesAtPath_KillsMatchingProcessAtExpectedPath()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using Process process = StartSleepingPowerShell(TimeSpan.FromSeconds(30));
        try
        {
            Assert.False(process.HasExited);

            UpdateApplyService.TerminateOtherInstancesAtPath(
                process.MainModule!.FileName,
                excludeProcessId: -1,
                "powershell");

            process.WaitForExit(5000);
            Assert.True(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void TerminateOtherInstancesAtPath_LeavesExcludedProcessIdRunning()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using Process process = StartSleepingPowerShell(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(process.HasExited);

            UpdateApplyService.TerminateOtherInstancesAtPath(
                process.MainModule!.FileName,
                excludeProcessId: process.Id,
                "powershell");

            Assert.False(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void TerminateOtherInstancesAtPath_LeavesNonMatchingPathRunning()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using Process process = StartSleepingPowerShell(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(process.HasExited);

            UpdateApplyService.TerminateOtherInstancesAtPath(
                @"C:\some\other\install\dir\Malx_AI.exe",
                excludeProcessId: -1,
                "powershell");

            Assert.False(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }
}
