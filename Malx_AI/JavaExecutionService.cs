using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI
{
    internal static class JavaExecutionService
    {
        private const int MaximumCodeCharacters = 200_000;
        private const int MaximumOutputCharacters = 120_000;
        private static readonly Regex PublicClassRegex = new(
            @"\bpublic\s+(?:final\s+)?class\s+(?<name>[A-Za-z_$][\w$]*)",
            RegexOptions.Compiled);
        private static readonly Regex AnyClassRegex = new(
            @"\bclass\s+(?<name>[A-Za-z_$][\w$]*)",
            RegexOptions.Compiled);
        private static readonly Regex FencedJavaRegex = new(
            @"```(?:java)\s*\r?\n(?<code>[\s\S]*?)```",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static bool TryExtractExplicitCode(string text, out string code)
        {
            Match match = FencedJavaRegex.Match(text ?? string.Empty);
            code = match.Success ? match.Groups["code"].Value.Trim() : string.Empty;
            return code.Length > 0;
        }

        internal static async Task<string> ExecuteAsync(
            string code,
            CancellationToken token,
            TimeSpan? timeout = null)
        {
            string source = (code ?? string.Empty).Trim();
            if (source.Length == 0)
                return "Java tool received no source code.";
            if (source.Length > MaximumCodeCharacters)
                return $"Java source exceeds the {MaximumCodeCharacters:N0}-character safety limit.";
            if (Regex.IsMatch(source, @"(?m)^\s*package\s+"))
                return "Java packages are not supported by the temporary single-file runner. Use a package-free class with main(String[] args).";

            string? javac = FindExecutable("javac");
            string? java = FindExecutable("java");
            if (javac == null || java == null)
                return "Java execution is unavailable because a JDK (javac and java) was not found on PATH.";

            Match classMatch = PublicClassRegex.Match(source);
            if (!classMatch.Success)
                classMatch = AnyClassRegex.Match(source);
            string className = classMatch.Success ? classMatch.Groups["name"].Value : "Main";

            string workingDirectory = Path.Combine(
                Path.GetTempPath(),
                "Axiom",
                "JavaSandbox",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workingDirectory);
            string sourcePath = Path.Combine(workingDirectory, className + ".java");

            try
            {
                await File.WriteAllTextAsync(sourcePath, source, new UTF8Encoding(false), token);
                TimeSpan executionTimeout = timeout ?? TimeSpan.FromSeconds(15);

                ProcessResult compile = await RunProcessAsync(
                    javac,
                    [sourcePath],
                    workingDirectory,
                    executionTimeout,
                    token);
                if (compile.TimedOut)
                    return "Java compilation timed out.";
                if (compile.ExitCode != 0)
                    return "Java compilation failed:\n" + BoundOutput(compile.CombinedOutput);

                ProcessResult run = await RunProcessAsync(
                    java,
                    ["-cp", workingDirectory, className],
                    workingDirectory,
                    executionTimeout,
                    token);
                if (run.TimedOut)
                    return "Java execution timed out.";

                string output = BoundOutput(run.CombinedOutput);
                if (run.ExitCode != 0)
                    return $"Java exited with code {run.ExitCode}:\n{output}";
                return string.IsNullOrWhiteSpace(output)
                    ? "Java completed successfully with no output."
                    : output;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return "Java execution failed: " + ex.Message;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(workingDirectory))
                        Directory.Delete(workingDirectory, recursive: true);
                }
                catch
                {
                    // Best effort. A later maintenance pass can remove a locked temp folder.
                }
            }
        }

        private static string? FindExecutable(string name)
        {
            string extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                         .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(directory.Trim().Trim('"'), name + extension);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch
                {
                }
            }

            return null;
        }

        private static async Task<ProcessResult> RunProcessAsync(
            string executable,
            string[] arguments,
            string workingDirectory,
            TimeSpan timeout,
            CancellationToken token)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(token);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
                string output = await stdout;
                string error = await stderr;
                return new ProcessResult(process.ExitCode, false, Combine(output, error));
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new ProcessResult(-1, true, string.Empty);
            }
        }

        private static string Combine(string stdout, string stderr)
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(stdout))
                builder.AppendLine(stdout.TrimEnd());
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                if (builder.Length > 0)
                    builder.AppendLine();
                builder.AppendLine("stderr:");
                builder.Append(stderr.TrimEnd());
            }
            return builder.ToString().Trim();
        }

        private static string BoundOutput(string value)
        {
            string normalized = value ?? string.Empty;
            return normalized.Length <= MaximumOutputCharacters
                ? normalized
                : normalized[..MaximumOutputCharacters] + "\n[output truncated]";
        }

        private readonly record struct ProcessResult(int ExitCode, bool TimedOut, string CombinedOutput);
    }
}
