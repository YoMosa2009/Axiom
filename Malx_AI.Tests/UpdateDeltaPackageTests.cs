using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using Xunit;

namespace Malx_AI.Tests
{
    [Collection("ProcessLifecycleCollection")]
    public class UpdateDeltaPackageTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "AxiomDeltaTests", Guid.NewGuid().ToString("N"));

        public UpdateDeltaPackageTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
            GC.SuppressFinalize(this);
        }

        private string MakePayload(string name, Dictionary<string, string> files)
        {
            string folder = Path.Combine(_root, name, "Axiom");
            foreach ((string path, string content) in files)
            {
                string full = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }
            return folder;
        }

        private static Dictionary<string, string> Release(string version) => new()
        {
            ["Axiom Harness.exe"] = "exe " + version,
            ["Malx_AI.exe"] = "exe " + version,
            ["Malx_AI.dll"] = "app " + version,
            ["AXIOM_UPDATE_MANIFEST.txt"] = "manifest " + version,
            ["runtimes/win-x64/native/llama.dll"] = "native runtime (unchanged)",
            ["WebView2Loader.dll"] = "webview (unchanged)"
        };

        private static Dictionary<string, string> IndexFor(string folder) =>
            Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetRelativePath(folder, f).Replace('\\', '/'), UpdateDeltaPackage.HashFile);

        /// <summary>Writes a delta the way scripts/New-DeltaPackage.ps1 does, in C#.</summary>
        private string MakeDelta(string basePayload, string newPayload, Version baseVersion, Version target, Action<string>? tamper = null)
        {
            string work = Path.Combine(_root, "delta-src-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            Dictionary<string, string> index = IndexFor(newPayload);
            foreach ((string relative, string hash) in index)
            {
                string old = Path.Combine(basePayload, relative);
                if (File.Exists(old) && UpdateDeltaPackage.HashFile(old) == hash)
                    continue;
                string destination = Path.Combine(work, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(newPayload, relative), destination);
            }
            File.WriteAllText(Path.Combine(work, UpdateDeltaPackage.IndexFileName), UpdateDeltaPackage.WriteIndex(baseVersion, target, index));
            tamper?.Invoke(work);
            return work;
        }

        [Fact]
        public void TheNewPackageIsRebuiltFromTheChangedFilesAndTheInstalledCopy()
        {
            string installed = MakePayload("installed", Release("1.14.0"));
            string next = MakePayload("next", Release("1.15.0"));
            string delta = MakeDelta(installed, next, new Version(1, 14, 0), new Version(1, 15, 0));

            Assert.Equal(4, Directory.GetFiles(delta, "*", SearchOption.AllDirectories).Length - 1);

            string payload = Path.Combine(_root, "payload");
            UpdateDeltaPackage.BuildPayload(delta, installed, payload, new Version(1, 14, 0), new Version(1, 15, 0), CancellationToken.None);

            Assert.Equal(IndexFor(next), IndexFor(payload));
        }

        [Fact]
        public void AnEditedInstalledFileFailsClosedSoTheFullPackageIsUsed()
        {
            string installed = MakePayload("installed", Release("1.14.0"));
            string next = MakePayload("next", Release("1.15.0"));
            string delta = MakeDelta(installed, next, new Version(1, 14, 0), new Version(1, 15, 0));
            File.WriteAllText(Path.Combine(installed, "WebView2Loader.dll"), "something else");

            var error = Assert.Throws<InvalidDataException>(() => UpdateDeltaPackage.BuildPayload(
                delta, installed, Path.Combine(_root, "payload"), new Version(1, 14, 0), new Version(1, 15, 0), CancellationToken.None));
            Assert.Contains("full update", error.Message);
        }

        [Fact]
        public void ATamperedOrExtraDeltaFileIsRejected()
        {
            string installed = MakePayload("installed", Release("1.14.0"));
            string next = MakePayload("next", Release("1.15.0"));
            string tampered = MakeDelta(installed, next, new Version(1, 14, 0), new Version(1, 15, 0),
                work => File.WriteAllText(Path.Combine(work, "Malx_AI.dll"), "evil"));
            Assert.Throws<InvalidDataException>(() => UpdateDeltaPackage.BuildPayload(
                tampered, installed, Path.Combine(_root, "p1"), new Version(1, 14, 0), new Version(1, 15, 0), CancellationToken.None));

            string extra = MakeDelta(installed, next, new Version(1, 14, 0), new Version(1, 15, 0),
                work => File.WriteAllText(Path.Combine(work, "unlisted.dll"), "x"));
            Assert.Throws<InvalidDataException>(() => UpdateDeltaPackage.BuildPayload(
                extra, installed, Path.Combine(_root, "p2"), new Version(1, 14, 0), new Version(1, 15, 0), CancellationToken.None));
        }

        [Fact]
        public void ADeltaForAnotherBaseVersionIsRejected()
        {
            string installed = MakePayload("installed", Release("1.14.0"));
            string next = MakePayload("next", Release("1.15.0"));
            string delta = MakeDelta(installed, next, new Version(1, 13, 0), new Version(1, 15, 0));
            Assert.Throws<InvalidDataException>(() => UpdateDeltaPackage.BuildPayload(
                delta, installed, Path.Combine(_root, "payload"), new Version(1, 14, 0), new Version(1, 15, 0), CancellationToken.None));
        }

        [Fact]
        public void TheReleaseParserFindsDeltasOnlyForTheLatestVersionAndWithADigest()
        {
            string json = """
            {"tag_name":"v1.15.0","assets":[
              {"name":"Axiom-v1.15.0-win-x64-clean.zip","browser_download_url":"https://github.com/x/full.zip","size":381784959,"digest":"sha256:AA"},
              {"name":"Axiom-v1.15.0-delta-from-v1.14.0.axdelta","browser_download_url":"https://github.com/x/d.axdelta","size":3000000,"digest":"sha256:BB"},
              {"name":"Axiom-v1.15.0-delta-from-v1.13.0.axdelta","browser_download_url":"https://github.com/x/d2.axdelta","size":3000000},
              {"name":"Axiom-v1.12.0-delta-from-v1.11.0.axdelta","browser_download_url":"https://github.com/x/old.axdelta","size":1,"digest":"sha256:CC"}
            ]}
            """;
            UpdateCheckResult result = UpdateReleaseParser.Parse(json, new Version(1, 14, 0))!;

            Assert.Equal("Axiom-v1.15.0-win-x64-clean.zip", result.PackageFileName);
            UpdateDeltaAsset delta = Assert.Single(result.DeltaPackages);
            Assert.Equal(new Version(1, 14, 0, 0), delta.BaseVersion);
            Assert.Same(delta, UpdateDeltaPackage.FindFor(result));
            Assert.Null(UpdateDeltaPackage.FindFor(UpdateReleaseParser.Parse(json, new Version(1, 13, 0))!));
        }

        [Fact]
        public void TheReleaseScriptBuildsADeltaTheClientAccepts()
        {
            if (!OperatingSystem.IsWindows())
                return;
            string script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "scripts", "New-DeltaPackage.ps1"));
            if (!File.Exists(script))
                return;

            string installed = MakePayload("installed", Release("1.14.0"));
            string next = MakePayload("next", Release("1.15.0"));
            string output = Path.Combine(_root, "out");
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            // A test host started from pwsh (CI) passes PowerShell 7's module path on, and
            // Windows PowerShell then cannot load its own Microsoft.PowerShell.Utility cmdlets.
            start.Environment.Remove("PSModulePath");
            foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script,
                "-NewPayload", Path.GetDirectoryName(next)!, "-BasePayload", Path.GetDirectoryName(installed)!,
                "-BaseVersion", "1.14.0", "-TargetVersion", "1.15.0", "-OutputDirectory", output })
                start.ArgumentList.Add(arg);
            using (Process process = Process.Start(start)!)
            {
                string stderr = process.StandardError.ReadToEnd();
                process.StandardOutput.ReadToEnd();
                Assert.True(process.WaitForExit(120_000));
                Assert.True(process.ExitCode == 0, stderr);
            }

            string package = Path.Combine(output, "Axiom-v1.15.0-delta-from-v1.14.0.axdelta");
            Assert.True(File.Exists(package));
            Assert.True(UpdateDeltaPackage.TryParseAssetName(Path.GetFileName(package), out Version target, out Version baseVersion));
            string extracted = Path.Combine(_root, "extracted");
            ZipFile.ExtractToDirectory(package, extracted);
            string payload = Path.Combine(_root, "payload");
            UpdateDeltaPackage.BuildPayload(extracted, installed, payload, baseVersion, target, CancellationToken.None);
            Assert.Equal(IndexFor(next), IndexFor(payload));
        }
    }
}
