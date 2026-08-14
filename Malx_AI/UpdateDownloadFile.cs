using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI
{
    internal static class UpdateDownloadFile
    {
        internal static async Task<long> WriteAsync(
            Stream source,
            string destinationPath,
            long maximumBytes,
            long expectedBytes,
            IProgress<double>? progress,
            CancellationToken token)
        {
            await using (source.ConfigureAwait(false))
            await using (var target = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                useAsync: true))
            {
                var buffer = new byte[1024 * 1024];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > maximumBytes)
                        throw new InvalidOperationException("The downloaded update exceeds Axiom's 4 GB safety limit.");

                    await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    if (expectedBytes > 0)
                        progress?.Report(Math.Clamp(received * 100.0 / expectedBytes, 0, 100));
                }

                await target.FlushAsync(token).ConfigureAwait(false);
                return received;
            }
        }
    }
}
