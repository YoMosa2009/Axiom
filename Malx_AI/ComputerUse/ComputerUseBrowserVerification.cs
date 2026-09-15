using System;

namespace Malx_AI.ComputerUse
{
    internal static class ComputerUseBrowserVerification
    {
        public static bool TryGetAbsoluteHttpUrl(string? value, out string url)
        {
            url = string.Empty;
            if (!Uri.TryCreate((value ?? string.Empty).Trim(), UriKind.Absolute, out Uri? parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                return false;
            }

            url = parsed.AbsoluteUri;
            return true;
        }

        public static string Describe(ComputerUseBrowserState? state)
        {
            if (state == null || !state.IsAvailable)
                return "Browser state is unavailable; use the screenshot as the only evidence.";

            string address = string.IsNullOrWhiteSpace(state.Address) ? "unknown" : state.Address;
            string tabs = state.TabCount == 0 ? "unknown" : state.TabCount.ToString();
            string title = string.IsNullOrWhiteSpace(state.WindowTitle) ? "unknown" : state.WindowTitle;
            return $"Address field: {address}\nAddress focused: {state.AddressHasFocus}\nLoaded document: {state.DocumentAddress}\nTabs: {tabs}\nWindow title: {title}\nError page detected: {(state.IsErrorPage ? "yes" : "no")}";
        }

        public static bool IsNewTabConfirmed(int tabCountBefore, ComputerUseBrowserState? current)
            => tabCountBefore >= 0 && current?.HasTabTelemetry == true && current.TabCount > tabCountBefore;

        /// <summary>
        /// True when a new-tab request was recorded without a usable "before" count, so no count
        /// delta can prove or disprove it.
        /// </summary>
        public static bool IsNewTabBaselineUnknown(int tabCountBefore) => tabCountBefore < 0;

        public static bool IsNavigationConfirmed(string expectedUrl, ComputerUseBrowserState? current, out string reason)
        {
            reason = "";
            if (current == null || !current.IsAvailable)
            {
                reason = "Browser state is unavailable.";
                return false;
            }

            if (current.IsErrorPage)
            {
                reason = "The browser reports an error page.";
                return false;
            }

            // Only the LOADED document is compared below, never the address bar's text, so the
            // caret's location is irrelevant: a browser commonly keeps focus in the address bar
            // after navigating from it, and treating that as "still being edited" reports a page
            // that is plainly open as not yet reached.
            if (string.IsNullOrWhiteSpace(current.DocumentAddress))
            {
                reason = "No loaded document URL is available yet.";
                return false;
            }

            if (!Uri.TryCreate(expectedUrl, UriKind.Absolute, out Uri? expected)
                || !Uri.TryCreate(current.DocumentAddress, UriKind.Absolute, out Uri? actual))
            {
                reason = "The expected or visible address is invalid.";
                return false;
            }

            if (!AreEquivalentUrls(expected.AbsoluteUri, actual.AbsoluteUri))
            {
                reason = $"Loaded document is {current.DocumentAddress}, not {expectedUrl}.";
                return false;
            }

            return true;
        }

        public static bool AreEquivalentUrls(string? expectedUrl, string? actualUrl)
        {
            if (!Uri.TryCreate(expectedUrl, UriKind.Absolute, out Uri? expected)
                || !Uri.TryCreate(actualUrl, UriKind.Absolute, out Uri? actual))
            {
                return false;
            }

            return string.Equals(expected.Scheme, actual.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(expected.Host, actual.Host, StringComparison.OrdinalIgnoreCase)
                && expected.Port == actual.Port
                && string.Equals(expected.AbsolutePath.TrimEnd('/'), actual.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                && string.Equals(expected.Query, actual.Query, StringComparison.Ordinal);
        }
    }
}
