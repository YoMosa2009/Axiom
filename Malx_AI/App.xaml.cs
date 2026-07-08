using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace Malx_AI
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public App()
        {
            try
            {
                // GPU contention note: WPF renders its UI through Direct3D and WebView2 (Chromium)
                // through its own GPU process, both of which can compete with the llama.cpp CUDA
                // decode on a single, VRAM-saturated card. We DELIBERATELY do NOT force all of WPF
                // to software rendering here: that made the whole UI (Settings panel fade, shadows,
                // every animation) render on the CPU and lag badly on every machine, while NOT
                // reliably preventing the crash. Instead, GPU-inference stability is guaranteed by
                // the crash-ledger safety net (NativeCrashLedger): a model that ever dies under CUDA
                // is automatically loaded on CPU — where there is zero GPU contention — until the
                // user explicitly retries GPU. That keeps the UI smooth and GPU-accelerated for
                // everyone whose hardware can sustain it, and makes the app self-heal everywhere
                // else. WebView2 panes still render static HTML and gain nothing from the GPU, so
                // they stay pinned to software rendering (cheap, no animation cost) as a lightweight
                // way to leave more of the GPU to inference.
                Environment.SetEnvironmentVariable(
                    "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
                    "--disable-gpu --disable-gpu-compositing");

                // Configure native LLamaSharp backend FIRST — before any LLama types are loaded.
                // This must happen before MainWindow, HardwareProfiler, or any ModelParams usage.
                NativeBackendInit.Configure();

                // Initialize portable embedded Python runtime in background for sandbox tools.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var py = new PythonExecutionService();
                        await py.InitializeAsync();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Python runtime initialization error: {ex.Message}");
                        await BackendLogService.LogErrorAsync("App.PythonInit", ex);
                    }
                });

                AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
                DispatcherUnhandledException += App_DispatcherUnhandledException;

                // The app fires many deliberate fire-and-forget tasks (logging, persistence,
                // usage refresh). A faulted one must be observed and recorded, never allowed
                // to vanish silently or (on older runtimes/policies) escalate.
                TaskScheduler.UnobservedTaskException += (_, args) =>
                {
                    args.SetObserved();
                    _ = BackendLogService.LogErrorAsync("App.UnobservedTaskException", args.Exception);
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"App initialization error: {ex}");
            }
        }

        // A second Axiom instance shares the same SQLite database and JSON session files as the
        // first; whichever saves last silently clobbers the other's chats and workplace state.
        // A named mutex makes the app single-instance, with a clear message instead of quiet
        // state corruption.
        private Mutex? _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\Axiom_SingleInstance", out bool isFirstInstance);
                if (!isFirstInstance)
                {
                    MessageBox.Show(
                        "Axiom is already running.\n\nUse the existing window — running two copies at once would overwrite each other's chats and settings.",
                        "Axiom",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    Shutdown();
                    return;
                }
            }
            catch (Exception ex)
            {
                // A mutex problem must never block startup — worst case we run without the guard.
                Debug.WriteLine($"Single-instance guard error: {ex.Message}");
            }

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Reaching OnExit means a GRACEFUL shutdown — a native llama.cpp abort fail-fasts the
            // process and never runs this. So clear any in-flight decode marker now: a turn the user
            // interrupted by simply closing the app while it was still generating must NOT be misread
            // as a GPU crash on the next launch (that false strike would pin the model to CPU even
            // though the GPU is healthy — see NativeDecodeForensics.MarkCleanShutdown).
            NativeDecodeForensics.MarkCleanShutdown();
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
                _singleInstanceMutex?.Dispose();
            }
            catch
            {
                // Never fail shutdown over mutex cleanup.
            }
            base.OnExit(e);
        }

        private static string BuildUserFacingErrorText(Exception ex, bool critical)
        {
            // End users get a readable summary and the log location; the full stack trace goes
            // to backend-errors.log, not the dialog.
            string headline = critical
                ? "Axiom hit a critical error and may need to restart."
                : "Axiom hit an unexpected error. The app will keep running.";
            return headline
                + $"\n\nDetails: {ex.Message}"
                + $"\n\nA full technical report was saved to:\n{AppDataPaths.Logs}\\backend-errors.log"
                + "\n\nIf this keeps happening, please attach that file to a GitHub issue.";
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            Debug.WriteLine($"Unhandled dispatcher exception: {e.Exception}");
            _ = BackendLogService.LogErrorAsync("App.DispatcherUnhandledException", e.Exception);
            MessageBox.Show(BuildUserFacingErrorText(e.Exception, critical: false), "Axiom — Error", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            Exception ex = (Exception)e.ExceptionObject;
            Debug.WriteLine($"Unhandled domain exception: {ex}");
            _ = BackendLogService.LogErrorAsync("App.CurrentDomainUnhandledException", ex);
            MessageBox.Show(BuildUserFacingErrorText(ex, critical: true), "Axiom — Critical Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

    }
}
