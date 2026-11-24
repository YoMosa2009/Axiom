using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Microsoft.Win32;

namespace Malx_AI.Agent
{
    public sealed record DetectedBrowserInfo(
        string BrowserId,
        string DisplayName,
        string ExecutablePath,
        string DefaultSearchEngineName,
        string SearchUrlTemplate);

    /// <summary>
    /// Automatically detects the user's installed browser and default search engine (strictly selecting
    /// only 1 browser when multiple are installed) and lets Workplace models directly open URLs,
    /// search, read pages, download assets, and run installers inside the Workplace agent loop.
    /// </summary>
    public sealed class BrowserControlService
    {
        private readonly Func<string, CancellationToken, Task<string>>? _httpGetOverride;
        private readonly Func<string, string, CancellationToken, Task<long>>? _downloadOverride;
        private static readonly HttpClient SharedClient = CreateClient();

        public BrowserControlService(
            Func<string, CancellationToken, Task<string>>? httpGetOverride = null,
            Func<string, string, CancellationToken, Task<long>>? downloadOverride = null)
        {
            _httpGetOverride = httpGetOverride;
            _downloadOverride = downloadOverride;
        }

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(25)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AxiomBrowserControl/1.0");
            return client;
        }

        public static DetectedBrowserInfo DetectSingleBrowser()
        {
            string? defaultProgId = TryReadDefaultHttpsProgId();
            var candidates = GetInstalledBrowserCandidates();

            if (!string.IsNullOrWhiteSpace(defaultProgId))
            {
                DetectedBrowserInfo? preferred = defaultProgId.ToLowerInvariant() switch
                {
                    string s when s.Contains("chrome", StringComparison.Ordinal) => candidates.FirstOrDefault(c => c.BrowserId == "chrome"),
                    string s when s.Contains("edge", StringComparison.Ordinal) => candidates.FirstOrDefault(c => c.BrowserId == "edge"),
                    string s when s.Contains("firefox", StringComparison.Ordinal) => candidates.FirstOrDefault(c => c.BrowserId == "firefox"),
                    string s when s.Contains("brave", StringComparison.Ordinal) => candidates.FirstOrDefault(c => c.BrowserId == "brave"),
                    string s when s.Contains("opera", StringComparison.Ordinal) => candidates.FirstOrDefault(c => c.BrowserId == "opera"),
                    _ => null
                };
                if (preferred != null)
                    return preferred;
            }

            if (candidates.Count > 0)
                return candidates[0];

            // Fallback when no desktop executable is located (e.g. headless/CI environment)
            return new DetectedBrowserInfo(
                "system",
                "System Default Browser",
                string.Empty,
                "DuckDuckGo",
                "https://html.duckduckgo.com/html/?q={query}");
        }

        internal static List<DetectedBrowserInfo> GetInstalledBrowserCandidates()
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            var specs = new (string Id, string Name, string SearchEngine, string SearchTemplate, string[] Paths)[]
            {
                ("edge", "Microsoft Edge", "Bing", "https://www.bing.com/search?q={query}",
                    [
                        Path.Combine(programFilesX86, @"Microsoft\Edge\Application\msedge.exe"),
                        Path.Combine(programFiles, @"Microsoft\Edge\Application\msedge.exe")
                    ]),
                ("chrome", "Google Chrome", "Google", "https://www.google.com/search?q={query}",
                    [
                        Path.Combine(programFiles, @"Google\Chrome\Application\chrome.exe"),
                        Path.Combine(programFilesX86, @"Google\Chrome\Application\chrome.exe"),
                        Path.Combine(localAppData, @"Google\Chrome\Application\chrome.exe")
                    ]),
                ("brave", "Brave", "Brave Search", "https://search.brave.com/search?q={query}",
                    [
                        Path.Combine(programFiles, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
                        Path.Combine(localAppData, @"BraveSoftware\Brave-Browser\Application\brave.exe")
                    ]),
                ("firefox", "Mozilla Firefox", "Google", "https://www.google.com/search?q={query}",
                    [
                        Path.Combine(programFiles, @"Mozilla Firefox\firefox.exe"),
                        Path.Combine(programFilesX86, @"Mozilla Firefox\firefox.exe")
                    ]),
                ("opera", "Opera", "Google", "https://www.google.com/search?q={query}",
                    [
                        Path.Combine(localAppData, @"Programs\Opera\opera.exe"),
                        Path.Combine(programFiles, @"Opera\opera.exe")
                    ])
            };

            var found = new List<DetectedBrowserInfo>();
            foreach (var spec in specs)
            {
                string? exe = spec.Paths.FirstOrDefault(File.Exists);
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    found.Add(new DetectedBrowserInfo(spec.Id, spec.Name, exe, spec.SearchEngine, spec.SearchTemplate));
                }
            }
            return found;
        }

        private static string? TryReadDefaultHttpsProgId()
        {
            if (!OperatingSystem.IsWindows())
                return null;

            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
                return key?.GetValue("ProgId") as string;
            }
            catch
            {
                return null;
            }
        }

        public async Task<AgentToolResult> ExecuteAsync(
            string action,
            string urlOrQuery,
            string? destinationPath,
            string workingDirectory,
            Func<string, bool>? isPathAllowed,
            CancellationToken token)
        {
            string normalizedAction = (action ?? string.Empty).Trim().ToLowerInvariant();
            DetectedBrowserInfo browser = DetectSingleBrowser();

            switch (normalizedAction)
            {
                case "detect":
                case "status":
                    return AgentToolResult.Ok(
                        $"Detected Browser: {browser.DisplayName}\n" +
                        $"Executable: {(string.IsNullOrWhiteSpace(browser.ExecutablePath) ? "(system shell association)" : browser.ExecutablePath)}\n" +
                        $"Search Engine: {browser.DefaultSearchEngineName}");

                case "open":
                case "navigate":
                    return OpenInBrowser(browser, urlOrQuery);

                case "search":
                    return await SearchAsync(browser, urlOrQuery, token).ConfigureAwait(false);

                case "read":
                case "read_page":
                case "fetch":
                    return await ReadPageAsync(urlOrQuery, token).ConfigureAwait(false);

                case "download":
                    return await DownloadFileAsync(urlOrQuery, destinationPath, workingDirectory, isPathAllowed, token).ConfigureAwait(false);

                case "install":
                    return await InstallPackageOrFileAsync(urlOrQuery, destinationPath, workingDirectory, isPathAllowed, token).ConfigureAwait(false);

                default:
                    return AgentToolResult.Fail(
                        $"Unknown browser_control action '{action}'. Use one of: detect, open, search, read_page, download, install.");
            }
        }

        public static AgentToolResult OpenInBrowser(DetectedBrowserInfo browser, string rawUrl)
        {
            string url = NormalizeHttpUrl(rawUrl);
            if (string.IsNullOrWhiteSpace(url))
                url = LocalPageUrl(rawUrl) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(url))
                return AgentToolResult.Fail("A valid http:// or https:// URL, or the path of a local .html/.htm/.svg/.pdf file, is required for browser_control action 'open'.");

            try
            {
                if (!string.IsNullOrWhiteSpace(browser.ExecutablePath) && File.Exists(browser.ExecutablePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = browser.ExecutablePath,
                        Arguments = $"\"{url}\"",
                        UseShellExecute = false
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }

                return AgentToolResult.Ok($"Opened {url} in {browser.DisplayName}.");
            }
            catch (Exception ex)
            {
                return AgentToolResult.Fail($"Failed to open {url} in {browser.DisplayName}: {ex.Message}");
            }
        }

        /// <summary>
        /// A file:// URL for a page the agent just built (index.html and similar), so it can be
        /// checked without starting a web server. A live run (2026-11-24) was refused here and
        /// spent four steps finding Python to serve one HTML file. Only page-like files that exist.
        /// </summary>
        internal static string? LocalPageUrl(string? raw)
        {
            string text = (raw ?? string.Empty).Trim().Trim('"');
            if (text.Length == 0)
                return null;
            string path;
            if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? fileUri) || !fileUri.IsFile)
                    return null;
                path = fileUri.LocalPath;
            }
            else if (Path.IsPathFullyQualified(text))
                path = text;
            else
                return null;

            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is not (".html" or ".htm" or ".svg" or ".pdf") || !File.Exists(path))
                return null;
            return new Uri(Path.GetFullPath(path)).AbsoluteUri;
        }

        public async Task<AgentToolResult> SearchAsync(DetectedBrowserInfo browser, string query, CancellationToken token)
        {
            string cleanQuery = (query ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(cleanQuery))
                return AgentToolResult.Fail("A search query is required for browser_control action 'search'.");

            string searchUrl = "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(cleanQuery);
            string html;
            try
            {
                html = await FetchStringAsync(searchUrl, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return AgentToolResult.Fail($"Browser search failed via {browser.DefaultSearchEngineName}: {ex.Message}");
            }

            List<(string Title, string Url, string Snippet)> results = ParseSearchResults(html);
            if (results.Count == 0)
                return AgentToolResult.Ok($"Search via {browser.DefaultSearchEngineName} for '{cleanQuery}' returned no parsed results.");

            var sb = new StringBuilder();
            sb.AppendLine($"Search results ({browser.DisplayName} / {browser.DefaultSearchEngineName}) for \"{cleanQuery}\":");
            int index = 1;
            foreach (var item in results.Take(8))
            {
                sb.AppendLine($"{index++}. {item.Title}");
                sb.AppendLine($"   URL: {item.Url}");
                if (!string.IsNullOrWhiteSpace(item.Snippet))
                    sb.AppendLine($"   {item.Snippet}");
            }
            return AgentToolResult.Ok(sb.ToString().TrimEnd());
        }

        public async Task<AgentToolResult> ReadPageAsync(string rawUrl, CancellationToken token)
        {
            string url = NormalizeHttpUrl(rawUrl);
            if (string.IsNullOrWhiteSpace(url))
                return AgentToolResult.Fail("A valid http:// or https:// URL is required for browser_control action 'read_page'.");

            string html;
            try
            {
                html = await FetchStringAsync(url, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return AgentToolResult.Fail($"Could not read page '{url}': {ex.Message}");
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            foreach (HtmlNode noise in doc.DocumentNode.SelectNodes("//script|//style|//noscript|//svg") ?? Enumerable.Empty<HtmlNode>())
                noise.Remove();

            string title = HtmlEntity.DeEntitize(doc.DocumentNode.SelectSingleNode("//title")?.InnerText ?? string.Empty).Trim();
            string rawText = HtmlEntity.DeEntitize(doc.DocumentNode.InnerText ?? string.Empty);
            string compactText = Regex.Replace(rawText, @"[ \t]+", " ");
            compactText = Regex.Replace(compactText, @"\r?\n\s*\r?\n+", "\n").Trim();
            if (compactText.Length > 7000)
                compactText = compactText[..7000] + "\n… [truncated]";

            var links = new List<string>();
            foreach (HtmlNode a in doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
            {
                string href = a.GetAttributeValue("href", string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(href) || href.StartsWith("#", StringComparison.Ordinal) || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                    continue;
                string label = Regex.Replace(HtmlEntity.DeEntitize(a.InnerText ?? string.Empty), @"\s+", " ").Trim();
                if (Uri.TryCreate(new Uri(url), href, out Uri? abs))
                    links.Add(string.IsNullOrWhiteSpace(label) ? abs.AbsoluteUri : $"{label} -> {abs.AbsoluteUri}");
                if (links.Count >= 20)
                    break;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Page: {url}");
            if (!string.IsNullOrWhiteSpace(title))
                sb.AppendLine($"Title: {title}");
            sb.AppendLine();
            sb.AppendLine(compactText);
            if (links.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Key Links:");
                foreach (string link in links)
                    sb.AppendLine("- " + link);
            }
            return AgentToolResult.Ok(sb.ToString().TrimEnd());
        }

        public async Task<AgentToolResult> DownloadFileAsync(
            string rawUrl,
            string? destinationPath,
            string workingDirectory,
            Func<string, bool>? isPathAllowed,
            CancellationToken token)
        {
            string url = NormalizeHttpUrl(rawUrl);
            if (string.IsNullOrWhiteSpace(url))
                return AgentToolResult.Fail("A valid http:// or https:// URL is required for browser_control action 'download'.");

            string targetPath = ResolveDownloadPath(url, destinationPath, workingDirectory);
            if (isPathAllowed != null && !isPathAllowed(targetPath))
                return AgentToolResult.Fail($"Download target '{targetPath}' is outside the selected project folder.");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                long bytes = _downloadOverride != null
                    ? await _downloadOverride(url, targetPath, token).ConfigureAwait(false)
                    : await DownloadToDiskAsync(url, targetPath, token).ConfigureAwait(false);

                return AgentToolResult.Ok($"Downloaded {bytes:N0} bytes from {url} to {targetPath}.");
            }
            catch (Exception ex)
            {
                return AgentToolResult.Fail($"Download failed for {url}: {ex.Message}");
            }
        }

        private async Task<AgentToolResult> InstallPackageOrFileAsync(
            string urlOrPackageId,
            string? destinationPath,
            string workingDirectory,
            Func<string, bool>? isPathAllowed,
            CancellationToken token)
        {
            string target = (urlOrPackageId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(target))
                return AgentToolResult.Fail("Specify a package ID (e.g. 'Python.Python.3.12') or installer URL for browser_control action 'install'.");

            // Case 1: Direct URL -> download into workspace/temp and launch installer silently if possible
            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                AgentToolResult dl = await DownloadFileAsync(target, destinationPath, workingDirectory, isPathAllowed, token).ConfigureAwait(false);
                if (!dl.Succeeded)
                    return dl;

                string filePath = ResolveDownloadPath(target, destinationPath, workingDirectory);
                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                if (ext == ".msi")
                {
                    return await RunInstallerProcessAsync("msiexec.exe", $"/i \"{filePath}\" /passive /norestart", workingDirectory, token).ConfigureAwait(false);
                }
                if (ext == ".exe")
                {
                    return await RunInstallerProcessAsync(filePath, "/S", workingDirectory, token).ConfigureAwait(false);
                }
                return AgentToolResult.Ok(dl.Output + "\nSaved installer/package to disk (manual execution or archive extraction may be needed for " + ext + ").");
            }

            // Case 2: Package ID via winget
            if (!Regex.IsMatch(target, @"^[A-Za-z0-9._-]+$"))
                return AgentToolResult.Fail($"Invalid package identifier '{target}'. Supply an exact winget package ID or an https:// installer URL.");

            return await RunInstallerProcessAsync(
                "winget.exe",
                $"install --id \"{target}\" -e --accept-source-agreements --accept-package-agreements",
                workingDirectory,
                token).ConfigureAwait(false);
        }

        private static async Task<AgentToolResult> RunInstallerProcessAsync(string fileName, string arguments, string workingDirectory, CancellationToken token)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(startInfo);
                if (proc == null)
                    return AgentToolResult.Fail($"Could not start installer '{fileName}'.");

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));
                string stdout = await proc.StandardOutput.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);
                string stderr = await proc.StandardError.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

                string combined = (stdout + "\n" + stderr).Trim();
                if (combined.Length > 4000)
                    combined = combined[..4000] + "\n…";

                return proc.ExitCode == 0
                    ? AgentToolResult.Ok($"Install succeeded (exit code 0):\n{combined}")
                    : AgentToolResult.Fail($"Installer exited with code {proc.ExitCode}:\n{combined}");
            }
            catch (Exception ex)
            {
                return AgentToolResult.Fail($"Installer failed: {ex.Message}");
            }
        }

        private async Task<string> FetchStringAsync(string url, CancellationToken token)
        {
            if (_httpGetOverride != null)
                return await _httpGetOverride(url, token).ConfigureAwait(false);
            return await SharedClient.GetStringAsync(url, token).ConfigureAwait(false);
        }

        private static async Task<long> DownloadToDiskAsync(string url, string targetPath, CancellationToken token)
        {
            using HttpResponseMessage response = await SharedClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, token).ConfigureAwait(false);
            return output.Length;
        }

        internal static List<(string Title, string Url, string Snippet)> ParseSearchResults(string html)
        {
            var list = new List<(string, string, string)>();
            if (string.IsNullOrWhiteSpace(html))
                return list;

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // DuckDuckGo HTML structure + generic fallback
            HtmlNodeCollection? nodes = doc.DocumentNode.SelectNodes("//div[contains(@class,'result')]");
            if (nodes != null)
            {
                foreach (HtmlNode node in nodes)
                {
                    HtmlNode? link = node.SelectSingleNode(".//a[contains(@class,'result__a')]")
                        ?? node.SelectSingleNode(".//a[@href]");
                    if (link == null)
                        continue;

                    string title = HtmlEntity.DeEntitize(link.InnerText ?? string.Empty).Trim();
                    string href = HtmlEntity.DeEntitize(link.GetAttributeValue("href", string.Empty)).Trim();
                    if (href.Contains("uddg=", StringComparison.OrdinalIgnoreCase))
                    {
                        Match m = Regex.Match(href, @"uddg=([^&]+)");
                        if (m.Success)
                            href = Uri.UnescapeDataString(m.Groups[1].Value);
                    }

                    HtmlNode? snippetNode = node.SelectSingleNode(".//*[contains(@class,'result__snippet')]");
                    string snippet = HtmlEntity.DeEntitize(snippetNode?.InnerText ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(title) && href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        list.Add((title, href, snippet));
                }
            }

            return list;
        }

        internal static string NormalizeHttpUrl(string? raw)
        {
            string value = (raw ?? string.Empty).Trim();
            if (value.Length == 0)
                return string.Empty;
            if (!value.Contains("://", StringComparison.Ordinal) && value.Contains('.', StringComparison.Ordinal) && !value.Any(char.IsWhiteSpace))
                value = "https://" + value;
            return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? uri.AbsoluteUri
                : string.Empty;
        }

        private static string ResolveDownloadPath(string url, string? destinationPath, string workingDirectory)
        {
            if (!string.IsNullOrWhiteSpace(destinationPath))
            {
                string trimmed = destinationPath.Trim().Trim('"');
                return Path.IsPathRooted(trimmed)
                    ? Path.GetFullPath(trimmed)
                    : Path.GetFullPath(Path.Combine(workingDirectory, trimmed));
            }

            string fileName = "downloaded_file";
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                string last = Path.GetFileName(uri.AbsolutePath);
                if (!string.IsNullOrWhiteSpace(last))
                    fileName = last;
            }
            return Path.GetFullPath(Path.Combine(workingDirectory, fileName));
        }
    }
}
