using System;
using System.Collections.Generic;

namespace Malx_AI
{
    internal static class OfflineUriPolicy
    {
        public static bool IsAllowedTopLevelNavigation(string uriText, IReadOnlySet<string> allowedVirtualHosts)
        {
            if (!Uri.TryCreate(uriText, UriKind.Absolute, out Uri? uri))
                return false;

            if (uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return IsAllowedVirtualHost(uri, allowedVirtualHosts);
        }

        public static bool IsAllowedResource(string uriText, IReadOnlySet<string> allowedVirtualHosts)
        {
            if (!Uri.TryCreate(uriText, UriKind.Absolute, out Uri? uri))
                return false;

            if (uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("blob", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return IsAllowedVirtualHost(uri, allowedVirtualHosts);
        }

        private static bool IsAllowedVirtualHost(Uri uri, IReadOnlySet<string> allowedVirtualHosts)
        {
            bool isHttp = uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            return isHttp && allowedVirtualHosts.Contains(uri.Host);
        }
    }
}
