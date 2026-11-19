using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI
{
    internal sealed record AxiomDesktopSession(string Token, string Email, DateTimeOffset ExpiresAt);
    internal sealed record StoredAxiomAccount(string Endpoint, AxiomDesktopSession Session);

    internal sealed class AxiomAccountClient(Uri endpoint)
    {
        private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        public static Uri? ConfiguredEndpoint()
            => ParseEndpoint(Environment.GetEnvironmentVariable("AXIOM_MANAGED_GATEWAY_URL"));

        internal static Uri? ParseEndpoint(string? value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
                || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath.TrimEnd('/') != "/v1")
                return null;
            return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        }

        public async Task<HttpStatusCode> RequestCodeAsync(string email, CancellationToken cancellationToken = default)
        {
            using var response = await Http.PostAsJsonAsync(new Uri(endpoint, "accounts/request-code"),
                new { email }, cancellationToken);
            return response.StatusCode;
        }

        public Task<AxiomDesktopSession?> SignInAsync(
            string email, string code, CancellationToken cancellationToken = default)
            => ObtainSessionAsync("accounts/sign-in", new { email, code }, cancellationToken);

        public Task<AxiomDesktopSession?> RecoverAsync(
            string email, string code, string recoveryKey, CancellationToken cancellationToken = default)
            => ObtainSessionAsync("accounts/recover", new { email, code, recoveryKey }, cancellationToken);

        private async Task<AxiomDesktopSession?> ObtainSessionAsync<T>(string operation, T input, CancellationToken cancellationToken)
        {
            using var response = await Http.PostAsJsonAsync(new Uri(endpoint, operation), input, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return null;
            response.EnsureSuccessStatusCode();
            var session = await response.Content.ReadFromJsonAsync<AxiomDesktopSession>(cancellationToken: cancellationToken);
            if (session is { Token.Length: 64, Email.Length: > 0 } && session.ExpiresAt > DateTimeOffset.UtcNow)
                return session;
            throw new JsonException("The gateway did not return a valid sign-in session.");
        }

        public async Task<string?> IssueRecoveryKeyAsync(string token, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "accounts/recovery-key"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Http.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                return null;

            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("recoveryKey", out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: 68 } key && key.StartsWith("axr_", StringComparison.Ordinal))
                return key;
            throw new JsonException("The gateway did not return a valid recovery key.");
        }

        public async Task SignOutAsync(string token, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "accounts/sign-out"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Http.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
                response.EnsureSuccessStatusCode();
        }
    }
}
