using System.Text;

using YtDlpCore;

namespace Pixie.Tracklist.Detection;

/// <summary>
/// Renders a <see cref="TracklistReport"/> as the plain-text block the debug tab shows after an
/// analysis: the URL and title, then only what concerns tracklists — the list picked, the other
/// candidates, yt-dlp's chapters, the hints seen and how many comments were read. Pure text, so it
/// can be diffed in tests and pasted into an issue.
/// </summary>
public static class TracklistReportFormatter
{
    private const string Rule = "==========";

    public static string Format(VideoInfo video, TracklistReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine(video.WebpageUrl);
        sb.AppendLine(Rule);
        sb.AppendLine(video.Title);
        sb.AppendLine(Rule);

        if (report.Best is { } best)
        {
            sb.Append("Tracklist: ").Append(Describe(best)).AppendLine();
            sb.Append("  evidências: ").AppendLine(string.Join("; ", best.Evidence));
            foreach (var e in best.Entries)
                sb.Append("  ").AppendLine(FormatEntry(e, best.Shape));
        }
        else
        {
            sb.AppendLine("Nenhuma tracklist encontrada.");
        }

        var others = report.Candidates.Where(c => !ReferenceEquals(c, report.Best)).ToList();
        if (others.Count > 0)
        {
            sb.AppendLine("Outros candidatos:");
            foreach (var c in others)
            {
                sb.Append("  • ").AppendLine(Describe(c));
                sb.Append("    evidências: ").AppendLine(string.Join("; ", c.Evidence));
            }
        }

        sb.Append("Capítulos (yt-dlp): ").Append(video.Chapters.Count);
        if (report.Best is { Source: not TracklistSource.Chapters } chosen && video.Chapters.Count > 0)
            sb.Append(TracklistExtractor.MatchesChapters(chosen, video.Chapters) ? " — batem com a lista escolhida" : " — diferentes da lista escolhida");
        sb.AppendLine();

        if (report.Notes.Count > 0)
        {
            sb.AppendLine("Avisos:");
            foreach (var note in report.Notes)
                sb.Append("  • ").AppendLine(note);
        }

        var pinned = video.Comments.FirstOrDefault(c => c.IsPinned);
        int fromUploader = video.Comments.Count(c => c.IsUploader);
        sb.Append("Comentários lidos: ").Append(video.Comments.Count);
        if (video.Comments.Count > 0)
        {
            sb.Append(" · fixado: ").Append(pinned is null ? "nenhum" : pinned.Author ?? "(sem autor)");
            sb.Append(" · do canal: ").Append(fromUploader);
        }
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>One line: where it came from, how many entries, what shape, and the score with its confidence.</summary>
    public static string Describe(Tracklist t)
    {
        var where = t.Source switch
        {
            TracklistSource.Description => "descrição",
            TracklistSource.PinnedComment => $"comentário fixado ({t.Author})",
            TracklistSource.UploaderComment => $"comentário do canal ({t.Author})",
            TracklistSource.TopComment => $"comentário de {t.Author}",
            TracklistSource.Chapters => "capítulos do yt-dlp",
            _ => t.Source.ToString(),
        };
        var shape = t.Shape switch
        {
            TracklistShape.Timestamped => "com tempos",
            TracklistShape.TitleWithDuration => "títulos com duração",
            TracklistShape.Numbered => "numerada",
            TracklistShape.TitleWithLink => "títulos com link",
            TracklistShape.LinksOnly => "só links",
            TracklistShape.PlainTitles => "só títulos",
            _ => t.Shape.ToString(),
        };
        var confidence = t.Score >= 50 ? "alta" : t.Score >= 30 ? "média" : "baixa";
        return $"{where} · {t.Entries.Count} faixas · {shape} · pontuação {t.Score} (confiança {confidence})";
    }

    public static string FormatEntry(TracklistEntry e, TracklistShape shape)
    {
        var sb = new StringBuilder();
        sb.Append(e.Number.ToString("00"));
        if (e.Start is { } start)
        {
            sb.Append("  ").Append(TracklistExtractor.Format(start).PadLeft(7));
            if (e.End is { } end && shape == TracklistShape.Timestamped && e.Raw.Contains(TracklistExtractor.Format(end)))
                sb.Append('-').Append(TracklistExtractor.Format(end));   // only when the line itself carried the end time
        }
        else
        {
            sb.Append("  ").Append(' ', 7);
        }
        sb.Append("  ").Append(e.Text.Length > 0 ? e.Text : "(sem título)");
        if (e.Link is not null)
            sb.Append("  → ").Append(e.Link);
        return sb.ToString();
    }
}
