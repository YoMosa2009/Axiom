using System.Text;
using Malx_AI.ComputerUse;
using Drawing = System.Drawing;

namespace AxSim;

/// <summary>
/// A stand-in Microsoft Edge: tabs, an address bar with focus and a draft, and a handful of pages
/// (new tab, Wikipedia home and articles, Bing results, any other site). It reports itself the
/// way the real capture does: UI targets, and a browser state with tabs, address and document URL.
/// </summary>
internal sealed class SimBrowser
{
    private sealed class Tab
    {
        public string Url = "edge://newtab/";
        public string SearchDraft = "";
        public List<string> History = new();
    }

    private readonly int _w, _h;
    private readonly double _s;
    private readonly List<Tab> _tabs = new() { new Tab() };
    private int _active;
    private int _tabIdSeed = 100;
    private readonly List<int> _tabIds = new() { 100 };
    private string _focus = "address"; // "address", "page-search" or ""
    private string? _addressDraft;

    public SimBrowser(int screenWidth, int screenHeight)
    {
        _w = screenWidth;
        _h = screenHeight;
        _s = screenHeight / 1000.0;
    }

    private Tab Current => _tabs[_active];
    private int S(double v) => (int)Math.Round(v * _s);
    public string Url => Current.Url;
    public bool AddressHasFocus => _focus == "address";

    // ── Page model

    private static Uri? Parse(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri : null;

    public string PageTitle
    {
        get
        {
            Uri? uri = Parse(Current.Url);
            if (uri == null || uri.Scheme == "edge") return "New tab";
            string host = uri.Host.ToLowerInvariant();
            if (host is "www.wikipedia.org" or "wikipedia.org") return "Wikipedia";
            if (host.EndsWith("wikipedia.org") && uri.AbsolutePath.StartsWith("/wiki/"))
                return Uri.UnescapeDataString(uri.AbsolutePath[6..]).Replace('_', ' ') + " - Wikipedia";
            if (host.EndsWith("bing.com") && uri.AbsolutePath.StartsWith("/search"))
                return Query(uri) + " - Search";
            string name = host.StartsWith("www.") ? host[4..] : host;
            return char.ToUpperInvariant(name[0]) + name[1..] + " - Home";
        }
    }

    public string WindowTitle => PageTitle + " - Microsoft Edge";

    private static string Query(Uri uri)
    {
        foreach (string part in uri.Query.TrimStart('?').Split('&'))
            if (part.StartsWith("q=")) return Uri.UnescapeDataString(part[2..].Replace('+', ' '));
        return "";
    }

    private void Navigate(string url)
    {
        Current.History.Add(Current.Url);
        Current.Url = url;
        Current.SearchDraft = "";
        _addressDraft = null;
        _focus = "";
    }

    private void CommitAddress(string text)
    {
        text = text.Trim();
        if (ComputerUseSessionController.TryLookLikeNavigationAddress(text))
        {
            string url = text.Contains("://") ? text : "https://" + text;
            Navigate(Parse(url)?.ToString() ?? url);
        }
        else
        {
            Navigate("https://www.bing.com/search?q=" + Uri.EscapeDataString(text));
        }
    }

    private void SearchWikipedia(string query)
    {
        string article = string.Join('_', query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
        Navigate("https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(article).Replace("%2F", "/"));
    }

    private enum PageKind { NewTab, WikiHome, WikiArticle, Bing, Other }

    private PageKind Kind
    {
        get
        {
            Uri? uri = Parse(Current.Url);
            if (uri == null || uri.Scheme == "edge") return PageKind.NewTab;
            string host = uri.Host.ToLowerInvariant();
            if (host is "www.wikipedia.org" or "wikipedia.org") return PageKind.WikiHome;
            if (host.EndsWith("wikipedia.org") && uri.AbsolutePath.StartsWith("/wiki/")) return PageKind.WikiArticle;
            if (host.EndsWith("bing.com") && uri.AbsolutePath.StartsWith("/search")) return PageKind.Bing;
            return PageKind.Other;
        }
    }

    private IEnumerable<(string Title, string Url)> BingResults()
    {
        string q = Query(Parse(Current.Url)!);
        string article = string.Join('_', q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        yield return ($"{q} - Wikipedia", "https://en.wikipedia.org/wiki/" + article);
        yield return ($"{q} | Britannica", "https://www.britannica.com/search?query=" + Uri.EscapeDataString(q));
        yield return ($"Images of {q}", "https://www.bing.com/images/search?q=" + Uri.EscapeDataString(q));
    }

    // ── Layout: every control as (id-less) target in screen pixels

    private sealed record Control(string Name, string Role, Drawing.Rectangle Box, string State = "", string Value = "", string Action = "");

    private List<Control> Controls()
    {
        var list = new List<Control>();
        int tabW = S(240), tabH = S(36);
        for (int i = 0; i < _tabs.Count; i++)
        {
            string title = TitleOf(_tabs[i]);
            list.Add(new Control(title, "TabItem", new Drawing.Rectangle(S(10) + i * tabW, S(4), tabW - S(8), tabH), i == _active ? "selected" : "", "", "tab:" + i));
        }
        list.Add(new Control("New Tab", "Button", new Drawing.Rectangle(S(10) + _tabs.Count * tabW, S(8), S(28), S(28)), "", "", "newtab"));
        list.Add(new Control("Back", "Button", new Drawing.Rectangle(S(14), S(48), S(30), S(30)), "", "", "back"));
        list.Add(new Control("Refresh", "Button", new Drawing.Rectangle(S(84), S(48), S(30), S(30)), "", "", "refresh"));
        list.Add(new Control("Address and search bar", "Edit", AddressBox, "", AddressText, "address"));

        int top = S(100);
        switch (Kind)
        {
            case PageKind.NewTab:
                list.Add(new Control("Search the web", "Edit", new Drawing.Rectangle(_w / 2 - S(300), top + S(220), S(600), S(46)), "", "", "address"));
                break;
            case PageKind.WikiHome:
                list.Add(new Control("Search Wikipedia", "ComboBox", new Drawing.Rectangle(_w / 2 - S(260), top + S(300), S(440), S(44)), "", Current.SearchDraft, "wikisearch"));
                list.Add(new Control("Search", "Button", new Drawing.Rectangle(_w / 2 + S(190), top + S(300), S(70), S(44)), "", "", "wikigo"));
                list.Add(new Control("English 6,900,000+ articles", "Hyperlink", new Drawing.Rectangle(_w / 2 - S(360), top + S(160), S(260), S(40)), "", "", "link:https://en.wikipedia.org/wiki/Main_Page"));
                break;
            case PageKind.WikiArticle:
                list.Add(new Control("Search Wikipedia", "Edit", new Drawing.Rectangle(_w - S(520), top + S(14), S(380), S(36)), "", Current.SearchDraft, "wikisearch"));
                list.Add(new Control("Search", "Button", new Drawing.Rectangle(_w - S(130), top + S(14), S(80), S(36)), "", "", "wikigo"));
                break;
            case PageKind.Bing:
                int y = top + S(130);
                foreach (var result in BingResults())
                {
                    list.Add(new Control(result.Title, "Hyperlink", new Drawing.Rectangle(S(180), y, S(620), S(34)), "", "", "link:" + result.Url));
                    y += S(110);
                }
                break;
        }

        return list;
    }

    private Drawing.Rectangle AddressBox => new(S(140), S(46), _w - S(340), S(34));

    private string AddressText => _addressDraft ?? (Kind == PageKind.NewTab ? "" : Display(Current.Url));

    private static string Display(string url) => url.StartsWith("https://") ? url[8..].TrimEnd('/') : url;

    private string TitleOf(Tab tab)
    {
        int index = _tabs.IndexOf(tab);
        int saved = _active;
        _active = index;
        string title = PageTitle;
        _active = saved;
        return title;
    }

    public List<ComputerUseUiTarget> Targets()
        => Controls().Select((control, i) => new ComputerUseUiTarget
        {
            Id = "ui" + (i + 1),
            Name = control.Name,
            Role = control.Role,
            State = control.State,
            Value = control.Value,
            ImageX = control.Box.Left + control.Box.Width / 2,
            ImageY = control.Box.Top + control.Box.Height / 2,
            ImageWidth = control.Box.Width,
            ImageHeight = control.Box.Height
        }).ToList();

    public ComputerUseBrowserState State() => new()
    {
        WindowTitle = WindowTitle,
        Address = AddressText,
        DocumentAddress = Kind == PageKind.NewTab ? "" : Current.Url,
        AddressHasFocus = AddressHasFocus,
        TabTitles = _tabs.Select(TitleOf).ToList(),
        TabIds = _tabIds.Select(id => id.ToString()).ToList(),
        ActiveTabId = _tabIds[_active].ToString(),
        HasTabTelemetry = true,
        IsErrorPage = false
    };

    // ── Input

    public string Click(int x, int y)
    {
        Control? hit = Controls().LastOrDefault(control => control.Box.Contains(x, y));
        _addressDraft = hit?.Action == "address" ? _addressDraft : null;
        switch (hit?.Action ?? "")
        {
            case "address": _focus = "address"; break;
            case "wikisearch": _focus = "page-search"; break;
            case "wikigo":
                if (Current.SearchDraft.Length > 0) SearchWikipedia(Current.SearchDraft);
                break;
            case "newtab": NewTab(); break;
            case "back": Back(); break;
            case "refresh": _focus = ""; break;
            case var a when a.StartsWith("tab:"): _active = int.Parse(a[4..]); _focus = ""; break;
            case var a when a.StartsWith("link:"): Navigate(a[5..]); break;
            default: _focus = ""; break;
        }

        return hit?.Name ?? "";
    }

    private void NewTab()
    {
        _tabs.Add(new Tab());
        _tabIds.Add(++_tabIdSeed);
        _active = _tabs.Count - 1;
        _focus = "address";
        _addressDraft = null;
    }

    private void Back()
    {
        if (Current.History.Count == 0) return;
        Current.Url = Current.History[^1];
        Current.History.RemoveAt(Current.History.Count - 1);
        _focus = "";
        _addressDraft = null;
    }

    /// <summary>Types text. Returns false when no text field has focus, so nothing changes.</summary>
    public bool Type(string text, bool replaceAndCommitAddress)
    {
        if (_focus == "address")
        {
            _addressDraft = replaceAndCommitAddress || _addressDraft == null ? text : _addressDraft + text;
            if (replaceAndCommitAddress && ComputerUseSessionController.TryLookLikeNavigationAddress(text))
                CommitAddress(_addressDraft);
            return true;
        }

        if (_focus == "page-search")
        {
            Current.SearchDraft += text;
            return true;
        }

        return false;
    }

    public bool Key(string keys)
    {
        string k = keys.Replace(" ", "").ToUpperInvariant();
        switch (k)
        {
            case "CTRL+T": NewTab(); return true;
            case "CTRL+L" or "ALT+D" or "F6": _focus = "address"; _addressDraft = null; return true;
            case "ALT+LEFT" or "BROWSERBACK": Back(); return true;
            case "CTRL+W":
                if (_tabs.Count > 1) { _tabs.RemoveAt(_active); _tabIds.RemoveAt(_active); _active = Math.Max(0, _active - 1); }
                return true;
            case "ESC" or "ESCAPE": _addressDraft = null; _focus = ""; return true;
            case "ENTER":
                if (_focus == "address" && !string.IsNullOrWhiteSpace(_addressDraft)) { CommitAddress(_addressDraft); return true; }
                if (_focus == "page-search" && Current.SearchDraft.Length > 0) { SearchWikipedia(Current.SearchDraft); return true; }
                return false;
            case "CTRL+A": return true;
            case "BACKSPACE":
                if (_focus == "address" && _addressDraft is { Length: > 0 }) { _addressDraft = _addressDraft[..^1]; return true; }
                if (_focus == "page-search" && Current.SearchDraft.Length > 0) { Current.SearchDraft = Current.SearchDraft[..^1]; return true; }
                return false;
            default: return false;
        }
    }

    // ── Picture

    public void Draw(Drawing.Graphics g)
    {
        using var chrome = new Drawing.SolidBrush(Drawing.Color.FromArgb(32, 32, 36));
        using var bar = new Drawing.SolidBrush(Drawing.Color.FromArgb(48, 48, 54));
        using var white = new Drawing.SolidBrush(Drawing.Color.White);
        using var fieldBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(62, 62, 70));
        using var focusPen = new Drawing.Pen(Drawing.Color.FromArgb(80, 160, 255), S(2));
        using var font = new Drawing.Font("Segoe UI", S(13), Drawing.GraphicsUnit.Pixel);
        using var big = new Drawing.Font("Segoe UI", S(30), Drawing.GraphicsUnit.Pixel);
        using var huge = new Drawing.Font("Georgia", S(44), Drawing.GraphicsUnit.Pixel);
        using var body = new Drawing.Font("Segoe UI", S(15), Drawing.GraphicsUnit.Pixel);
        using var link = new Drawing.Font("Segoe UI", S(19), Drawing.FontStyle.Underline, Drawing.GraphicsUnit.Pixel);
        using var linkBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(26, 13, 171));
        using var grey = new Drawing.SolidBrush(Drawing.Color.FromArgb(90, 90, 90));

        g.FillRectangle(chrome, 0, 0, _w, S(90));
        g.FillRectangle(bar, 0, S(42), _w, S(48));
        foreach (Control c in Controls())
        {
            switch (c.Role)
            {
                case "TabItem":
                    using (var tabBrush = new Drawing.SolidBrush(c.State == "selected" ? Drawing.Color.FromArgb(48, 48, 54) : Drawing.Color.FromArgb(38, 38, 42)))
                        g.FillRectangle(tabBrush, c.Box);
                    g.DrawString(Clip(c.Name, 26), font, Drawing.Brushes.Gainsboro, c.Box.Left + S(10), c.Box.Top + S(9));
                    break;
                case "Button" when c.Box.Top < S(90):
                    g.DrawString(c.Action switch { "newtab" => "+", "back" => "←", _ => "⟳" }, big, Drawing.Brushes.Gainsboro, c.Box.Left, c.Box.Top - S(8));
                    break;
            }
        }

        Drawing.Rectangle address = AddressBox;
        g.FillRectangle(fieldBrush, address);
        if (_focus == "address") g.DrawRectangle(focusPen, address);
        string shown = AddressText;
        g.DrawString(shown.Length == 0 ? "Search or enter web address" : shown + (_focus == "address" ? "|" : ""), body,
            shown.Length == 0 ? Drawing.Brushes.Gray : Drawing.Brushes.White, address.Left + S(12), address.Top + S(6));

        int top = S(90);
        var page = new Drawing.Rectangle(0, top, _w, _h - top - S(48));
        switch (Kind)
        {
            case PageKind.NewTab:
            {
                using var bg = new Drawing.SolidBrush(Drawing.Color.FromArgb(28, 52, 86));
                g.FillRectangle(bg, page);
                Control field = Controls().First(c => c.Name == "Search the web");
                g.FillRectangle(white, field.Box);
                g.DrawString("Search the web", body, Drawing.Brushes.Gray, field.Box.Left + S(14), field.Box.Top + S(12));
                g.DrawString("New tab", big, Drawing.Brushes.White, _w / 2 - S(60), top + S(140));
                break;
            }
            case PageKind.WikiHome:
            {
                g.FillRectangle(white, page);
                g.DrawString("WIKIPEDIA", huge, Drawing.Brushes.Black, _w / 2 - S(150), top + S(60));
                g.DrawString("The Free Encyclopedia", body, grey, _w / 2 - S(80), top + S(125));
                foreach (Control c in Controls().Where(c => c.Box.Top > top))
                {
                    if (c.Role == "Hyperlink") g.DrawString(c.Name, link, linkBrush, c.Box.Left, c.Box.Top);
                    else if (c.Role == "Button") { g.FillRectangle(Drawing.Brushes.SteelBlue, c.Box); g.DrawString("Search", body, white, c.Box.Left + S(8), c.Box.Top + S(12)); }
                    else
                    {
                        g.DrawRectangle(Drawing.Pens.Gray, c.Box);
                        if (_focus == "page-search") g.DrawRectangle(focusPen, c.Box);
                        g.DrawString(Current.SearchDraft.Length > 0 ? Current.SearchDraft : "Search Wikipedia", body,
                            Current.SearchDraft.Length > 0 ? Drawing.Brushes.Black : Drawing.Brushes.Gray, c.Box.Left + S(10), c.Box.Top + S(11));
                    }
                }
                break;
            }
            case PageKind.WikiArticle:
            {
                g.FillRectangle(white, page);
                string heading = PageTitle.Replace(" - Wikipedia", "");
                g.DrawString("WIKIPEDIA", big, Drawing.Brushes.Black, S(40), top + S(14));
                foreach (Control c in Controls().Where(c => c.Box.Top > top))
                {
                    if (c.Role == "Button") { g.FillRectangle(Drawing.Brushes.SteelBlue, c.Box); g.DrawString("Search", body, white, c.Box.Left + S(10), c.Box.Top + S(8)); }
                    else { g.DrawRectangle(Drawing.Pens.Gray, c.Box); if (_focus == "page-search") g.DrawRectangle(focusPen, c.Box);
                        g.DrawString(Current.SearchDraft.Length > 0 ? Current.SearchDraft : "Search Wikipedia", body, Drawing.Brushes.Gray, c.Box.Left + S(8), c.Box.Top + S(7)); }
                }
                g.DrawString(heading, huge, Drawing.Brushes.Black, S(240), top + S(90));
                g.DrawLine(Drawing.Pens.Silver, S(240), top + S(150), _w - S(80), top + S(150));
                g.DrawString("From Wikipedia, the free encyclopedia", body, grey, S(240), top + S(165));
                var text = new StringBuilder();
                text.AppendLine($"{heading} is the subject of this article. The lead paragraph summarises who or what it is,");
                text.AppendLine("followed by sections on history, work and legacy, with an infobox on the right.");
                g.DrawString(text.ToString(), body, Drawing.Brushes.Black, S(240), top + S(210));
                g.DrawRectangle(Drawing.Pens.Silver, _w - S(520), top + S(200), S(420), S(520));
                g.DrawString(heading, link, Drawing.Brushes.Black, _w - S(500), top + S(215));
                break;
            }
            case PageKind.Bing:
            {
                g.FillRectangle(white, page);
                g.DrawString("Bing", big, Drawing.Brushes.Teal, S(40), top + S(30));
                g.DrawRectangle(Drawing.Pens.Gray, S(180), top + S(30), S(700), S(44));
                g.DrawString(Query(Parse(Current.Url)!), body, Drawing.Brushes.Black, S(194), top + S(42));
                foreach (Control c in Controls().Where(c => c.Role == "Hyperlink"))
                {
                    g.DrawString(c.Name, link, linkBrush, c.Box.Left, c.Box.Top);
                    g.DrawString(c.Action[5..], body, Drawing.Brushes.DarkGreen, c.Box.Left, c.Box.Top + S(36));
                }
                break;
            }
            default:
            {
                g.FillRectangle(white, page);
                g.DrawString(PageTitle.Replace(" - Home", ""), huge, Drawing.Brushes.Black, S(120), top + S(80));
                g.DrawString("Welcome. This is the home page of " + Parse(Current.Url)!.Host + ".", body, Drawing.Brushes.Black, S(120), top + S(160));
                break;
            }
        }
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
