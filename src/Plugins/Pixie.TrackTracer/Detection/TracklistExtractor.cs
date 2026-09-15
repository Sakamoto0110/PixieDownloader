using YtDlpCore;

namespace Pixie.TrackTracer.Detection;

/// <summary>
/// Finds the tracklist of a mix in the free text around the video — the description first, then the
/// pinned comment, the uploader's own comments and, last, the most-liked ones — and reads it into
/// entries. Nothing about how people write tracklists is standard, so this is a heuristic on purpose:
/// every line is classified by the marks it carries (<see cref="TracklistLine"/>: a leading time, an
/// item number, a link to a track, an "Artist - Title" dash…), consecutive lines of one shape make a
/// block, and each block is scored by the evidence around it — a "Tracklist:" header above, times that
/// grow, a "Follow me" header that says it is something else. yt-dlp's own <c>chapters</c> are only a
/// fallback candidate: YouTube builds them from the description with rules stricter than ours (first
/// line at 0:00, at least three, ascending, bare timestamps), so they miss lists that start late, sit in
/// a comment, wrap the time in brackets or carry no time at all.
/// </summary>
public static class TracklistExtractor
{
    /// <summary>Blocks scoring below this are noise, not candidates.</summary>
    private const int MinScore = 10;

    public static TracklistReport Analyze(VideoInfo video)
    {
        var candidates = new List<Tracklist>();
        var notes = new List<string>();

        Scan(video.Description, TracklistSource.Description, "a descrição", author: null, likes: 0, video.Duration, candidates, notes);

        foreach (var comment in video.Comments)
        {
            var source = comment.IsPinned ? TracklistSource.PinnedComment
                       : comment.IsUploader ? TracklistSource.UploaderComment
                       : TracklistSource.TopComment;
            var label = source switch
            {
                TracklistSource.PinnedComment => $"o comentário fixado ({comment.Author})",
                TracklistSource.UploaderComment => $"o comentário do canal ({comment.Author})",
                _ => $"o comentário de {comment.Author}",
            };
            Scan(comment.Text, source, label, comment.Author, comment.LikeCount, video.Duration, candidates, notes);
        }

        if (video.Chapters.Count >= 3)
        {
            // A block YouTube itself turned into chapters is a list beyond doubt.
            for (int i = 0; i < candidates.Count; i++)
            {
                if (MatchesChapters(candidates[i], video.Chapters))
                    candidates[i] = candidates[i] with { Score = candidates[i].Score + 5, Evidence = [.. candidates[i].Evidence, "bate com os capítulos do YouTube"] };
            }
        }
        if (video.Chapters.Count >= 2)
            candidates.Add(FromChapters(video.Chapters));

        candidates.Sort((a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score) : a.Source.CompareTo(b.Source));
        return new TracklistReport(candidates.FirstOrDefault(), candidates, notes);
    }

    /// <summary>Same count and every start within a second of yt-dlp's chapters.</summary>
    public static bool MatchesChapters(Tracklist t, IReadOnlyList<ChapterInfo> chapters)
    {
        if (t.Entries.Count != chapters.Count)
            return false;
        for (int i = 0; i < chapters.Count; i++)
        {
            if (t.Entries[i].Start is not { } start || Math.Abs((start - chapters[i].Start).TotalSeconds) > 1)
                return false;
        }
        return true;
    }

    private static void Scan(string? text, TracklistSource source, string label, string? author, long likes, TimeSpan? duration,
                             List<Tracklist> candidates, List<string> notes)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var lines = TracklistLine.Classify(text);
        CollectNotes(lines, label, notes);

        foreach (var block in FindBlocks(lines))
        {
            if (Evaluate(block, source, author, likes, duration) is { } tracklist)
                candidates.Add(tracklist);
        }
    }

    private static void CollectNotes(List<TracklistLine> lines, string label, List<string> notes)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            switch (line.Hint)
            {
                case HeaderHint.InComments:
                    notes.Add($"{Capitalize(label)} aponta a tracklist para os comentários: \"{line.Norm}\"");
                    break;
                case HeaderHint.External:
                    notes.Add($"{Capitalize(label)} tem um link externo de tracklist: {line.Link}");
                    break;
                case HeaderHint.Missing:
                    notes.Add($"{Capitalize(label)} diz que não há tracklist: \"{line.Norm}\"");
                    break;
                case HeaderHint.None when line.Kind == LineKind.Header && line.StrongHeader:
                    {
                        // "▼Tracklist▼" with nothing under it but a link (pastebin, a doc…): the list lives there.
                        int next = NextNonBlank(lines, i);
                        if (next >= 0 && lines[next].Link is not null && lines[next].Text.Length == 0)
                            notes.Add($"{Capitalize(label)} tem um link externo de tracklist: {lines[next].Link}");
                        break;
                    }
            }
        }
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ───────────────────────── Blocks ─────────────────────────

    /// <summary>Lines with a time are one family, list-like lines (numbers, links, bare titles) another; they don't mix.</summary>
    private enum Family { Timed, Listed }

    private sealed class Block
    {
        public int Start;
        public Family Family;
        public readonly List<TracklistLine> Entries = [];
        public TracklistLine? Header;         // the header line found just above the block
        public bool ForeignHeader;            // a "Follow me:"-style header just above instead
        public int Strays;                    // short non-entry lines swallowed mid-block ("Part 2:")
        public int Merged;                    // "time on one line, title on the next" pairs joined so far
    }

    private enum Fit { No, Add, Attach, Merge, Skip }

    private static List<Block> FindBlocks(List<TracklistLine> lines)
    {
        var blocks = new List<Block>();
        int i = 0;
        while (i < lines.Count)
        {
            if (!CanStart(lines, i))
            {
                i++;
                continue;
            }

            var block = new Block { Start = i, Family = FamilyOf(lines[i].Kind) };
            LookAboveForHeader(block, lines);

            int j = i;
            while (j < lines.Count)
            {
                var line = lines[j];
                if (line.Kind == LineKind.Blank)
                {
                    // Blank lines inside a list are common ("title / blank / link / blank / title", a
                    // sub-heading per hour of a set); keep going only if the next real line still belongs
                    // to this block, or is a stray the list resumes right after.
                    int k = NextNonBlank(lines, j);
                    if (k >= 0 && (Fits(block, lines, k, acrossGap: true) != Fit.No || (block.Family == Family.Timed && IsStray(block, lines, k)))) { j = k; continue; }
                    break;
                }

                var fit = Fits(block, lines, j, acrossGap: false);
                if (fit == Fit.Add) { block.Entries.Add(line); j++; continue; }
                if (fit == Fit.Attach) { block.Entries[^1].Link = line.Link; block.Entries[^1].LinkKind = line.LinkKind; j++; continue; }
                if (fit == Fit.Merge)
                {
                    // "00:00" on one line, the title on the next: the pair is one entry.
                    var last = block.Entries[^1];
                    last.Text = line.Text;
                    last.HasSeparator = line.HasSeparator;
                    if (last.Link is null) { last.Link = line.Link; last.LinkKind = line.LinkKind; }
                    block.Merged++;
                    j++;
                    continue;
                }
                if (fit == Fit.Skip || IsStray(block, lines, j)) { block.Strays++; j++; continue; }
                break;
            }

            // A time with nothing after it at the very end ("12:05-") is an entry someone never finished.
            while (block.Entries.Count > 0 && block.Entries[^1].Text.Length == 0 && block.Entries[^1].Link is null && block.Family == Family.Timed)
                block.Entries.RemoveAt(block.Entries.Count - 1);

            if (block.Entries.Count >= 2)
                blocks.Add(block);
            i = Math.Max(j, i + 1);
        }
        return blocks;
    }

    private static Family FamilyOf(LineKind kind) =>
        kind is LineKind.Timestamped or LineKind.TrailingTime ? Family.Timed : Family.Listed;

    private static bool CanStart(List<TracklistLine> lines, int i)
    {
        var line = lines[i];
        switch (line.Kind)
        {
            case LineKind.Timestamped:
            case LineKind.TrailingTime:
            case LineKind.Numbered:
            case LineKind.TitleWithLink:
            case LineKind.TrackLink:
                return true;
            case LineKind.Text:
                // A bare title only opens a list when something vouches for it: a header right above, a
                // track link right below (title/link pairs), or an "Artist - Title" neighbour of the same shape.
                if (line.Norm.Length > 100)
                    return false;
                int prev = PrevNonBlank(lines, i, skipDividers: true);
                if (prev >= 0 && lines[prev].Kind == LineKind.Header && lines[prev].Hint == HeaderHint.None)
                    return true;
                int next = NextNonBlank(lines, i);
                if (next < 0)
                    return false;
                if (lines[next].Kind == LineKind.TrackLink)
                    return true;
                return line.HasSeparator && next == i + 1 && lines[next].Kind == LineKind.Text && lines[next].HasSeparator;
            default:
                return false;
        }
    }

    private static Fit Fits(Block block, List<TracklistLine> lines, int idx, bool acrossGap)
    {
        var line = lines[idx];
        switch (line.Kind)
        {
            case LineKind.TrackLink:
                {
                    // A link line right after an entry is that entry's link ("[00:00] Title" / "https://…").
                    var last = block.Entries.Count > 0 ? block.Entries[^1] : null;
                    if (last is { Link: null } && last.Kind != LineKind.TrackLink)
                        return Fit.Attach;
                    if (block.Family == Family.Listed)
                        return Fit.Add;
                    return last is not null ? Fit.Skip : Fit.No;   // a second link under a timed entry
                }
            case LineKind.OtherLink when line.Text.Length == 0:
                {
                    // A bare URL to somewhere we can't place (a label's site, a shortener) between the
                    // entries of a list belongs to the entry above it; it never breaks the list.
                    var last = block.Entries.Count > 0 ? block.Entries[^1] : null;
                    if (last is null)
                        return Fit.No;
                    return last.Link is null ? Fit.Attach : Fit.Skip;
                }
            case LineKind.Timestamped:
            case LineKind.TrailingTime:
                {
                    if (block.Family != Family.Timed)
                        return Fit.No;
                    // After a gap the list must still be a list: a bare "10:48" or a time that jumps back
                    // is a mention ("the drop at 10:48"), not the next track.
                    if (acrossGap && block.Entries.Count > 0 && (line.Text.Length == 0 || line.Start < block.Entries[^1].Start))
                        return Fit.No;
                    return Fit.Add;
                }
            case LineKind.Numbered:
            case LineKind.TitleWithLink:
                return block.Family == Family.Listed ? Fit.Add : Fit.No;
            case LineKind.Text:
                {
                    if (block.Family == Family.Timed)
                    {
                        // The title of a bare "00:00" line above — only while the time/title alternation
                        // keeps going (or has clearly established itself), so a closing remark under an
                        // unfinished "12:05-" is not taken for its title.
                        var last = block.Entries.Count > 0 ? block.Entries[^1] : null;
                        if (acrossGap || last is null || last.Text.Length > 0 || line.Norm.Length > 100)
                            return Fit.No;
                        int after = NextNonBlank(lines, idx);
                        bool alternationContinues = after >= 0 && lines[after].Kind is LineKind.Timestamped or LineKind.TrailingTime;
                        return alternationContinues || block.Merged >= 2 ? Fit.Merge : Fit.No;
                    }
                    if (line.Norm.Length > 100)
                        return Fit.No;
                    if (block.Entries.Count >= 2 && block.Entries.All(e => e.Kind == LineKind.Numbered))
                        return Fit.No;                                   // a numbered list has no unnumbered members
                    int next = NextNonBlank(lines, idx);
                    if (next >= 0 && lines[next].Kind == LineKind.TrackLink)
                        return Fit.Add;                                  // title/link pair
                    if (acrossGap)
                        return Fit.No;                                   // a blank line ends a list of bare titles
                    return block.Header is not null || line.HasSeparator ? Fit.Add : Fit.No;
                }
            default:
                return Fit.No;
        }
    }

    /// <summary>
    /// A short odd line inside a list ("Part 2:", "— second hour —", a row of hearts) that the list
    /// resumes right after. Timed lists get this always; bare-title lists only under a header, since
    /// for them any line could pass as an entry.
    /// </summary>
    private static bool IsStray(Block block, List<TracklistLine> lines, int idx)
    {
        var line = lines[idx];
        if (block.Strays >= 3)
            return false;
        if (block.Family == Family.Timed ? block.Entries.Count < 2 : block.Header is null)
            return false;
        if (line.Kind is not (LineKind.Text or LineKind.Header or LineKind.Divider) || line.Norm.Length > 60)
            return false;
        if (block.Family == Family.Listed && line.Kind == LineKind.Text)
            return false;   // under a header a text line is an entry or nothing — never a stray
        int next = NextNonBlank(lines, idx);
        return next >= 0 && Fits(block, lines, next, acrossGap: false) == Fit.Add;
    }

    private static void LookAboveForHeader(Block block, List<TracklistLine> lines)
    {
        // Up to three prose lines above the block, skipping blanks and dividers; an entry of another
        // list, or a header that points elsewhere, ends the search.
        int seen = 0;
        for (int i = block.Start - 1; i >= 0 && seen < 3; i--)
        {
            var line = lines[i];
            switch (line.Kind)
            {
                case LineKind.Blank:
                case LineKind.Divider:
                case LineKind.Noise:
                    continue;
                case LineKind.Header when line.Hint == HeaderHint.None:
                    block.Header = line;
                    return;
                case LineKind.ForeignHeader:
                    block.ForeignHeader = seen == 0;   // only when nothing else sits between it and the list
                    return;
                case LineKind.Text when seen == 0 && line.Norm.EndsWith(':') && line.Norm.Length <= 60:
                    // "Types of comments under every lofi video:" — a label for the list that isn't about tracks.
                    block.ForeignHeader = true;
                    return;
                case LineKind.Text:
                case LineKind.OtherLink:
                    seen++;
                    continue;
                default:
                    return;
            }
        }
    }

    private static int NextNonBlank(List<TracklistLine> lines, int idx)
    {
        for (int k = idx + 1; k < lines.Count; k++)
            if (lines[k].Kind != LineKind.Blank)
                return k;
        return -1;
    }

    private static int PrevNonBlank(List<TracklistLine> lines, int idx, bool skipDividers = false)
    {
        for (int k = idx - 1; k >= 0; k--)
        {
            if (lines[k].Kind == LineKind.Blank || (skipDividers && lines[k].Kind is LineKind.Divider or LineKind.Noise))
                continue;
            return k;
        }
        return -1;
    }

    // ───────────────────────── Scoring ─────────────────────────

    private static Tracklist? Evaluate(Block block, TracklistSource source, string? author, long likes, TimeSpan? duration)
    {
        var lines = block.Entries;
        int n = lines.Count;
        if (n < 3 && (n < 2 || block.Header is null))
            return null;

        var evidence = new List<string>();
        int score = Math.Min(n, 40);
        evidence.Add($"{n} linhas");

        int leading = lines.Count(l => l.Kind == LineKind.Timestamped);
        int numbered = lines.Count(l => l.Kind == LineKind.Numbered);
        int withLink = lines.Count(l => l.Link is not null && l.LinkKind == LinkKind.Track);
        int withText = lines.Count(l => l.Text.Length > 0);
        int separators = lines.Count(l => l.HasSeparator);
        int titleLike = lines.Count(l => l.HasSeparator || TracklistLine.HasTitleTag(l.Text));

        TracklistShape shape;
        bool monotonic = false;
        if (block.Family == Family.Timed)
        {
            var starts = lines.Select(l => l.Start!.Value).ToList();
            int drops = starts.Zip(starts.Skip(1), (a, b) => b < a ? 1 : 0).Sum();
            monotonic = drops == 0;

            if (leading == 0 && !monotonic && starts.All(s => s < TimeSpan.FromMinutes(20)))
            {
                // "Artist - Title 3:45" lines whose times don't grow are track lengths, not positions.
                shape = TracklistShape.TitleWithDuration;
                evidence.Add("tempos no fim da linha não crescem — parecem durações, não posições");
            }
            else
            {
                shape = TracklistShape.Timestamped;
                if (monotonic) { score += 10; evidence.Add("tempos crescentes"); }
                else if (drops <= n / 10) { score += 5; monotonic = true; evidence.Add($"tempos crescentes, {drops} fora de ordem (erro de digitação?)"); }
                else { score -= 10; evidence.Add($"tempos fora de ordem ({drops}×)"); }
                if (starts[0] <= TimeSpan.FromMinutes(2)) { score += 3; evidence.Add($"começa em {Format(starts[0])}"); }
                if (duration is { } d)
                {
                    if (starts[^1] < d) { score += 3; evidence.Add("último tempo dentro da duração"); }
                    else { score -= 5; evidence.Add("último tempo passa da duração do vídeo"); }
                }
            }
        }
        else if (withText == 0)
        {
            shape = TracklistShape.LinksOnly;
            score += 2;
            evidence.Add("só links de faixa");
        }
        else if (withLink * 2 >= n)
        {
            shape = TracklistShape.TitleWithLink;
            score += 6;
            evidence.Add($"{withLink} links de faixa");
        }
        else if (numbered * 2 >= n)
        {
            shape = TracklistShape.Numbered;
            score += 6;
            evidence.Add("numeração");
        }
        else
        {
            shape = TracklistShape.PlainTitles;
            if (block.Header is null && titleLike * 2 < n)
                return null;   // bare lines with nothing vouching for them: prose
            evidence.Add("só títulos");
        }

        if (block.Header is { } header)
        {
            score += header.StrongHeader ? 15 : 8;
            evidence.Add($"cabeçalho \"{header.Norm}\"");
        }
        if (block.ForeignHeader)
        {
            score -= 15;
            evidence.Add("abaixo de um cabeçalho de outra seção");
        }
        if (separators * 2 >= n)
        {
            score += 4;
            evidence.Add("artista - título");
        }
        else if (titleLike * 2 >= n)
        {
            score += 4;
            evidence.Add("tags de remix/gênero nos títulos");
        }
        if (n >= 4 && lines.Select(l => l.Text).Distinct(StringComparer.OrdinalIgnoreCase).Count() * 2 < n)
        {
            score -= 10;
            evidence.Add("textos repetidos");
        }
        if (withText > 0)
        {
            // Sentences, not titles: long or wordy lines without an "Artist - Title" split ("1. Drink water
            // every 45 minutes…"). A long title with a dash in it is still a title.
            var texts = lines.Where(l => l.Text.Length > 0).Select(l => l.Text).ToList();
            double avgLength = texts.Average(t => t.Length);
            double avgWords = texts.Average(t => t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
            if (avgLength > 120 || (titleLike * 2 < n && (avgLength > 60 || avgWords >= 5)))
            {
                score -= 8;
                evidence.Add("linhas longas demais — parecem frases");
            }
        }
        if (block.Strays > 0)
            evidence.Add($"{block.Strays} linha(s) estranha(s) no meio");

        // Where it was found: the description beats a viewer's partial copy of the same list.
        int sourceBonus = source switch
        {
            TracklistSource.Description => 10,
            TracklistSource.PinnedComment => 6,
            TracklistSource.UploaderComment => 4,
            _ => 0,
        };
        score += sourceBonus;
        if (likes > 0)
            evidence.Add($"{likes} curtidas");

        if (score < MinScore)
            return null;

        var entries = new List<TracklistEntry>(n);
        for (int i = 0; i < n; i++)
        {
            var line = lines[i];
            TimeSpan? start = shape == TracklistShape.TitleWithDuration ? null : line.Start;
            TimeSpan? end = line.End;
            if (end is null && start is not null && monotonic && i + 1 < n)
                end = lines[i + 1].Start;
            entries.Add(new TracklistEntry(i + 1, start, end, line.Text, line.Link, line.Raw));
        }

        return new Tracklist(source, shape, block.Header?.Norm, entries, score, evidence) { Author = author };
    }

    private static Tracklist FromChapters(IReadOnlyList<ChapterInfo> chapters)
    {
        var entries = chapters
            .Select((c, i) => new TracklistEntry(i + 1, c.Start, c.End, c.Title, null, $"{Format(c.Start)} {c.Title}"))
            .ToList();
        // Below any block we detect ourselves with a header or growing times; above a weak bare list.
        int score = Math.Min(entries.Count, 40) + 8;
        return new Tracklist(TracklistSource.Chapters, TracklistShape.Timestamped, null, entries, score,
            [$"{entries.Count} capítulos do yt-dlp"]);
    }

    /// <summary><c>m:ss</c> under an hour, <c>h:mm:ss</c> above — the way tracklists write them.</summary>
    public static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
}
