using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI.Agent
{
    /// <summary>Renders a page without creating a visible browser or touching the user's session.</summary>
    public static class AgentPageCapture
    {
        internal static Uri ResolveUrl(string value, AgentScope scope)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
                || !(uri.IsFile || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                throw new ArgumentException("Page screenshots require a file:///, http:// or https:// URL.");
            if (uri.IsFile)
            {
                if (uri.IsUnc || !scope.Allows(uri.LocalPath))
                    throw new ArgumentException("The page is outside the selected local project scope.");
                if (!File.Exists(uri.LocalPath))
                    throw new FileNotFoundException("The page file does not exist.", uri.LocalPath);
            }
            return uri;
        }

        public static async Task<AgentScreenshot> CaptureAsync(string url, AgentScope scope, CancellationToken token)
        {
            Uri uri = ResolveUrl(url, scope);
            try
            {
                return await CaptureOnceAsync(uri, token).ConfigureAwait(false);
            }
            catch (InvalidOperationException) when (!token.IsCancellationRequested && !uri.IsFile)
            {
                // A live run (2026-11-24) asked for its local server's page a few seconds after
                // starting the server, and the first render failed in under a second. The same
                // URL rendered fine later. One short retry covers a server that is still starting.
                await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                return await CaptureOnceAsync(uri, token).ConfigureAwait(false);
            }
        }

        private static async Task<AgentScreenshot> CaptureOnceAsync(Uri uri, CancellationToken token)
        {
            DetectedBrowserInfo? browser = BrowserControlService.GetInstalledBrowserCandidates()
                .FirstOrDefault(b => b.BrowserId is "edge" or "chrome" or "brave");
            if (browser == null)
                throw new InvalidOperationException("Background page capture requires an installed Edge, Chrome or Brave browser. No visible browser was opened.");

            string work = Path.Combine(Path.GetTempPath(), "AxiomPageCapture", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            string png = Path.Combine(work, "page.png");
            var start = new ProcessStartInfo(browser.ExecutablePath)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string arg in new[] { "--headless=new", "--no-first-run", "--no-default-browser-check",
                "--disable-extensions", "--disable-background-networking", "--hide-scrollbars",
                "--window-size=1280,900", "--virtual-time-budget=2000",
                "--user-data-dir=" + Path.Combine(work, "profile"), "--screenshot=" + png, uri.AbsoluteUri })
                start.ArgumentList.Add(arg);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(25));
            using var process = new Process { StartInfo = start };
            bool started = false;
            try
            {
                token.ThrowIfCancellationRequested();
                started = process.Start();
                if (!started) throw new InvalidOperationException("The background capture browser did not start.");
                Task<string> stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
                Task<string> stderr = process.StandardError.ReadToEndAsync(deadline.Token);
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                if (process.ExitCode != 0 || !File.Exists(png))
                    throw new InvalidOperationException(
                        $"The background browser could not render the page (exit code {process.ExitCode}). "
                        + (uri.IsFile ? "" : "If the page comes from a local server, check that the server is running and answers this URL. ")
                        + "No desktop windows were changed.");
                byte[] bytes = await File.ReadAllBytesAsync(png, deadline.Token).ConfigureAwait(false);
                if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                    throw new InvalidOperationException("The background browser did not return a PNG image.");
                int width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
                int height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
                return new AgentScreenshot(bytes, width, height,
                    $"background render of {uri.AbsoluteUri} (isolated browser; initial layout only, interactions/playback are not proved by this image)", []);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException("Background page capture exceeded 25 seconds and was stopped. No desktop windows were changed.");
            }
            finally
            {
                // Only this owned process tree/profile is affected. Never attach to or kill the
                // user's browser, which runs with a different user-data directory.
                if (started)
                {
                    try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(3000); } }
                    catch { }
                }
                try { Directory.Delete(work, recursive: true); } catch { }
            }
        }
    }
}
