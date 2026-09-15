using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using YtDlpCore;

namespace Pixie.TrackTracer.Detection;

/// <summary>What a line of description/comment text looks like, once its marks have been read.</summary>
internal enum LineKind
{
    Blank,
    /// <summary>Only symbols (a run of dashes, box-drawing art, a row of emoji): a visual separator.</summary>
    Divider,
    /// <summary>Hashtags only: carries nothing.</summary>
    Noise,
    /// <summary>"Tracklist:", "Timestamps", "Треклист" — announces a list (or says where it is).</summary>
    Header,
    /// <summary>"Follow me:", "Support the artists" — announces that the next lines are NOT the tracklist.</summary>
    ForeignHeader,
    /// <summary><c>00:00 Artist - Title</c>, optionally numbered, bracketed or with an end time.</summary>
    Timestamped,
    /// <summary><c>Artist - Title 3:45</c>: the time at the end of the line.</summary>
    TrailingTime,
    /// <summary><c>01. Artist - Title</c></summary>
    Numbered,
    /// <summary><c>Artist - Title https://…</c> where the link points at a track.</summary>
    TitleWithLink,
    /// <summary>A line that is only a link to a track.</summary>
    TrackLink,
    /// <summary>A line that is (or labels) a link to something else: a profile, a playlist, a social page.</summary>
    OtherLink,
    Text,
}

/// <summary>What a header line says beyond "a list follows".</summary>
internal enum HeaderHint
{
    None,
    /// <summary>"Tracklist in the comments" — the list lives in a comment, not here.</summary>
    InComments,
    /// <summary>"Tracklist: https://…" — the list lives on another site.</summary>
    External,
    /// <summary>"No tracklist", "tracklist soon", "IDs only" — there is none to find.</summary>
    Missing,
}

/// <summary>Where a URL points, as far as a tracklist is concerned.</summary>
internal enum LinkKind
{
    /// <summary>A single track/release: a YouTube video, a SoundCloud track, a Spotify track/album, a smart link…</summary>
    Track,
    /// <summary>A whole-mix resource (1001tracklists, Mixcloud, setlist.fm): an external tracklist, not a track.</summary>
    TracklistSite,
    /// <summary>A profile, a playlist, a social page, a shop — anything that is not a track.</summary>
    Other,
}

/// <summary>
/// One line of free text and everything <see cref="TracklistLine.Classify"/> could read off it. The
/// parsed fields are filled only when they apply to the <see cref="Kind"/>; <see cref="Raw"/> is the
/// original line and <see cref="Norm"/> the NFKC-normalised, zero-width-free copy every match ran on
/// (so full-width digits, colons and dashes from CJK descriptions look like ASCII to the patterns).
/// </summary>
internal sealed class TracklistLine
{
    public required string Raw { get; init; }
    public required string Norm { get; init; }
    public LineKind Kind { get; set; } = LineKind.Text;

    /// <summary>The time on the line (leading or trailing), when there is one.</summary>
    public TimeSpan? Start { get; set; }
    /// <summary>An explicit end time (<c>00:00 - 04:30 …</c>), when there is one.</summary>
    public TimeSpan? End { get; set; }
    /// <summary>The line without its time/number/bullet/link decorations — what a track entry reads as.</summary>
    public string Text { get; set; } = "";
    public string? Link { get; set; }
    public LinkKind LinkKind { get; set; } = LinkKind.Other;
    /// <summary><c>Artist - Title</c>-style split somewhere in <see cref="Text"/>.</summary>
    public bool HasSeparator { get; set; }
    /// <summary>For <see cref="LineKind.Header"/>: a short, dedicated line ("Tracklist:") vs. a sentence that mentions one.</summary>
    public bool StrongHeader { get; set; }
    public HeaderHint Hint { get; set; }

    // ───────────────────────── Patterns ─────────────────────────
    // Every pattern runs on Norm. Times are h:mm:ss or mm:ss (mm may exceed 59 when there are no hours:
    // "75:30" is how some people write 1:15:30). Group names get a prefix so two times can share a pattern.

    private static string Time(string p) =>
        $@"(?<!\d)(?:(?<{p}h>\d{{1,2}}):)?(?<{p}m>\d{{1,3}}):(?<{p}s>\d{{2}})(?!\d)";

    private const string Sep = @"[-–—―~>→|·•:/\\]";

    /// <summary>
    /// <c>[01. ]  [00:00]  [ - 04:30]  [ - ]  [1. ]  text</c> — a leading time with the decorations seen in
    /// the wild around it: an item number before it ("01. 00:00 …", "1&lt;tab&gt;00:00 …"), brackets, an end time,
    /// a separator, and an item number after it ("00:00 | 1. …").
    /// </summary>
    private static readonly Regex LeadingTime = new(
        @"^(?:(?<num>\d{1,3})(?:[.)\]]\s*|\s+))?" +
        @"[\[(]?" + Time("a") + @"[\])]?" +
        @"(?:\s*(?:" + Sep + @"|to|até|bis)\s*[\[(]?" + Time("b") + @"[\])]?)?" +
        @"\s*(?:" + Sep + @"\s*)*" +
        @"(?:(?<num2>\d{1,3})[.)]\s+)?" +
        @"(?<rest>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary><c>text [ - | : | @ ] (3:45)</c> or <c>text 00:00 - 47:44</c> — a time (or a range) at the end of the line.</summary>
    private static readonly Regex TrailingTimeRx = new(
        @"^(?<rest>.*?\p{L}.*?\S)\s*(?:[-–—―:|@]\s*)?[\[(]?" + Time("a") + @"[\])]?" +
        @"(?:\s*(?:" + Sep + @"|to|até|bis)\s*[\[(]?" + Time("b") + @"[\])]?)?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary><c>01. text</c>, <c>1) text</c>, <c>#1 text</c>, <c>[01] text</c>, <c>1 - text</c>. A bare "1 text" is not a number ("2 Unlimited").</summary>
    private static readonly Regex NumberedRx = new(
        @"^(?:#\s?(?<num>\d{1,3})\s+|\[(?<num>\d{1,3})\]\s*|(?<num>\d{1,3})(?:[.)\]:]|\s+[-–—―])\s*)(?<rest>\S.*)$",
        RegexOptions.CultureInvariant);

    /// <summary>An http(s) URL, or a scheme-less "domain.tld/path" the way YouTube collapses links ("spoti.fi/3dE1kCy").</summary>
    private static readonly Regex UrlRx = new(
        @"https?://[^\s<>""'()\[\]]+|(?<![\w/.@])(?:www\.)?(?:[a-z0-9-]+\.)+[a-z]{2,}/[^\s<>""'()\[\]]*",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Bullets, emoji, arrows and markdown-ish emphasis that people put in front of a line.</summary>
    private static readonly Regex LeadingDecoration = new(
        @"^(?:[\s\p{So}\p{Sm}\p{Sk}\uD800-\uDFFF︎️‍•·▪▫■□◦●○►▶▷→➤➜⇒➡★☆✅✔✓♪♫♬|«»]|[-*>_~]\s+|[-*>_~](?=[-*>_~]))+",
        RegexOptions.CultureInvariant);

    private static readonly Regex TrailingDecoration = new(@"[\s*_~|]+$", RegexOptions.CultureInvariant);

    private static readonly Regex EdgeSeparators = new(@"^(?:" + Sep + @"|\s)+|(?:" + Sep + @"|\s)+$", RegexOptions.CultureInvariant);
    private static readonly Regex EmptyBrackets = new(@"\(\s*\)|\[\s*\]", RegexOptions.CultureInvariant);

    /// <summary>"Instagram: …", "Spotify - …", "Chave pix: …" — a labelled contact/social line, link or not.</summary>
    private static readonly Regex SocialLabelRx = new(
        @"^(?:instagram|insta|ig|facebook|fb|twitter|x|tiktok|spotify|soundcloud|sc|youtube|yt|patreon|discord|telegram|twitch|website|site|web|e-?mail|bookings?|merch|snapchat|threads|bandcamp|apple\s+music|deezer|tidal|mixcloud|beatport|linktree|linktr\.ee|paypal|pix|chave\s+pix|whatsapp|vk|weibo|bilibili|line|kakao|contato|contact|donate|doa[cç][aã]o)\s*[:\-–—→|]",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ZeroWidth = new(@"[​-‍⁠﻿]", RegexOptions.CultureInvariant);

    private static readonly Regex HashtagsOnly = new(@"^(?:#[\p{L}\p{N}_]+[\s,]*)+$", RegexOptions.CultureInvariant);

    /// <summary><c>Artist - Title</c>, <c>Artist – Title</c>, <c>Title · Artist</c>, <c>Title by Artist</c>, <c>Artist "Title"</c>.</summary>
    private static readonly Regex SeparatorRx = new(
        @"\s[-–—―·•|/]\s|\sby\s|\p{L}\s+[""“„]",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// What a bare line has that a sentence rarely does: a "(Remix)"/"[Bootleg]" tag, or music vocabulary
    /// ("feat.", "vs.", "hardstyle", "sped up"…). Lets a list of titles with no dashes still read as one.
    /// </summary>
    private static readonly Regex TitleTagRx = new(
        @"\([^()]{2,}\)|\[[^\[\]]{2,}\]" +
        @"|\b(?:remix|rmx|bootleg|edit|mashup|flip|rework|vip|version|extended|original|radio|club|dub|instrumental|acoustic|cover" +
        @"|feat|ft|vs|prod|hardstyle|frenchcore|hardcore|uptempo|rawstyle|nightcore|hardtekk|tekk|techno|trance|house|dnb|drum|bass|dubstep|phonk|funk|lofi|lo-fi|speed\s?up|sped\s?up|slowed|reverb|8d|ремикс|спид\s?ап)\b\.?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>True when the text carries a remix-style tag or music vocabulary — see <see cref="TitleTagRx"/>.</summary>
    public static bool HasTitleTag(string text) => TitleTagRx.IsMatch(text);

    /// <summary>Words that name a tracklist, in the languages the owner expects to run into.</summary>
    private static readonly Regex StrongHeaderWords = new(
        @"tra(?:ck|k)\s?-?\s?(?:list|lsit|ist|lis|lst)(?:ing|e|a)?s?|set\s?-?\s?list|time\s?-?\s?stamps?|time\s?-?\s?codes?|\bchapters\b|\btl\b|\bin\s+order\b" +
        @"|трек-?\s?лист|тайм-?\s?код|список\s+треков|трэклист|таймлайн" +
        @"|トラックリスト|セットリスト|曲目|歌单|歌單|时间轴|時間軸|時間戳|타임라인|트랙리스트|곡\s*목록|셋리스트" +
        @"|\bfaixas\b|lista\s+de\s+(?:faixas|músicas|musicas|canciones|temas|tracks|reprodu[cç][aã]o)|titelliste|lista\s+utworów",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Words that only count as a header on a short, dedicated line ("Songs:", "Playlist").</summary>
    private static readonly Regex WeakHeaderWords = new(
        @"\b(?:tracks|songs|song\s?list|playlist|music|músicas|musicas|canciones|titres|lista|set|tunes|ids)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Section titles whose lines are never the tracklist: socials, credits, shop, "more videos"…</summary>
    private static readonly Regex ForeignHeaderWords = new(
        @"^(?:follow|socials?|links?|bookings?|contacts?|support|subscribe|downloads?|stream(?:ing)?|listen|credits?|video\b|filmed|shot|edit(?:ed|or)?|thanks|special\s+thanks" +
        @"|artists?|merch|website|business|e-?mail|copyright|disclaimer|tags|hashtags|connect|donat(?:e|ions?)|buy|shop|gear|equipment|more|other|another|also|check\s+out|my\s+other" +
        @"|apoie|apoio|contato|contatos|redes|siga|cr[eé]ditos|agradecimentos|outros|mais|doa[cç][aã]o|loja|parcerias?|patroc[ií]nio" +
        @"|поддерж\w*|контакт\w*|подпис\w*|ссылки|соцсети|благодарност\w*|донат\w*)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex InCommentsWords = new(
        @"comment|коммент|コメント|评论|評論|댓글|coment[aá]rio",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex MissingWords = new(
        @"\b(?:no|sem|sin|kein|нет|without)\b.{0,12}(?:track\s?list|tl\b)|coming\s+soon|\bsoon\b|em\s+breve|\bids?\s+only\b|\bunknown\b|\bnão\s+tem\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // ───────────────────────── Classification ─────────────────────────

    /// <summary>Splits a description or comment into classified lines. Handles \r\n, \n\r and stray \r alike.</summary>
    public static List<TracklistLine> Classify(string text)
    {
        var lines = new List<TracklistLine>();
        foreach (var raw in text.Replace("\r\n", "\n").Replace("\n\r", "\n").Replace('\r', '\n').Split('\n'))
        {
            var norm = ZeroWidth.Replace(raw.Normalize(NormalizationForm.FormKC), "").Trim();
            var line = new TracklistLine { Raw = raw.TrimEnd(), Norm = norm };
            Read(line);
            lines.Add(line);
        }
        return lines;
    }

    private static void Read(TracklistLine line)
    {
        var norm = line.Norm;
        if (norm.Length == 0) { line.Kind = LineKind.Blank; return; }
        if (!norm.Any(char.IsLetterOrDigit)) { line.Kind = LineKind.Divider; return; }
        if (HashtagsOnly.IsMatch(norm)) { line.Kind = LineKind.Noise; return; }

        var body = TrailingDecoration.Replace(LeadingDecoration.Replace(norm, ""), "");
        if (body.Length == 0) { line.Kind = LineKind.Divider; return; }

        // A leading time wins over everything: "Timestamps: 00:00 Intro" is the rare loss we accept.
        if (LeadingTime.Match(body) is { Success: true } lead && ParseTime(lead, "a") is { } start)
        {
            line.Kind = LineKind.Timestamped;
            line.Start = start;
            line.End = lead.Groups["bh"].Success || lead.Groups["bm"].Success ? ParseTime(lead, "b") : null;
            SetText(line, lead.Groups["rest"].Value);
            return;
        }

        var url = UrlRx.Match(body);
        var textPart = url.Success ? body.Remove(url.Index, url.Length) : body;
        var mentionsTracklist = StrongHeaderWords.IsMatch(textPart);

        if (url.Success)
        {
            line.Link = TrimUrl(url.Value);
            line.LinkKind = ClassifyLink(line.Link);

            if (line.LinkKind == LinkKind.TracklistSite || (mentionsTracklist && !MissingWords.IsMatch(textPart)))
            {
                // "Tracklist: https://1001tracklists.com/…", "Get the full tracklist at http://blrrm.tv/x"
                line.Kind = LineKind.OtherLink;
                line.Hint = HeaderHint.External;
                SetText(line, textPart);
                return;
            }
            if (line.LinkKind == LinkKind.Other)
            {
                // "Instagram: https://…", "artist → soundcloud profile" — or a bare URL nobody can place,
                // which Text = "" lets a list absorb as the previous entry's link.
                line.Kind = LineKind.OtherLink;
                SetText(line, textPart);
                return;
            }
        }

        if (SocialLabelRx.IsMatch(textPart))
        {
            line.Kind = LineKind.OtherLink;
            SetText(line, textPart);
            return;
        }
        if (IsHeader(textPart, out var strong, out var hint))
        {
            line.Kind = LineKind.Header;
            line.StrongHeader = strong;
            line.Hint = hint;
            return;
        }
        // A section title is short and alone on its line ("Follow me on social media:", "Support Artists");
        // "Artist - Title 3:45" merely starts with one of the words.
        if (textPart.Length <= 40 && !url.Success && ForeignHeaderWords.IsMatch(textPart.TrimStart())
            && !SeparatorRx.IsMatch(textPart)
            && (textPart.EndsWith(':') || textPart.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= 4))
        {
            line.Kind = LineKind.ForeignHeader;
            return;
        }

        if (NumberedRx.Match(textPart) is { Success: true } num)
        {
            line.Kind = LineKind.Numbered;
            SetText(line, num.Groups["rest"].Value);
            return;
        }
        if (TrailingTimeRx.Match(textPart) is { Success: true } trail && ParseTime(trail, "a") is { } trailing)
        {
            line.Kind = LineKind.TrailingTime;
            line.Start = trailing;
            line.End = trail.Groups["bm"].Success ? ParseTime(trail, "b") : null;
            SetText(line, trail.Groups["rest"].Value);
            return;
        }

        SetText(line, textPart);
        if (url.Success)
            line.Kind = line.Text.Length > 0 ? LineKind.TitleWithLink : LineKind.TrackLink;   // LinkKind.Track by now
        else
            line.Kind = LineKind.Text;
    }

    /// <summary>Stores the entry text, pulling out a link that sits inside it and tidying the edges.</summary>
    private static void SetText(TracklistLine line, string rest)
    {
        if (line.Link is null && UrlRx.Match(rest) is { Success: true } url)
        {
            line.Link = TrimUrl(url.Value);
            line.LinkKind = ClassifyLink(line.Link);
            rest = rest.Remove(url.Index, url.Length);
        }
        var text = EdgeSeparators.Replace(rest, "");
        text = EmptyBrackets.Replace(text, "");        // "Title ()" left behind by a link that sat in parentheses
        text = EdgeSeparators.Replace(text, "");
        line.Text = TrailingDecoration.Replace(text, "").Trim();
        line.HasSeparator = SeparatorRx.IsMatch(line.Text);
    }

    private static bool IsHeader(string body, out bool strong, out HeaderHint hint)
    {
        strong = false;
        hint = HeaderHint.None;
        var words = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        if (StrongHeaderWords.IsMatch(body))
        {
            strong = body.Length <= 60;   // a dedicated line vs. a sentence that mentions the list
            if (InCommentsWords.IsMatch(body)) hint = HeaderHint.InComments;
            else if (MissingWords.IsMatch(body)) hint = HeaderHint.Missing;
            return true;
        }
        if (body.Length <= 40 && WeakHeaderWords.IsMatch(body) && (body.EndsWith(':') || words <= 3))
        {
            strong = false;
            return true;
        }
        return false;
    }

    private static TimeSpan? ParseTime(Match m, string p)
    {
        var mm = m.Groups[p + "m"];
        var ss = m.Groups[p + "s"];
        if (!mm.Success || !ss.Success)
            return null;
        var hasH = m.Groups[p + "h"].Success;
        int h = hasH ? int.Parse(m.Groups[p + "h"].Value, CultureInfo.InvariantCulture) : 0;
        int min = int.Parse(mm.Value, CultureInfo.InvariantCulture);
        int sec = int.Parse(ss.Value, CultureInfo.InvariantCulture);
        if (sec > 59 || (hasH && min > 59) || min > 999)
            return null;
        return new TimeSpan(h, min, sec);
    }

    private static string TrimUrl(string url) => url.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '\'', '"');

    // ───────────────────────── Links ─────────────────────────

    private static readonly HashSet<string> SmartLinkHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "lnk.to", "fanlink.to", "fanlink.tv", "ffm.to", "hypeddit.com", "ditto.fm", "orcd.co", "smarturl.it",
        "bfan.link", "push.fm", "linkco.re", "song.link", "album.link", "ampl.ink", "lnkfi.re", "sndl.ink",
        "found.ee", "backl.ink", "distrokid.com", "unitedmasters.com", "li.sten.to",
    };

    private static readonly HashSet<string> TracklistSiteHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "1001tracklists.com", "1001.tl", "mixesdb.com", "setlist.fm", "mixcloud.com",
    };

    /// <summary>Where a URL points. Profiles, playlists, channels and social pages are all "Other".</summary>
    internal static LinkKind ClassifyLink(string url)
    {
        var abs = url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url;
        if (!Uri.TryCreate(abs, UriKind.Absolute, out var uri))
            return LinkKind.Other;

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];
        if (host.StartsWith("m.")) host = host[2..];
        var segs = uri.AbsolutePath.ToLowerInvariant().Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? seg0 = segs.Length > 0 ? segs[0] : null;

        if (TracklistSiteHosts.Contains(host))
            return LinkKind.TracklistSite;
        if (SmartLinkHosts.Contains(host))
            return LinkKind.Track;

        switch (host)
        {
            case "youtube.com":
            case "music.youtube.com":
                return seg0 is "watch" or "shorts" ? LinkKind.Track : LinkKind.Other;   // /@channel, /playlist, /channel/…
            case "youtu.be":
                return segs.Length >= 1 ? LinkKind.Track : LinkKind.Other;
            case "soundcloud.com":
                // soundcloud.com/artist = profile, soundcloud.com/artist/track = track, …/artist/sets/x = playlist
                return segs.Length >= 2 && segs[1] is not ("sets" or "likes" or "tracks" or "albums" or "reposts" or "followers" or "following")
                    ? LinkKind.Track : LinkKind.Other;
            case "on.soundcloud.com":
                return LinkKind.Other;   // opaque short link: could be anything
            case "open.spotify.com":
            case "spotify.com":
            case "play.spotify.com":
                {
                    var type = segs.FirstOrDefault(s => !s.StartsWith("intl-"));
                    return type is "track" or "album" or "episode" ? LinkKind.Track : LinkKind.Other;
                }
            case "beatport.com":
                return segs.Contains("track") ? LinkKind.Track : LinkKind.Other;
            case "music.apple.com":
            case "itunes.apple.com":
                return segs.Contains("album") || segs.Contains("song") ? LinkKind.Track : LinkKind.Other;
            case "deezer.com":
            case "tidal.com":
            case "listen.tidal.com":
                return segs.Contains("track") || segs.Contains("album") ? LinkKind.Track : LinkKind.Other;
            case "hearthis.at":
            case "audiomack.com":
                return segs.Length >= 2 ? LinkKind.Track : LinkKind.Other;
        }
        if (host.EndsWith(".bandcamp.com"))
            return seg0 is "track" or "album" ? LinkKind.Track : LinkKind.Other;
        return LinkKind.Other;
    }
}
