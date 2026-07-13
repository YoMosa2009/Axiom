using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI.Mcp
{
    /// <summary>
    /// GitHub OAuth device flow — browser opens, user enters a code / authorizes, app polls for token.
    /// No fixed redirect URI required (better for desktop than loopback).
    /// </summary>
    internal sealed class GitHubOAuthService
    {
        private const string DeviceCodeEndpoint = "https://github.com/login/device/code";
        private const string TokenEndpoint = "https://github.com/login/oauth/access_token";
        private const string ApiBase = "https://api.github.com";
        /// <summary>Full practical access for repo code, issues, PRs, actions, gists, org read.</summary>
        public const string DefaultScopes = "repo read:user user:email read:org gist workflow project";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Axiom-MCP/1.0");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }

        public async Task<McpTokenBundle> AuthorizeDeviceAsync(
            string clientId,
            string? clientSecret,
            CancellationToken token,
            IProgress<string>? progress = null)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                throw new InvalidOperationException(
                    "GitHub OAuth Client ID is not configured. Create an OAuth App at GitHub → Settings → Developer settings → OAuth Apps, " +
                    "then set McpOAuthConfig.BuiltInGitHubClientId (and Client Secret), or use AXIOM_GITHUB_OAUTH_CLIENT_ID / client secret file.");

            progress?.Report("Requesting GitHub device code…");

            var codeForm = new Dictionary<string, string>
            {
                ["client_id"] = clientId.Trim(),
                ["scope"] = DefaultScopes
            };
            using var codeContent = new FormUrlEncodedContent(codeForm);
            using HttpResponseMessage codeResponse = await Http.PostAsync(DeviceCodeEndpoint, codeContent, token).ConfigureAwait(false);
            string codeBody = await codeResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!codeResponse.IsSuccessStatusCode)
                throw new InvalidOperationException("GitHub device code request failed: " + Truncate(codeBody, 400));

            using JsonDocument codeDoc = JsonDocument.Parse(codeBody);
            JsonElement root = codeDoc.RootElement;
            string deviceCode = root.GetProperty("device_code").GetString() ?? "";
            string userCode = root.GetProperty("user_code").GetString() ?? "";
            string verificationUri = root.TryGetProperty("verification_uri", out JsonElement vu)
                ? vu.GetString() ?? "https://github.com/login/device"
                : "https://github.com/login/device";
            int interval = root.TryGetProperty("interval", out JsonElement iv) && iv.TryGetInt32(out int sec) ? Math.Max(5, sec) : 5;
            int expiresIn = root.TryGetProperty("expires_in", out JsonElement exp) && exp.TryGetInt32(out int e) ? e : 900;

            if (string.IsNullOrWhiteSpace(deviceCode) || string.IsNullOrWhiteSpace(userCode))
                throw new InvalidOperationException("GitHub did not return a device/user code.");

            // Signal the UI to show the code in a blocking dialog + copy to clipboard.
            // Format: DEVICE_CODE|<user_code>|<verification_uri>
            string verificationPage = string.IsNullOrWhiteSpace(verificationUri)
                ? "https://github.com/login/device"
                : verificationUri.Trim();
            progress?.Report($"DEVICE_CODE|{userCode}|{verificationPage}");

            // Open the plain device page (user enters the code shown in Axiom).
            try
            {
                Process.Start(new ProcessStartInfo { FileName = verificationPage, UseShellExecute = true });
            }
            catch
            {
                // User can still open the URI from the dialog message.
            }

            progress?.Report($"Waiting for GitHub… enter code {userCode} in the browser, then authorize.");

            var deadline = DateTime.UtcNow.AddSeconds(expiresIn);
            while (DateTime.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromSeconds(interval), token).ConfigureAwait(false);

                var tokenForm = new Dictionary<string, string>
                {
                    ["client_id"] = clientId.Trim(),
                    ["device_code"] = deviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                };
                if (!string.IsNullOrWhiteSpace(clientSecret))
                    tokenForm["client_secret"] = clientSecret.Trim();

                using var tokenContent = new FormUrlEncodedContent(tokenForm);
                using HttpResponseMessage tokenResponse = await Http.PostAsync(TokenEndpoint, tokenContent, token).ConfigureAwait(false);
                string tokenBody = await tokenResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false);

                // GitHub may return 200 with error=authorization_pending in JSON body.
                using JsonDocument tokenDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(tokenBody) ? "{}" : tokenBody);
                JsonElement tr = tokenDoc.RootElement;
                if (tr.TryGetProperty("error", out JsonElement errEl))
                {
                    string err = errEl.GetString() ?? "";
                    if (string.Equals(err, "authorization_pending", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(err, "slow_down", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.Equals(err, "slow_down", StringComparison.OrdinalIgnoreCase))
                            interval += 5;
                        continue;
                    }

                    if (string.Equals(err, "expired_token", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(err, "access_denied", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("GitHub authorization " + err.Replace('_', ' ') + ". Try Connect again.");
                    }

                    throw new InvalidOperationException("GitHub authorization failed: " + err);
                }

                string accessToken = tr.TryGetProperty("access_token", out JsonElement at)
                    ? at.GetString() ?? ""
                    : "";
                if (string.IsNullOrWhiteSpace(accessToken))
                    continue;

                string scope = tr.TryGetProperty("scope", out JsonElement sc) ? sc.GetString() ?? DefaultScopes : DefaultScopes;
                string login = await TryFetchLoginAsync(accessToken, token).ConfigureAwait(false) ?? "GitHub";

                return new McpTokenBundle
                {
                    AccessToken = accessToken,
                    RefreshToken = string.Empty, // classic OAuth tokens don't refresh
                    ExpiresAtUtc = DateTime.UtcNow.AddYears(10),
                    AccountEmail = login,
                    Scope = scope
                };
            }

            throw new TimeoutException("GitHub authorization timed out. Click Connect and try again.");
        }

        private static async Task<string?> TryFetchLoginAsync(string accessToken, CancellationToken token)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + "/user");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                request.Headers.UserAgent.ParseAdd("Axiom-MCP/1.0");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return null;
                string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(body);
                string? login = doc.RootElement.TryGetProperty("login", out JsonElement l) ? l.GetString() : null;
                string? email = doc.RootElement.TryGetProperty("email", out JsonElement e) ? e.GetString() : null;
                if (!string.IsNullOrWhiteSpace(login) && !string.IsNullOrWhiteSpace(email))
                    return $"{login} ({email})";
                return login ?? email;
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
