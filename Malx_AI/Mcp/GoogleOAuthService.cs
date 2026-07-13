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
    /// Desktop OAuth 2.0 (authorization code + PKCE) for Google via loopback redirect.
    /// Same user flow as Claude/ChatGPT connectors: open browser → sign in → return to app.
    /// </summary>
    internal sealed class GoogleOAuthService
    {
        private const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
        private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
        private const string UserInfoEndpoint = "https://www.googleapis.com/oauth2/v2/userinfo";
        private static readonly HttpClient Http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Axiom-MCP/1.0");
            return client;
        }

        public async Task<McpTokenBundle> AuthorizeAsync(
            string clientId,
            string? clientSecret,
            IReadOnlyList<string> scopes,
            CancellationToken token,
            IProgress<string>? progress = null,
            bool forceConsent = true)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                throw new InvalidOperationException(
                    "Google sign-in is not configured for this build of Axiom. " +
                    "The app needs a Desktop OAuth client id (set AXIOM_GOOGLE_OAUTH_CLIENT_ID or embed it for release).");

            if (string.IsNullOrWhiteSpace(clientSecret))
                throw new InvalidOperationException(
                    "Google OAuth client secret is missing. Open Google Cloud Console → APIs & Services → Credentials → " +
                    "your Desktop OAuth client, copy the Client secret, and put it in McpOAuthConfig.BuiltInGoogleDesktopClientSecret " +
                    "(or %LocalAppData%\\Axiom\\google_oauth_client_secret.txt).");

            if (scopes == null || scopes.Count == 0)
                throw new ArgumentException("At least one OAuth scope is required.", nameof(scopes));

            string codeVerifier = CreateCodeVerifier();
            string codeChallenge = CreateCodeChallenge(codeVerifier);
            string state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

            using var listener = new HttpListener();
            int port = PickFreePort();
            // Trailing slash is required — must match redirect_uri sent to Google exactly.
            string redirectUri = $"http://127.0.0.1:{port}/oauth2/callback/";
            listener.Prefixes.Add(redirectUri);
            listener.Start();

            string scopeParam = string.Join(' ', scopes);
            string authUrl =
                AuthorizationEndpoint
                + "?response_type=code"
                + "&client_id=" + Uri.EscapeDataString(clientId.Trim())
                + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
                + "&scope=" + Uri.EscapeDataString(scopeParam)
                + "&state=" + Uri.EscapeDataString(state)
                + "&code_challenge=" + Uri.EscapeDataString(codeChallenge)
                + "&code_challenge_method=S256"
                + "&access_type=offline"
                + "&include_granted_scopes=true"
                + (forceConsent ? "&prompt=consent" : "&prompt=select_account");

            progress?.Report("Opening your browser to sign in with Google…");

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = authUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Could not open the browser for Google sign-in: " + ex.Message, ex);
            }

            progress?.Report("Waiting for you to finish signing in…");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException("Google sign-in timed out. Click Connect again and complete login in the browser.");
            }

            string? code = context.Request.QueryString["code"];
            string? returnedState = context.Request.QueryString["state"];
            string? error = context.Request.QueryString["error"];

            bool success = string.IsNullOrWhiteSpace(error) && !string.IsNullOrWhiteSpace(code);
            string html = success
                ? BuildSuccessHtml()
                : BuildFailureHtml(error ?? "Missing authorization code.");

            byte[] buffer = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = buffer.Length;
            context.Response.StatusCode = success ? 200 : 400;
            await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
            context.Response.OutputStream.Close();
            listener.Stop();

            if (!string.IsNullOrWhiteSpace(error))
                throw new InvalidOperationException("Google authorization failed: " + error);

            if (string.IsNullOrWhiteSpace(code))
                throw new InvalidOperationException("Google authorization did not return a code.");

            if (!string.Equals(returnedState, state, StringComparison.Ordinal))
                throw new InvalidOperationException("OAuth state mismatch. Please try connecting again.");

            progress?.Report("Finishing connection…");

            McpTokenBundle tokens = await ExchangeCodeAsync(
                    clientId.Trim(),
                    clientSecret?.Trim() ?? string.Empty,
                    code,
                    redirectUri,
                    codeVerifier,
                    token)
                .ConfigureAwait(false);
            tokens.Scope = scopeParam;

            if (string.IsNullOrWhiteSpace(tokens.AccountEmail) && !string.IsNullOrWhiteSpace(tokens.AccessToken))
                tokens.AccountEmail = await TryFetchEmailAsync(tokens.AccessToken, token).ConfigureAwait(false) ?? string.Empty;

            return tokens;
        }

        public async Task<McpTokenBundle> RefreshAsync(
            string clientId,
            string? clientSecret,
            McpTokenBundle existing,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                throw new InvalidOperationException("Google sign-in is not configured for this build.");
            if (existing == null || string.IsNullOrWhiteSpace(existing.RefreshToken))
                throw new InvalidOperationException("Session expired. Disconnect and connect again.");

            var form = new Dictionary<string, string>
            {
                ["client_id"] = clientId.Trim(),
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = existing.RefreshToken
            };
            if (!string.IsNullOrWhiteSpace(clientSecret))
                form["client_secret"] = clientSecret.Trim();

            using var content = new FormUrlEncodedContent(form);
            using HttpResponseMessage response = await Http.PostAsync(TokenEndpoint, content, token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Token refresh failed: " + Truncate(body, 300));

            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            string accessToken = root.GetProperty("access_token").GetString() ?? string.Empty;
            int expiresIn = root.TryGetProperty("expires_in", out JsonElement expEl) && expEl.TryGetInt32(out int seconds)
                ? seconds
                : 3600;
            string refresh = root.TryGetProperty("refresh_token", out JsonElement rtEl)
                ? rtEl.GetString() ?? existing.RefreshToken
                : existing.RefreshToken;

            return new McpTokenBundle
            {
                AccessToken = accessToken,
                RefreshToken = refresh,
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60)),
                AccountEmail = existing.AccountEmail,
                Scope = existing.Scope
            };
        }

        private static string BuildSuccessHtml()
        {
            return """
                <!DOCTYPE html>
                <html><head><meta charset="utf-8"/><title>Connected — Axiom</title>
                <style>
                  body{font-family:'Segoe UI',system-ui,sans-serif;background:#171615;color:#EDE8E3;
                       display:flex;align-items:center;justify-content:center;min-height:100vh;margin:0}
                  .card{background:#211F1D;border:1px solid #B8924A;border-radius:16px;padding:36px 40px;
                        max-width:420px;text-align:center;box-shadow:0 12px 40px rgba(0,0,0,.35)}
                  h1{color:#B8924A;font-size:22px;font-weight:600;margin:0 0 12px}
                  p{color:#B0A89F;font-size:14px;line-height:1.5;margin:0}
                  .check{font-size:40px;margin-bottom:12px}
                </style></head>
                <body><div class="card">
                  <div class="check">✓</div>
                  <h1>You're connected</h1>
                  <p>Return to Axiom — this tab can be closed.</p>
                </div>
                <script>setTimeout(function(){ try{ window.close(); }catch(e){} }, 1200);</script>
                </body></html>
                """;
        }

        private static string BuildFailureHtml(string error)
        {
            return "<!DOCTYPE html><html><head><meta charset='utf-8'/><title>Sign-in failed</title></head>"
                + "<body style=\"font-family:Segoe UI,sans-serif;background:#171615;color:#EDE8E3;padding:48px\">"
                + "<h2 style=\"color:#E07070\">Sign-in did not complete</h2>"
                + "<p>" + WebUtility.HtmlEncode(error) + "</p>"
                + "<p style=\"color:#B0A89F\">Return to Axiom and try Connect again.</p></body></html>";
        }

        private static async Task<McpTokenBundle> ExchangeCodeAsync(
            string clientId,
            string clientSecret,
            string code,
            string redirectUri,
            string codeVerifier,
            CancellationToken token)
        {
            var form = new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["code"] = code,
                ["code_verifier"] = codeVerifier,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = redirectUri
            };
            // Google requires client_secret on the token endpoint for most OAuth client types,
            // including Desktop clients that ship the secret in the app binary.
            if (!string.IsNullOrWhiteSpace(clientSecret))
                form["client_secret"] = clientSecret;

            using var content = new FormUrlEncodedContent(form);
            using HttpResponseMessage response = await Http.PostAsync(TokenEndpoint, content, token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Token exchange failed: " + Truncate(body, 400));

            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            string accessToken = root.GetProperty("access_token").GetString() ?? string.Empty;
            string refreshToken = root.TryGetProperty("refresh_token", out JsonElement rt)
                ? rt.GetString() ?? string.Empty
                : string.Empty;
            int expiresIn = root.TryGetProperty("expires_in", out JsonElement expEl) && expEl.TryGetInt32(out int seconds)
                ? seconds
                : 3600;

            return new McpTokenBundle
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60)),
                AccountEmail = string.Empty,
                Scope = string.Empty
            };
        }

        private static async Task<string?> TryFetchEmailAsync(string accessToken, CancellationToken token)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return null;

                string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("email", out JsonElement emailEl))
                    return emailEl.GetString();
            }
            catch
            {
                // optional
            }

            return null;
        }

        private static string CreateCodeVerifier()
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(32);
            return Base64Url(bytes);
        }

        private static string CreateCodeChallenge(string verifier)
        {
            byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
            return Base64Url(hash);
        }

        private static string Base64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static int PickFreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max)
                return text ?? string.Empty;
            return text[..max] + "…";
        }
    }
}
