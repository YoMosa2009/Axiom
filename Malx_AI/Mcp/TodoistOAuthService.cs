using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI.Mcp
{
    /// <summary>
    /// Todoist OAuth2 authorization-code flow via fixed loopback redirect.
    /// Register redirect URI exactly: http://127.0.0.1:17466/oauth2/callback/
    /// App Management: https://app.todoist.com/app/settings/integrations/app-management
    /// </summary>
    internal sealed class TodoistOAuthService
    {
        public const int LoopbackPort = 17466;
        public const string RedirectUri = "http://127.0.0.1:17466/oauth2/callback/";
        /// <summary>Full practical access for agent task management.</summary>
        public const string DefaultScopes = "data:read_write,data:delete,project:delete";

        private const string AuthorizeEndpoint = "https://app.todoist.com/oauth/authorize";
        private const string TokenEndpoint = "https://api.todoist.com/oauth/access_token";
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Axiom-MCP/1.0");
            return client;
        }

        public async Task<McpTokenBundle> AuthorizeAsync(
            string clientId,
            string clientSecret,
            CancellationToken token,
            IProgress<string>? progress = null)
        {
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                throw new InvalidOperationException(
                    "Todoist Client ID/Secret are not configured.\n\n" +
                    "1) Open https://app.todoist.com/app/settings/integrations/app-management\n" +
                    "2) Create an app and set Redirect URI exactly:\n   " + RedirectUri + "\n" +
                    "3) Put Client ID and Secret in McpOAuthConfig.BuiltInTodoistClientId/Secret\n" +
                    "   or %LocalAppData%\\Axiom\\todoist_client_id.txt and todoist_client_secret.txt");
            }

            string state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

            using var listener = new HttpListener();
            listener.Prefixes.Add(RedirectUri);
            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not listen on {RedirectUri}. Close anything using port {LoopbackPort} and try again. ({ex.Message})", ex);
            }

            string authUrl =
                AuthorizeEndpoint
                + "?client_id=" + Uri.EscapeDataString(clientId.Trim())
                + "&scope=" + Uri.EscapeDataString(DefaultScopes)
                + "&state=" + Uri.EscapeDataString(state)
                + "&response_type=code"
                + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri);

            progress?.Report("Opening your browser to sign in with Todoist…");
            try
            {
                Process.Start(new ProcessStartInfo { FileName = authUrl, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Could not open the browser for Todoist sign-in: " + ex.Message, ex);
            }

            progress?.Report("Waiting for you to finish Todoist authorization…");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException("Todoist sign-in timed out. Click Connect and try again.");
            }

            string? code = context.Request.QueryString["code"];
            string? returnedState = context.Request.QueryString["state"];
            string? error = context.Request.QueryString["error"];

            bool ok = string.IsNullOrWhiteSpace(error) && !string.IsNullOrWhiteSpace(code);
            string html = ok
                ? """
                  <!DOCTYPE html><html><body style="font-family:Segoe UI,sans-serif;background:#171615;color:#EDE8E3;padding:48px;text-align:center">
                  <h2 style="color:#B8924A">Connected to Axiom</h2>
                  <p>You can close this tab and return to the app.</p>
                  <script>setTimeout(function(){try{window.close()}catch(e){}},1200)</script>
                  </body></html>
                  """
                : "<html><body style='font-family:Segoe UI;background:#171615;color:#EDE8E3;padding:40px'><h2>Sign-in failed</h2><p>"
                  + WebUtility.HtmlEncode(error ?? "Missing code") + "</p></body></html>";

            byte[] buffer = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = buffer.Length;
            context.Response.StatusCode = ok ? 200 : 400;
            await context.Response.OutputStream.WriteAsync(buffer, token).ConfigureAwait(false);
            context.Response.OutputStream.Close();
            listener.Stop();

            if (!string.IsNullOrWhiteSpace(error))
                throw new InvalidOperationException("Todoist authorization failed: " + error);
            if (string.IsNullOrWhiteSpace(code))
                throw new InvalidOperationException("Todoist did not return an authorization code.");
            if (!string.Equals(returnedState, state, StringComparison.Ordinal))
                throw new InvalidOperationException("OAuth state mismatch. Try Connect again.");

            progress?.Report("Finishing Todoist connection…");
            McpTokenBundle tokens = await ExchangeCodeAsync(clientId.Trim(), clientSecret.Trim(), code, token)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(tokens.AccountEmail))
                tokens.AccountEmail = await TryFetchUserLabelAsync(tokens.AccessToken, token).ConfigureAwait(false) ?? "Todoist";

            return tokens;
        }

        public async Task<McpTokenBundle> RefreshAsync(
            string clientId,
            string clientSecret,
            McpTokenBundle existing,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(existing.RefreshToken))
                throw new InvalidOperationException("No Todoist refresh token. Disconnect and connect again.");

            var form = new Dictionary<string, string>
            {
                ["client_id"] = clientId.Trim(),
                ["client_secret"] = clientSecret.Trim(),
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = existing.RefreshToken
            };

            using var content = new FormUrlEncodedContent(form);
            using HttpResponseMessage response = await Http.PostAsync(TokenEndpoint, content, token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Todoist token refresh failed: " + Truncate(body, 300));

            return ParseTokenResponse(body, existing.AccountEmail, existing.RefreshToken);
        }

        private static async Task<McpTokenBundle> ExchangeCodeAsync(
            string clientId,
            string clientSecret,
            string code,
            CancellationToken token)
        {
            var form = new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["code"] = code,
                ["redirect_uri"] = RedirectUri
            };

            using var content = new FormUrlEncodedContent(form);
            using HttpResponseMessage response = await Http.PostAsync(TokenEndpoint, content, token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Todoist token exchange failed: " + Truncate(body, 400));

            return ParseTokenResponse(body, accountEmail: string.Empty, previousRefresh: string.Empty);
        }

        private static McpTokenBundle ParseTokenResponse(string body, string accountEmail, string previousRefresh)
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            string access = root.GetProperty("access_token").GetString() ?? "";
            string refresh = root.TryGetProperty("refresh_token", out JsonElement rt)
                ? rt.GetString() ?? previousRefresh
                : previousRefresh;
            int expiresIn = root.TryGetProperty("expires_in", out JsonElement exp) && exp.TryGetInt32(out int s)
                ? s
                : 3600;
            // Legacy long-lived tokens use a 10-year expires_in; treat as non-expiring.
            DateTime expiresAt = expiresIn > 86400 * 30
                ? DateTime.UtcNow.AddYears(10)
                : DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60));
            string scope = root.TryGetProperty("scope", out JsonElement sc) ? sc.GetString() ?? DefaultScopes : DefaultScopes;

            return new McpTokenBundle
            {
                AccessToken = access,
                RefreshToken = refresh,
                ExpiresAtUtc = expiresAt,
                AccountEmail = accountEmail,
                Scope = scope
            };
        }

        private static async Task<string?> TryFetchUserLabelAsync(string accessToken, CancellationToken token)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.todoist.com/api/v1/user");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return null;
                string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(body);
                string? email = doc.RootElement.TryGetProperty("email", out JsonElement e) ? e.GetString() : null;
                string? fullName = doc.RootElement.TryGetProperty("full_name", out JsonElement n) ? n.GetString() : null;
                if (!string.IsNullOrWhiteSpace(fullName) && !string.IsNullOrWhiteSpace(email))
                    return $"{fullName} ({email})";
                return fullName ?? email;
            }
            catch
            {
                return null;
            }
        }

        private static string Truncate(string text, int max)
            => string.IsNullOrEmpty(text) || text.Length <= max ? text ?? "" : text[..max] + "…";
    }
}
