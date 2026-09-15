using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI.ComputerUse;

internal sealed class ComputerUseInferenceInterruptedException : Exception
{
    public ComputerUseInferenceInterruptedException(string message, Exception inner) : base(message, inner) { }
}

internal static class ComputerUseInference
{
    public static async Task<string> RunAsync(Func<CancellationToken, Task<string>> infer,
        TimeSpan timeout, CancellationToken sessionToken, Action? onDraining = null)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        Task<string>? pending = null;
        try
        {
            pending = infer(cancellation.Token);
            string response = await pending.WaitAsync(timeout, sessionToken).ConfigureAwait(true);
            // A provider that dies, drops the connection, or exhausts its budget can complete the
            // call with nothing to show for it. That is an interrupted turn, not a badly formatted
            // one: routing it through the parse-error path would burn desktop-control steps
            // re-asking a provider that produced no output at all.
            if (string.IsNullOrWhiteSpace(response))
                throw new ComputerUseInferenceInterruptedException(
                    "The model returned an empty response.", new InvalidOperationException("Empty model response."));
            return response;
        }
        catch (TimeoutException ex)
        {
            cancellation.Cancel();
            // Never start another inference while the previous one still owns model/image state.
            onDraining?.Invoke();
            if (pending != null)
            {
                try { await pending.WaitAsync(sessionToken).ConfigureAwait(true); }
                catch (Exception) when (!sessionToken.IsCancellationRequested) { }
            }
            sessionToken.ThrowIfCancellationRequested();
            throw new ComputerUseInferenceInterruptedException("The model response timed out.", ex);
        }
        catch (OperationCanceledException ex) when (!sessionToken.IsCancellationRequested)
        {
            throw new ComputerUseInferenceInterruptedException("The model provider canceled its response.", ex);
        }
        catch (Exception ex) when (!sessionToken.IsCancellationRequested
            && ex is not OutOfMemoryException and not ComputerUseInferenceInterruptedException)
        {
            throw new ComputerUseInferenceInterruptedException(ex.Message, ex);
        }
    }
}
