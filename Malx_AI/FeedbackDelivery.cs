using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI
{
    /// <summary>
    /// Gets a feedback report to the developer's inbox.
    /// </summary>
    /// <remarks>
    /// Direct sending goes through Web3Forms, a free form-to-email relay (250 messages a month):
    /// the app posts the report and Web3Forms emails it to the address the access key was created
    /// for. The access key is designed to be public (it can only send mail to that one inbox), so it
    /// can live in the app. Until a key is set, or when sending fails, the report opens as a ready
    /// email in the user's mail app instead, so feedback is never lost.
    /// </remarks>
    internal static class FeedbackDelivery
    {
        public const string FeedbackEmail = "Malxshrouds@gmail.com";

        // Created at https://web3forms.com for FeedbackEmail. Public by design: it can only send to that inbox.
        public const string Web3FormsAccessKey = "bc1fcf77-bb2f-401d-a7e1-268b638216e2";

        private const string Web3FormsEndpoint = "https://api.web3forms.com/submit";
        private const int MailtoBodyLimit = 1800;

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

        public static bool CanSendDirectly => !string.IsNullOrWhiteSpace(Web3FormsAccessKey);

        /// <summary>Returns null on success, otherwise a short reason for the fallback message.</summary>
        public static async Task<string?> TrySendAsync(FeedbackReport report, string? replyEmail, CancellationToken cancellationToken)
        {
            if (!CanSendDirectly)
                return "Direct sending isn't set up in this version.";

            var payload = new
            {
                access_key = Web3FormsAccessKey,
                subject = report.Subject,
                from_name = "Axiom app",
                email = FeedbackReportBuilder.IsPlausibleEmail(replyEmail) ? replyEmail!.Trim() : null,
                message = report.Body
            };

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, Web3FormsEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Axiom", UpdateCheckService.GetCurrentVersionText()));

                using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode && ReadSuccess(body))
                    return null;

                _ = BackendLogService.LogEventAsync("Feedback", $"Web3Forms rejected the report: HTTP {(int)response.StatusCode} {Truncate(body, 300)}");
                return $"The feedback service answered with an error (HTTP {(int)response.StatusCode}).";
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return "The feedback service didn't answer in time.";
            }
            catch (HttpRequestException ex)
            {
                _ = BackendLogService.LogEventAsync("Feedback", "Sending failed: " + ex.Message);
                return "Axiom couldn't reach the feedback service. Check your internet connection.";
            }
        }

        /// <summary>Opens the user's mail app with the report filled in (trimmed to fit a mailto link).</summary>
        public static bool TryOpenEmailDraft(FeedbackReport report)
        {
            try
            {
                string uri = FeedbackReportBuilder.BuildMailtoUri(FeedbackEmail, report, MailtoBodyLimit);
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                _ = BackendLogService.LogEventAsync("Feedback", "Couldn't open a mail app: " + ex.Message);
                return false;
            }
        }

        private static bool ReadSuccess(string json)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty("success", out JsonElement success)
                    && success.ValueKind == JsonValueKind.True;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string Truncate(string text, int max)
            => string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max] + "…";
    }
}
