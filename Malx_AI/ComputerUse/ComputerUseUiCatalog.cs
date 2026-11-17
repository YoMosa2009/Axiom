using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;

namespace Malx_AI.ComputerUse
{
    internal static class ComputerUseUiCatalog
    {
        private const int MaxTargets = 36;

        public static IReadOnlyList<ComputerUseUiTarget> Capture(IntPtr windowHandle, int screenX, int screenY, int screenWidth, int screenHeight)
        {
            if (windowHandle == IntPtr.Zero)
                return Array.Empty<ComputerUseUiTarget>();

            try
            {
                AutomationElement root = AutomationElement.FromHandle(windowHandle);
                AutomationElementCollection elements = root.FindAll(
                    TreeScope.Descendants,
                    System.Windows.Automation.Condition.TrueCondition);
                // Browser/window chrome is often enumerated after document content.  Keep a
                // bounded candidate pool and prioritize global controls (for example New tab)
                // before applying the prompt-size limit so recovery actions do not degrade into
                // coordinate guesses merely because a page has many links.
                var targets = new List<ComputerUseUiTarget>(Math.Min(MaxTargets * 8, elements.Count));
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (AutomationElement element in elements.Cast<AutomationElement>())
                {
                    var info = element.Current;
                    if (info.IsOffscreen || !info.IsEnabled || !IsClickableRole(info.ControlType))
                        continue;

                    string name = (info.Name ?? string.Empty).Trim();
                    Rect bounds = info.BoundingRectangle;
                    if (string.IsNullOrWhiteSpace(name) || bounds.Width < 8 || bounds.Height < 8 || !Intersects(bounds, screenX, screenY, screenWidth, screenHeight))
                        continue;

                    int centerX = Math.Clamp((int)Math.Round(bounds.Left + bounds.Width / 2d) - screenX, 0, Math.Max(0, screenWidth - 1));
                    int centerY = Math.Clamp((int)Math.Round(bounds.Top + bounds.Height / 2d) - screenY, 0, Math.Max(0, screenHeight - 1));
                    string role = info.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty, StringComparison.Ordinal);
                    string value = string.Empty;
                    if (!info.IsPassword && element.TryGetCurrentPattern(ValuePattern.Pattern, out object valueObject)
                        && valueObject is ValuePattern valuePattern)
                        value = Bound(valuePattern.Current.Value ?? "", 2048);
                    string signature = $"{role}|{name}|{centerX}|{centerY}";
                    if (!seen.Add(signature))
                        continue;

                    string state = ReadSelectionState(element);

                    targets.Add(new ComputerUseUiTarget
                    {
                        Id = ComputerUseTargetId.Create(targets.Count),
                        Name = name.Length <= 96 ? name : name[..96],
                        Role = role,
                        Value = value,
                        State = state,
                        ImageX = centerX,
                        ImageY = centerY,
                        ImageWidth = (int)Math.Round(bounds.Width),
                        ImageHeight = (int)Math.Round(bounds.Height)
                    });
                }

                return targets
                    .OrderBy(GetTargetPriority)
                    .ThenBy(target => target.ImageY)
                    .ThenBy(target => target.ImageX)
                    .Take(MaxTargets)
                    .Select((target, index) => new ComputerUseUiTarget
                    {
                        Id = ComputerUseTargetId.Create(index),
                        Name = target.Name,
                        Role = target.Role,
                        Value = target.Value,
                        State = target.State,
                        ImageX = target.ImageX,
                        ImageY = target.ImageY,
                        ImageWidth = target.ImageWidth,
                        ImageHeight = target.ImageHeight
                    })
                    .ToList();
            }
            catch
            {
                // UI Automation is optional. A screenshot-only turn must remain usable when an
                // app does not expose an accessibility tree or has a transient provider failure.
                return Array.Empty<ComputerUseUiTarget>();
            }
        }

        /// <summary>
        /// Named container elements of the window, in screen pixels relative to the monitor.
        /// The caller keeps the large ones; see <see cref="ComputerUseImageGeometry.SelectRegions"/>.
        /// </summary>
        public static IReadOnlyList<ComputerUseUiRegion> CaptureRegions(IntPtr windowHandle, int screenX, int screenY, int screenWidth, int screenHeight)
        {
            if (windowHandle == IntPtr.Zero)
                return Array.Empty<ComputerUseUiRegion>();

            try
            {
                AutomationElement root = AutomationElement.FromHandle(windowHandle);
                var containerRoles = new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Image));
                var regions = new List<ComputerUseUiRegion>();
                Rect windowBounds = root.Current.BoundingRectangle;
                if (double.IsInfinity(windowBounds.Width) || double.IsInfinity(windowBounds.Height))
                    windowBounds = Rect.Empty;
                foreach (AutomationElement element in root.FindAll(TreeScope.Descendants, containerRoles).Cast<AutomationElement>())
                {
                    var info = element.Current;
                    string name = (info.Name ?? string.Empty).Trim();
                    Rect bounds = info.BoundingRectangle;
                    if (info.IsOffscreen || name.Length == 0 || bounds.IsEmpty
                        || double.IsInfinity(bounds.Width) || double.IsInfinity(bounds.Height)
                        || !Intersects(bounds, screenX, screenY, screenWidth, screenHeight))
                        continue;

                    var region = new ComputerUseUiRegion
                    {
                        Name = name,
                        Role = info.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty, StringComparison.Ordinal),
                        Left = Math.Clamp((int)Math.Round(bounds.Left) - screenX, 0, Math.Max(0, screenWidth - 1)),
                        Top = Math.Clamp((int)Math.Round(bounds.Top) - screenY, 0, Math.Max(0, screenHeight - 1)),
                        Right = Math.Clamp((int)Math.Round(bounds.Right) - screenX, 0, Math.Max(0, screenWidth - 1)),
                        Bottom = Math.Clamp((int)Math.Round(bounds.Bottom) - screenY, 0, Math.Max(0, screenHeight - 1))
                    };
                    // A scrollable surface reports its full extent, past the window's edges.
                    if (!windowBounds.IsEmpty)
                    {
                        region = ComputerUseImageGeometry.ClipToWindow(region,
                            (int)Math.Round(windowBounds.Left) - screenX,
                            (int)Math.Round(windowBounds.Top) - screenY,
                            (int)Math.Round(windowBounds.Right) - screenX,
                            (int)Math.Round(windowBounds.Bottom) - screenY);
                        if (region == null)
                            continue;
                    }

                    regions.Add(region);
                    if (regions.Count >= 64)
                        break;
                }

                return regions;
            }
            catch
            {
                return Array.Empty<ComputerUseUiRegion>();
            }
        }

        /// <summary>
        /// Whether a control reports itself selected or toggled on. Read from accessibility, so it
        /// holds even when the highlight is too faint to see in a screenshot.
        /// </summary>
        private static string ReadSelectionState(AutomationElement element)
        {
            try
            {
                if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object selection)
                    && selection is SelectionItemPattern selectionItem)
                    return selectionItem.Current.IsSelected ? "selected" : "";
                if (element.TryGetCurrentPattern(TogglePattern.Pattern, out object toggle)
                    && toggle is TogglePattern togglePattern)
                    return togglePattern.Current.ToggleState == ToggleState.On ? "on" : "off";
            }
            catch
            {
                // State is a bonus; a control that will not report it is still a usable target.
            }

            return "";
        }

        public static bool TryResolve(ComputerUseCapture capture, string? targetId, out int imageX, out int imageY)
        {
            imageX = 0;
            imageY = 0;
            if (capture == null || string.IsNullOrWhiteSpace(targetId))
                return false;

            ComputerUseUiTarget? target = capture.UiTargets.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, targetId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (target == null)
                return false;

            imageX = target.ImageX;
            imageY = target.ImageY;
            return true;
        }

        public static ComputerUseBrowserState? CaptureBrowserState(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
                return null;

            try
            {
                AutomationElement root = AutomationElement.FromHandle(windowHandle);
                string title = (root.Current.Name ?? string.Empty).Trim();
                AutomationElementCollection elements = root.FindAll(
                    TreeScope.Descendants,
                    System.Windows.Automation.Condition.TrueCondition);
                string address = string.Empty;
                string documentAddress = string.Empty;
                bool addressHasFocus = false;
                bool documentError = false;
                var tabs = new List<string>();
                var tabIds = new List<string>();
                string activeTabId = string.Empty;
                bool sawTab = false;
                foreach (AutomationElement element in elements.Cast<AutomationElement>())
                {
                    var info = element.Current;

                    if (info.ControlType == ControlType.TabItem)
                    {
                        sawTab = true;
                        string tab = (info.Name ?? string.Empty).Trim();
                        // Tab titles are not unique (two documents can have the same title),
                        // so retain one item per UIA tab rather than de-duplicating by name.
                        tabs.Add(string.IsNullOrWhiteSpace(tab) ? "(unnamed tab)" : Bound(tab, 160));
                        string tabId = string.Join(".", element.GetRuntimeId());
                        tabIds.Add(tabId);
                        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object selection)
                            && selection is SelectionItemPattern selected && selected.Current.IsSelected)
                            activeTabId = tabId;
                        continue;
                    }

                    if (info.IsOffscreen)
                        continue;

                    if (info.ControlType == ControlType.Document)
                    {
                        documentError |= ContainsErrorMarker(info.Name);
                        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object documentPattern)
                            && documentPattern is ValuePattern documentValue
                            && ComputerUseBrowserVerification.TryGetAbsoluteHttpUrl(documentValue.Current.Value, out string loadedUrl))
                            documentAddress = loadedUrl;
                    }
                    // Check visible error headings, not unrelated background tab titles.
                    if (info.ControlType == ControlType.Text)
                        documentError |= ContainsErrorMarker(info.Name);

                    if (info.ControlType != ControlType.Edit || !LooksLikeAddressBar(info.Name))
                        continue;

                    if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object pattern)
                        && pattern is ValuePattern valuePattern)
                    {
                        address = (valuePattern.Current.Value ?? string.Empty).Trim();
                        addressHasFocus = info.HasKeyboardFocus;
                    }
                }

                bool looksLikeBrowser = !string.IsNullOrWhiteSpace(address) || sawTab;
                if (!looksLikeBrowser)
                    return null;

                bool error = ContainsErrorMarker(title) || documentError;
                return new ComputerUseBrowserState
                {
                    WindowTitle = Bound(title, 240),
                    Address = Bound(address, 2048),
                    DocumentAddress = Bound(documentAddress, 2048),
                    AddressHasFocus = addressHasFocus,
                    ActiveTabId = activeTabId,
                    TabIds = tabIds,
                    TabTitles = tabs.Take(32).ToList(),
                    HasTabTelemetry = sawTab,
                    IsErrorPage = error
                };
            }
            catch
            {
                return null;
            }
        }

        private static int GetTargetPriority(ComputerUseUiTarget target)
        {
            string name = target.Name.Trim();
            if (string.Equals(name, "New tab", StringComparison.OrdinalIgnoreCase))
                return 0;
            if (name.Contains("address", StringComparison.OrdinalIgnoreCase)
                || name.Contains("search bar", StringComparison.OrdinalIgnoreCase))
                return 1;
            // A control that reports a selected/on state is a mode switch (a tool, a tab, an
            // option), and knowing which one is active matters more than any plain button. Paint's
            // "Brushes" is a radio button and was being cut from the list by the size limit.
            if (target.State.Length > 0
                || string.Equals(target.Role, "Button", StringComparison.OrdinalIgnoreCase)
                || string.Equals(target.Role, "RadioButton", StringComparison.OrdinalIgnoreCase))
                return 2;
            if (string.Equals(target.Role, "TabItem", StringComparison.OrdinalIgnoreCase))
                return 3;
            return 4;
        }

        private static bool IsClickableRole(ControlType type)
            => type == ControlType.Button
                || type == ControlType.Hyperlink
                || type == ControlType.MenuItem
                || type == ControlType.TabItem
                || type == ControlType.ListItem
                || type == ControlType.TreeItem
                || type == ControlType.ComboBox
                || type == ControlType.Edit
                || type == ControlType.CheckBox
                || type == ControlType.RadioButton;

        private static bool Intersects(Rect bounds, int x, int y, int width, int height)
            => bounds.Right > x && bounds.Bottom > y && bounds.Left < x + width && bounds.Top < y + height;

        private static bool LooksLikeAddressBar(string? name)
            => (name ?? string.Empty).Contains("address", StringComparison.OrdinalIgnoreCase)
                || (name ?? string.Empty).Contains("search bar", StringComparison.OrdinalIgnoreCase);

        private static bool ContainsErrorMarker(string? value)
        {
            string text = value ?? string.Empty;
            return text.Contains("page not found", StringComparison.OrdinalIgnoreCase)
                || text.Equals("404", StringComparison.OrdinalIgnoreCase)
                || text.Contains("404 -", StringComparison.OrdinalIgnoreCase)
                || text.Contains("This is not the web page you are looking for", StringComparison.OrdinalIgnoreCase)
                || text.Contains("site can't be reached", StringComparison.OrdinalIgnoreCase)
                || text.Contains("this site can’t be reached", StringComparison.OrdinalIgnoreCase);
        }

        private static string Bound(string value, int max)
            => value.Length <= max ? value : value[..max];
    }
}
