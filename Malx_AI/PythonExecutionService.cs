using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Python.Included;
using Python.Runtime;

namespace Malx_AI
{
    public sealed class PythonExecutionService
    {
        private const int DefaultTimeoutMs = 10000;
        private const string SandboxPrelude = """
import math
import json
import re
import statistics
import decimal
import fractions
import datetime
import itertools
import collections
import pathlib
import csv
import random

def axiom_print_json(value):
    print(json.dumps(value, indent=2, ensure_ascii=False, default=str))

def axiom_preview_csv(path, limit=5):
    rows = []
    with open(path, newline='', encoding='utf-8') as f:
        reader = csv.reader(f)
        for idx, row in enumerate(reader):
            rows.append(row)
            if idx + 1 >= limit:
                break
    return rows

def axiom_read_text(path):
    return pathlib.Path(path).read_text(encoding='utf-8')

def axiom_write_text(path, content):
    pathlib.Path(path).write_text(str(content), encoding='utf-8')
    return path

def axiom_now_iso():
    return datetime.datetime.utcnow().isoformat() + 'Z'
""";
        private static readonly SemaphoreSlim PythonInitGate = new(1, 1);
        private static readonly SemaphoreSlim PythonExecGate = new(1, 1);
        private static readonly Regex PreambleAssignmentRegex = new(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=", RegexOptions.Compiled);
        private static readonly Regex InputAssignmentLineRegex = new("(?m)^(?<indent>\\s*)(?:(?<var>[A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*)?(?<expr>(?:(?<cast>int|float|str)\\s*\\(\\s*)?input\\s*\\([^\\r\\n]*?\\)\\s*\\)?)(?<comment>\\s*#.*)?(?<newline>\\r?\\n|$)", RegexOptions.Compiled);
        private static readonly Regex InputExpressionRegex = new("(?<expr>(?:(?<cast>int|float|str)\\s*\\(\\s*)?input\\s*\\([^\\r\\n]*?\\)\\s*\\)?)", RegexOptions.Compiled);
        private static bool _pythonReady;
        private static bool _pythonEngineInitialized;
        private static PythonSessionState? _persistentSession;
        private readonly List<string> _persistentStdoutHistory = new();
        private int _lastSandboxInputSubstitutionCount;
        private List<string> _lastSandboxInputSubstitutionDetails = new();

        private sealed class PythonSessionState : IDisposable
        {
            public required PyDict Globals { get; init; }
            public DateTime StartedAt { get; init; } = DateTime.UtcNow;

            public void Dispose()
            {
                try
                {
                    Globals.Dispose();
                }
                catch
                {
                }
            }
        }

        public async Task InitializeAsync(CancellationToken token = default)
        {
            if (_pythonReady)
                return;

            await PythonInitGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_pythonReady)
                    return;

                await Task.Run(() => Installer.SetupPython(), token).ConfigureAwait(false);
                if (!_pythonEngineInitialized)
                {
                    await Task.Run(() => PythonEngine.Initialize(), token).ConfigureAwait(false);
                    _pythonEngineInitialized = true;
                }

                _pythonReady = true;
                await BackendLogService.LogEventAsync("PythonExecutionService.Initialize", "Portable Python runtime initialized.").ConfigureAwait(false);
            }
            finally
            {
                PythonInitGate.Release();
            }
        }

        public async Task StartPersistentSessionAsync(CancellationToken token = default)
        {
            await InitializeAsync(token).ConfigureAwait(false);
            await PythonExecGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                _persistentStdoutHistory.Clear();
                if (_persistentSession != null)
                    return;

                using var gil = Py.GIL();
                var globals = new PyDict();
                using var builtins = Py.Import("builtins");
                globals.SetItem("__builtins__", builtins);
                using var mainName = new PyString("__main__");
                globals.SetItem("__name__", mainName);
                PythonEngine.Exec(SandboxPrelude, globals, globals);

                _persistentSession = new PythonSessionState
                {
                    Globals = globals,
                    StartedAt = DateTime.UtcNow
                };
            }
            finally
            {
                PythonExecGate.Release();
            }
        }

        public async Task EndPersistentSessionAsync(CancellationToken token = default)
        {
            await PythonInitGate.WaitAsync(token).ConfigureAwait(false);
            await PythonExecGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_persistentSession == null)
                    return;

                try
                {
                    using var gil = Py.GIL();
                    _persistentSession.Dispose();
                }
                catch
                {
                }
                finally
                {
                    _persistentSession = null;
                    _persistentStdoutHistory.Clear();
                }
            }
            finally
            {
                PythonExecGate.Release();
                PythonInitGate.Release();
            }
        }

        public async Task<PythonExecutionResult> ExecuteMathScriptAsync(string code, int timeoutMs = DefaultTimeoutMs, CancellationToken token = default, bool sanitizeInput = true)
        {
            return await ExecuteMathScriptAsync(code, string.Empty, timeoutMs, token, sanitizeInput).ConfigureAwait(false);
        }

        public async Task<PythonExecutionResult> ExecuteMathScriptAsync(string code, string preamble, int timeoutMs = DefaultTimeoutMs, CancellationToken token = default, bool sanitizeInput = true)
        {
            if (string.IsNullOrWhiteSpace(code))
                return PythonExecutionResult.Fail("Python code is empty.", TimeSpan.Zero);

            string instrumentedCode = ArtifactRenderService.AppendChartCaptureScriptIfNeeded(code);

            string fullCode = string.IsNullOrWhiteSpace(preamble)
                ? instrumentedCode
                : preamble.TrimEnd() + "\n\n" + instrumentedCode;
            _lastSandboxInputSubstitutionCount = 0;
            _lastSandboxInputSubstitutionDetails = new List<string>();
            string sanitizedCode = sanitizeInput ? SanitizeSandboxCode(fullCode) : fullCode;
            int substitutionCount = _lastSandboxInputSubstitutionCount;
            List<string> substitutionDetails = _lastSandboxInputSubstitutionDetails;

            if (substitutionCount > 0)
            {
                await BackendLogService.LogEventAsync("SandboxInputSubstitution",
                    $"Count:{substitutionCount}\n" + string.Join("\n", substitutionDetails)).ConfigureAwait(false);
            }

            await InitializeAsync(token).ConfigureAwait(false);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            linkedCts.CancelAfter(timeoutMs);
            var sw = Stopwatch.StartNew();
            ulong workerThreadId = 0;

            await PythonExecGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                PyDict? globals = _persistentSession?.Globals;
                var pythonTask = Task.Run(() => ExecuteInternal(sanitizedCode, linkedCts.Token, id => workerThreadId = id, globals), linkedCts.Token);
                Task completed = await Task.WhenAny(pythonTask, Task.Delay(timeoutMs, token)).ConfigureAwait(false);
                if (completed != pythonTask)
                {
                    if (workerThreadId != 0)
                    {
                        try { PythonEngine.Interrupt(workerThreadId); } catch { }
                    }

                    linkedCts.Cancel();
                    sw.Stop();
                    return PythonExecutionResult.Fail($"Python execution timed out after {timeoutMs}ms.", sw.Elapsed, sanitizedCode, substitutionCount, true, "Timeout");
                }

                string output = await pythonTask.ConfigureAwait(false);
                sw.Stop();
                if (_persistentSession != null && !string.IsNullOrWhiteSpace(output))
                    _persistentStdoutHistory.Add(output.Trim());
                return PythonExecutionResult.Ok(output, sw.Elapsed, sanitizedCode, substitutionCount);
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                return PythonExecutionResult.Fail($"Python execution timed out after {timeoutMs}ms.", sw.Elapsed, sanitizedCode, substitutionCount, true, "Timeout");
            }
            catch (PythonException pyEx)
            {
                sw.Stop();
                return PythonExecutionResult.Fail(pyEx.Message, sw.Elapsed, sanitizedCode, substitutionCount, false, pyEx.Type?.ToString() ?? nameof(PythonException));
            }
            catch (Exception ex)
            {
                sw.Stop();
                return PythonExecutionResult.Fail(ex.Message, sw.Elapsed, sanitizedCode, substitutionCount, false, ex.GetType().Name);
            }
            finally
            {
                PythonExecGate.Release();
            }
        }

        public string GetPersistentStdoutHistoryBlock()
        {
            if (_persistentStdoutHistory.Count == 0)
                return string.Empty;

            return "Prior computation results\n" + string.Join("\n\n", _persistentStdoutHistory);
        }

        private string SanitizeSandboxCode(string rawPythonCode)
        {
            _lastSandboxInputSubstitutionCount = 0;
            _lastSandboxInputSubstitutionDetails = new List<string>();

            if (string.IsNullOrWhiteSpace(rawPythonCode))
                return rawPythonCode;

            var declaredVariables = ExtractDeclaredVariablesFromPreamble(rawPythonCode);
            string sanitized = InputAssignmentLineRegex.Replace(rawPythonCode, match => ReplaceWholeInputLine(match, declaredVariables));
            sanitized = InputExpressionRegex.Replace(sanitized, match => ReplaceResidualInputExpression(match, sanitized, declaredVariables));

            if (_lastSandboxInputSubstitutionCount > 0)
            {
                sanitized = sanitized.TrimStart();
                sanitized = "# Values below were substituted automatically for sandbox execution.\n" + sanitized;
            }

            return sanitized;
        }

        private HashSet<string> ExtractDeclaredVariablesFromPreamble(string rawPythonCode)
        {
            var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in rawPythonCode.Replace("\r\n", "\n").Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line))
                    break;

                Match match = PreambleAssignmentRegex.Match(line.Trim());
                if (match.Success)
                    declared.Add(match.Groups["name"].Value);
            }

            return declared;
        }

        private string ReplaceWholeInputLine(Match match, HashSet<string> declaredVariables)
        {
            string variableName = match.Groups["var"].Value;
            string cast = match.Groups["cast"].Value;
            string newline = match.Groups["newline"].Value;
            string comment = match.Groups["comment"].Value;

            if (!string.IsNullOrWhiteSpace(variableName) && declaredVariables.Contains(variableName))
            {
                RecordSandboxInputSubstitution(variableName, "removed line because seeded variable already exists");
                return newline;
            }

            string defaultLiteral = GetInputDefaultLiteral(variableName);
            string replacement = ApplyPythonCast(defaultLiteral, cast);
            string detailVariable = string.IsNullOrWhiteSpace(variableName) ? "(unassigned)" : variableName;
            RecordSandboxInputSubstitution(detailVariable, $"replaced input() with {replacement}");

            string indent = match.Groups["indent"].Value;
            string assignmentPrefix = string.IsNullOrWhiteSpace(variableName) ? string.Empty : $"{variableName} = ";
            string commentSuffix = string.IsNullOrWhiteSpace(comment) ? string.Empty : comment.TrimEnd();
            return $"{indent}{assignmentPrefix}{replacement}{commentSuffix}{newline}";
        }

        private string ReplaceResidualInputExpression(Match match, string sanitizedCode, HashSet<string> declaredVariables)
        {
            string cast = match.Groups["cast"].Value;
            string variableName = TryExtractAssignedVariableName(sanitizedCode, match.Index);

            if (!string.IsNullOrWhiteSpace(variableName) && declaredVariables.Contains(variableName))
            {
                RecordSandboxInputSubstitution(variableName, "replaced residual input() with seeded variable reference");
                return variableName;
            }

            string defaultLiteral = GetInputDefaultLiteral(variableName);
            string replacement = ApplyPythonCast(defaultLiteral, cast);
            string detailVariable = string.IsNullOrWhiteSpace(variableName) ? "(unassigned)" : variableName;
            RecordSandboxInputSubstitution(detailVariable, $"replaced residual input() with {replacement}");
            return replacement;
        }

        private string TryExtractAssignedVariableName(string code, int matchIndex)
        {
            int lineStart = code.LastIndexOf('\n', Math.Max(0, matchIndex - 1));
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            int prefixLength = Math.Max(0, matchIndex - lineStart);
            string prefix = code.Substring(lineStart, prefixLength);
            Match assignmentMatch = Regex.Match(prefix, @"(?<var>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?:(?:int|float|str)\s*\(\s*)?$", RegexOptions.RightToLeft);
            return assignmentMatch.Success ? assignmentMatch.Groups["var"].Value : string.Empty;
        }

        private string GetInputDefaultLiteral(string variableName)
        {
            string lower = variableName?.ToLowerInvariant() ?? string.Empty;
            if (lower.Contains("name") || lower.Contains("user"))
                return "\"test_user\"";
            if (lower.Contains("email"))
                return "\"user@example.com\"";
            if (lower.Contains("path") || lower.Contains("file"))
                return "\"sample.txt\"";
            if (lower.Contains("hours") || lower.Contains("time") || lower.Contains("duration"))
                return "\"1\"";
            if (lower.Contains("amount") || lower.Contains("quantity") || lower.Contains("count") || lower.Contains("sold"))
                return "\"10\"";
            if (lower.Contains("cost") || lower.Contains("price") || lower.Contains("rate"))
                return "\"1.0\"";
            return "\"0\"";
        }

        private string ApplyPythonCast(string defaultLiteral, string cast)
        {
            return cast switch
            {
                "int" => $"int({defaultLiteral})",
                "float" => $"float({defaultLiteral})",
                "str" => $"str({defaultLiteral})",
                _ => defaultLiteral
            };
        }

        private void RecordSandboxInputSubstitution(string variableName, string action)
        {
            _lastSandboxInputSubstitutionCount++;
            _lastSandboxInputSubstitutionDetails.Add($"{variableName}: {action}");
        }

        private static string ExecuteInternal(string code, CancellationToken token, Action<ulong>? onThreadId, PyDict? globals)
        {
            token.ThrowIfCancellationRequested();

            using var gil = Py.GIL();
            onThreadId?.Invoke(PythonEngine.GetPythonThreadID());
            using var ioModule = Py.Import("io");
            using var sysModule = Py.Import("sys");
            using var stdoutIo = ioModule.InvokeMethod("StringIO");
            using var stderrIo = ioModule.InvokeMethod("StringIO");
            using var oldStdout = sysModule.GetAttr("stdout");
            using var oldStderr = sysModule.GetAttr("stderr");

            sysModule.SetAttr("stdout", stdoutIo);
            sysModule.SetAttr("stderr", stderrIo);
            try
            {
                if (globals != null)
                    PythonEngine.Exec(SandboxPrelude, globals, globals);
                else
                    PythonEngine.Exec(SandboxPrelude);

                if (globals != null)
                    PythonEngine.Exec(code, globals, globals);
                else
                    PythonEngine.Exec(code);

                string capturedStdout = stdoutIo.InvokeMethod("getvalue").ToString();
                string capturedStderr = stderrIo.InvokeMethod("getvalue").ToString();
                string combined = string.IsNullOrWhiteSpace(capturedStderr)
                    ? capturedStdout
                    : string.IsNullOrWhiteSpace(capturedStdout)
                        ? capturedStderr
                        : capturedStdout.TrimEnd() + "\n" + capturedStderr.Trim();

                return string.IsNullOrWhiteSpace(combined)
                    ? "Python completed with no printed output. Use print() to emit final answer."
                    : combined.Trim();
            }
            finally
            {
                sysModule.SetAttr("stdout", oldStdout);
                sysModule.SetAttr("stderr", oldStderr);
            }
        }
    }

    public sealed class PythonExecutionResult
    {
        public bool Success { get; private set; }
        public string Output { get; private set; } = string.Empty;
        public TimeSpan Duration { get; private set; }
        public string SanitizedCode { get; private set; } = string.Empty;
        public string ErrorType { get; private set; } = string.Empty;
        public int InputSubstitutionCount { get; private set; }
        public bool TimedOut { get; private set; }

        public static PythonExecutionResult Ok(string output, TimeSpan duration, string sanitizedCode, int inputSubstitutionCount) =>
            new PythonExecutionResult
            {
                Success = true,
                Output = output ?? string.Empty,
                Duration = duration,
                SanitizedCode = sanitizedCode ?? string.Empty,
                InputSubstitutionCount = inputSubstitutionCount
            };

        public static PythonExecutionResult Fail(string error, TimeSpan duration, string sanitizedCode = "", int inputSubstitutionCount = 0, bool timedOut = false, string errorType = "") =>
            new PythonExecutionResult
            {
                Success = false,
                Output = error ?? "Python execution failed.",
                Duration = duration,
                SanitizedCode = sanitizedCode ?? string.Empty,
                ErrorType = errorType ?? string.Empty,
                InputSubstitutionCount = inputSubstitutionCount,
                TimedOut = timedOut
            };
    }
}
