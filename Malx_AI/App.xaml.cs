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
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"App initialization error: {ex}");
            }
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            Debug.WriteLine($"Unhandled dispatcher exception: {e.Exception}");
            _ = BackendLogService.LogErrorAsync("App.DispatcherUnhandledException", e.Exception);
            MessageBox.Show($"Application error:\n\n{e.Exception.Message}\n\n{e.Exception.StackTrace}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            Exception ex = (Exception)e.ExceptionObject;
            Debug.WriteLine($"Unhandled domain exception: {ex}");
            _ = BackendLogService.LogErrorAsync("App.CurrentDomainUnhandledException", ex);
            MessageBox.Show($"Critical application error:\n\n{ex.Message}\n\n{ex.StackTrace}", "Critical Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

    }
}
