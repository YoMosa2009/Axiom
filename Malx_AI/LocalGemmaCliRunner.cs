using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI
{
    internal static class LocalGemmaCliRunner
    {
        public static bool TryResolveExecutable(out string executablePath)
        {
            string? envPath = Environment.GetEnvironmentVariable("AXIOM_LLAMA_CLI_PATH");
            if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            {
                executablePath = envPath;
                return true;
            }

            string? pathHit = FindInPath("llama-cli") ?? FindInPath("llama-cli.exe");
            if (!string.IsNullOrWhiteSpace(pathHit))
            {
                executablePath = pathHit;
                return true;
            }

            executablePath = string.Empty;
            return false;
        }

        public static async Task<string> InferAsync(
            string modelPath,
            string prompt,
            int maxTokens,
            float temperature,
            float minP,
            IReadOnlyList<string> stopTokens,
            CancellationToken token)
        {
            if (!TryResolveExecutable(out string exe))
            {
                throw new InvalidOperationException("Local runner not found. Install llama.cpp llama-cli or set AXIOM_LLAMA_CLI_PATH.");
            }

            string tempPromptPath = Path.Combine(Path.GetTempPath(), $"axiom_gemma4_{Guid.NewGuid():N}.prompt.txt");
            await File.WriteAllTextAsync(tempPromptPath, prompt, Encoding.UTF8, token);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                psi.ArgumentList.Add("-m");
                psi.ArgumentList.Add(modelPath);
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add(tempPromptPath);
                psi.ArgumentList.Add("-n");
                psi.ArgumentList.Add(Math.Max(1, maxTokens).ToString());
                psi.ArgumentList.Add("--temp");
                psi.ArgumentList.Add(temperature.ToString(System.Globalization.CultureInfo.InvariantCulture));
                psi.ArgumentList.Add("--min-p");
                psi.ArgumentList.Add(minP.ToString(System.Globalization.CultureInfo.InvariantCulture));
                psi.ArgumentList.Add("--no-display-prompt");

                foreach (var stop in stopTokens.Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    psi.ArgumentList.Add("-r");
                    psi.ArgumentList.Add(stop);
                }

                using var process = new Process { StartInfo = psi };
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();

                process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using var reg = token.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                });

                await process.WaitForExitAsync(token);

                string output = stdout.ToString().Trim();
                if (process.ExitCode != 0)
                {
                    string err = stderr.ToString().Trim();
                    throw new InvalidOperationException($"llama-cli failed (exit {process.ExitCode}): {err}");
                }

                return output;
            }
            finally
            {
                try { File.Delete(tempPromptPath); } catch { }
            }
        }

        private static string? FindInPath(string executable)
        {
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;

                string full = Path.Combine(dir.Trim(), executable);
                if (File.Exists(full))
                    return full;
            }

            return null;
        }
    }
}
