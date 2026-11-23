using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI
{
    /// <summary>
    /// Anonymous diagnostics (errors, crashes and usage counts) sent to the Axiom gateway's
    /// /v1/diagnostics endpoint. On by default; Settings › General turns it off, which also
    /// deletes anything still waiting to be sent. Builds without a configured gateway send
    /// nothing. See <see cref="DiagnosticsReport"/> for what an event contains.
    /// </summary>
    internal static class DiagnosticsService
    {
        public const string EnabledSettingKey = "diagnostics_enabled";
        private const string InstallIdSettingKey = "diagnostics_install_id";
        private static readonly TimeSpan FlushInterval = TimeSpan.FromMinutes(15);
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
        private static readonly object Gate = new();
        private static readonly List<DiagnosticsEvent> Pending = new();
        private static readonly SemaphoreSlim SendGate = new(1, 1);
        private static Timer? _timer;
        private static bool _started;
        private static bool _enabled = true;
        private static string _installId = string.Empty;

        private static string PendingPath => Path.Combine(AppDataPaths.Root, "diagnostics-pending.json");

        public static bool Enabled
        {
            get { lock (Gate) return _enabled; }
        }

        /// <summary>Loads the preference, restores unsent events and starts the periodic send.</summary>
        public static void Start(string sessionDetail)
        {
            lock (Gate)
            {
                if (_started)
                    return;
                _started = true;
            }
            try
            {
                using var db = new DatabaseService();
                string stored = db.GetSetting(EnabledSettingKey);
                bool enabled = !string.Equals(stored, "false", StringComparison.OrdinalIgnoreCase);
                string id = db.GetSetting(InstallIdSettingKey);
                if (!DiagnosticsReport.IsValidInstallId(id))
                {
                    id = DiagnosticsReport.NewInstallId();
                    db.SaveSetting(InstallIdSettingKey, id);
                }
                lock (Gate)
                {
                    _enabled = enabled;
                    _installId = id;
                }
            }
            catch
            {
                // Diagnostics must never stop the app from starting.
            }

            BackendLogService.ErrorLogged += (area, ex) => RecordError(area, ex);
            if (!Enabled)
            {
                DeletePendingFile();
                return;
            }
            RestorePending();
            Record(DiagnosticsReport.Session(sessionDetail, DateTimeOffset.UtcNow));
            _timer = new Timer(_ => _ = FlushAsync(), null, TimeSpan.FromSeconds(30), FlushInterval);
        }

        public static void SetEnabled(bool enabled)
        {
            lock (Gate)
            {
                _enabled = enabled;
                if (!enabled)
                    Pending.Clear();
            }
            try
            {
                using var db = new DatabaseService();
                db.SaveSetting(EnabledSettingKey, enabled ? "true" : "false");
            }
            catch
            {
            }
            if (!enabled)
            {
                DeletePendingFile();
                _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            }
            else
            {
                _timer ??= new Timer(_ => _ = FlushAsync(), null, FlushInterval, FlushInterval);
                _timer.Change(FlushInterval, FlushInterval);
            }
        }

        public static void RecordError(string area, Exception exception)
        {
            // Unhandled exceptions arrive here too (they are also logged); they count once, as crashes.
            if (Enabled && exception is not OperationCanceledException
                && area is not ("App.DispatcherUnhandledException" or "App.CurrentDomainUnhandledException"))
                Record(DiagnosticsReport.FromException(area, exception, crash: false, DateTimeOffset.UtcNow));
        }

        /// <summary>A crash is written to disk at once, so it is sent on the next launch if the app dies now.</summary>
        public static void RecordCrash(string area, Exception exception)
        {
            if (!Enabled)
                return;
            Record(DiagnosticsReport.FromException(area, exception, crash: true, DateTimeOffset.UtcNow));
            SavePending();
        }

        /// <summary>Counts a use of a feature, e.g. ("Chat.Send", "cloud"). No content is recorded.</summary>
        public static void RecordFeature(string area, string detail)
        {
            if (Enabled)
                Record(DiagnosticsReport.Feature(area, detail, 1, DateTimeOffset.UtcNow));
        }

        /// <summary>Sends pending events. Safe to call any time; does nothing when off or offline.</summary>
        public static async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            Uri? endpoint = AxiomAccountClient.ConfiguredEndpoint();
            if (endpoint is null || !Enabled)
                return;
            if (!await SendGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                return;
            try
            {
                List<DiagnosticsEvent> batch;
                string installId;
                lock (Gate)
                {
                    if (Pending.Count == 0)
                        return;
                    batch = Pending.GetRange(0, Math.Min(50, Pending.Count));
                    installId = _installId;
                }
                if (!DiagnosticsReport.IsValidInstallId(installId))
                    return;

                string json = DiagnosticsReport.BuildPayload(installId, UpdateCheckService.GetCurrentVersionText(),
                    RuntimeInformation.OSDescription, batch);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await Http.PostAsync(new Uri(endpoint, "diagnostics"), content, cancellationToken).ConfigureAwait(false);
                // Drop the batch on success, and on a 400 (it will never be accepted). Keep it
                // for later on network errors, rate limits and server errors.
                if (response.IsSuccessStatusCode || (int)response.StatusCode == 400)
                {
                    lock (Gate)
                    {
                        foreach (var sent in batch)
                            Pending.Remove(sent);
                    }
                    SavePending();
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                // Offline or blocked: the events stay queued (capped) for the next attempt.
            }
            finally
            {
                SendGate.Release();
            }
        }

        private static void Record(DiagnosticsEvent next)
        {
            lock (Gate)
                DiagnosticsReport.Merge(Pending, next);
        }

        private static void SavePending()
        {
            try
            {
                string json;
                lock (Gate)
                {
                    if (!_enabled)
                        return;
                    json = JsonSerializer.Serialize(Pending);
                }
                Directory.CreateDirectory(AppDataPaths.Root);
                AtomicFileWriter.WriteAllText(PendingPath, json, keepBackup: false);
            }
            catch
            {
            }
        }

        private static void RestorePending()
        {
            try
            {
                if (!File.Exists(PendingPath))
                    return;
                var saved = JsonSerializer.Deserialize<List<DiagnosticsEvent>>(File.ReadAllText(PendingPath));
                if (saved is null)
                    return;
                lock (Gate)
                {
                    foreach (var e in saved)
                        DiagnosticsReport.Merge(Pending, e);
                }
            }
            catch
            {
                DeletePendingFile();
            }
        }

        private static void DeletePendingFile()
        {
            try
            {
                if (File.Exists(PendingPath))
                    File.Delete(PendingPath);
            }
            catch
            {
            }
        }

        /// <summary>Keeps unsent events for the next launch. No network at exit.</summary>
        public static void SaveOnExit() => SavePending();
    }
}
