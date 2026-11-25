using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App;

/// <summary>
/// “点一下直接起播”的选线，搜索结果与首页横幅共用（原先写在 SearchView 里，原样搬过来）：
/// 有观看记录的线路按记录续播，没有就从第一条有剧集的线路的第一集开始；取不到可播剧集时返回 null，由调用方退回详情页。
/// </summary>
public static class DirectPlay
{
    public static async Task<PlayParameter?> ResolveAsync(AppServices services, CatalogItem item)
    {
        var candidates = item.CandidateSources is { Count: > 0 }
            ? item.CandidateSources
            : await services.Catalog.GetPreferredDetailSourcesAsync(services.Sources, item.Source, item.Id, item.Title);
        candidates = candidates.Where(candidate => !services.Catalog.IsResultHidden(candidate)).ToList();

        // 续播：优先用有观看记录的那条线路（同一 source/id，线路 key 一致的优先）
        PlayRecordItem? record = null;
        SearchResult? line = null;
        foreach (var candidate in candidates)
        {
            var saved = services.PlayRecords.Get(candidate.Source, candidate.Id);
            if (saved is null || services.IsPlayRecordPendingDeletion(candidate.Source, candidate.Id))
            {
                continue;
            }

            if (line is null || string.Equals(saved.PlaybackSourceKey, candidate.PlaybackSourceKey, StringComparison.Ordinal))
            {
                record = saved;
                line = candidate;
                if (string.Equals(saved.PlaybackSourceKey, candidate.PlaybackSourceKey, StringComparison.Ordinal))
                {
                    break;
                }
            }
        }

        line ??= candidates.FirstOrDefault(candidate => candidate.Episodes.Count > 0) ?? candidates.FirstOrDefault();
        if (line is not null && line.Episodes.Count == 0)
        {
            // TVBox 等源 search 不带剧集：按 id 补一次
            line = await services.Catalog.HydrateEpisodesAsync(services.Sources, line) ?? line;
        }

        if (line is not null && line.Episodes.Count > 0)
        {
            PlaybackResumeContext? resume = null;
            var index = 0;
            if (record is not null)
            {
                resume = ResumeFromRecord(record, line.Source, line.Id);
                index = resume.FindEpisodeIndex(line.EpisodeLabels, line.Episodes.Count);
                if (index < 0)
                {
                    index = 0;
                    resume = null;
                }
            }

            if (!string.IsNullOrWhiteSpace(line.Episodes[index]))
            {
                return new PlayParameter(
                    item.Title,
                    line.Source,
                    line.SourceName,
                    line.Id,
                    line.Episodes,
                    index,
                    resume?.StartPositionMs ?? 0,
                    candidates,
                    string.IsNullOrWhiteSpace(item.Poster) ? line.Poster : item.Poster,
                    line.PlaybackSourceKey,
                    line.EpisodeLabels,
                    resume,
                    AdultContentFilter.CategoryOf(line));
            }
        }

        return null;
    }

    /// <summary>由一条观看记录得到续播位置（集数按序号 / 标签 / 集号定位）。</summary>
    public static PlaybackResumeContext ResumeFromRecord(PlayRecordItem record, string source, string id)
    {
        EpisodeLabelFormatter.TryGetNumber(record.EpisodeLabel, out var parsedNumber);
        return new PlaybackResumeContext(
            record.Index,
            record.PlayTime * 1000,
            record.EpisodeLabel,
            record.EpisodeNumber ?? (parsedNumber > 0 ? parsedNumber : (int?)null),
            source,
            id);
    }
}
