using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Malx_AI
{
    internal static class WebView2OfflinePolicy
    {
        private const string NetworkGuardScript = """
            (() => {
                const offlineError = () => new TypeError('Network access is disabled in Axiom artifact views.');
                const isLocalUrl = value => {
                    try {
                        const url = new URL(String(value), document.baseURI);
                        return url.protocol === 'data:' || url.protocol === 'blob:' || url.protocol === 'about:';
                    } catch {
                        return false;
                    }
                };

                const nativeFetch = window.fetch.bind(window);
                window.fetch = (input, init) => {
                    const value = input && typeof input === 'object' && 'url' in input ? input.url : input;
                    return isLocalUrl(value) ? nativeFetch(input, init) : Promise.reject(offlineError());
                };

                const nativeOpen = XMLHttpRequest.prototype.open;
                XMLHttpRequest.prototype.open = function(method, url, ...rest) {
                    if (!isLocalUrl(url))
                        throw offlineError();
                    return nativeOpen.call(this, method, url, ...rest);
                };

                const blockedConstructor = class {
                    constructor() { throw offlineError(); }
                };
                Object.defineProperty(window, 'WebSocket', { value: blockedConstructor, configurable: false });
                Object.defineProperty(window, 'EventSource', { value: blockedConstructor, configurable: false });
                Object.defineProperty(window, 'RTCPeerConnection', { value: blockedConstructor, configurable: false });
                if ('webkitRTCPeerConnection' in window)
                    Object.defineProperty(window, 'webkitRTCPeerConnection', { value: blockedConstructor, configurable: false });
                if (navigator.sendBeacon)
                    navigator.sendBeacon = () => false;
            })();
            """;

        public static async Task ConfigureAsync(CoreWebView2 coreWebView, params string[] allowedVirtualHosts)
        {
            CoreWebView2Settings settings = coreWebView.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.AreHostObjectsAllowed = false;
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.IsPinchZoomEnabled = false;
            settings.IsSwipeNavigationEnabled = false;
            settings.IsBuiltInErrorPageEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;

            var allowedHosts = new HashSet<string>(
                allowedVirtualHosts.Where(host => !string.IsNullOrWhiteSpace(host)),
                StringComparer.OrdinalIgnoreCase);

            coreWebView.NavigationStarting += (_, args) =>
            {
                if (!OfflineUriPolicy.IsAllowedTopLevelNavigation(args.Uri, allowedHosts))
                    args.Cancel = true;
            };
            coreWebView.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
            };
            coreWebView.DownloadStarting += (_, args) =>
            {
                args.Cancel = true;
                args.Handled = true;
            };
            coreWebView.PermissionRequested += (_, args) =>
            {
                args.State = CoreWebView2PermissionState.Deny;
                args.Handled = true;
            };

            coreWebView.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            coreWebView.WebResourceRequested += (_, args) =>
            {
                if (OfflineUriPolicy.IsAllowedResource(args.Request.Uri, allowedHosts))
                    return;

                args.Response = coreWebView.Environment.CreateWebResourceResponse(
                    Stream.Null,
                    403,
                    "Blocked by Axiom offline policy",
                    "Content-Type: text/plain\r\nCache-Control: no-store");
            };

            await coreWebView.AddScriptToExecuteOnDocumentCreatedAsync(NetworkGuardScript);
        }
    }
}
