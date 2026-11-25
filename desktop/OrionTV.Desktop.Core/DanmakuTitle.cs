using System.Globalization;
using System.Text.RegularExpressions;

namespace OrionTV.Desktop.Core;

/// <summary>标题解析结果。</summary>
/// <param name="Cleaned">去掉括号备注后的标题(用于搜索)</param>
/// <param name="Normalized">去掉空白和标点、转小写后的完整标题</param>
/// <param name="Base">再去掉"第二季"之类季标记和"国语版"之类版本后缀后的片名</param>
/// <param name="BaseText">去掉季标记后、未规范化的片名(用于第二次搜索)</param>
/// <param name="Season">标题里写明的季号;没写为 null</param>
/// <param name="Variant">标题带"特别版/国语版"等版本后缀</param>
public sealed record DanmakuParsedTitle(
    string Cleaned,
    string Normalized,
    string Base,
    string BaseText,
    int? Season,
    bool Variant);

/// <summary>平台搜索结果里的一个候选。</summary>
/// <param name="Title">片名</param>
/// <param name="Year">年份(可空)</param>
/// <param name="EpisodeCount">该候选的总集数;未知为 0</param>
/// <param name="IsMovie">是否电影;未知为 null</param>
public record DanmakuTitleCandidate(string Title, string? Year = null, int EpisodeCount = 0, bool? IsMovie = null);

/// <summary>
/// 标题解析与候选匹配。宁可匹配不到,也不把别的片子的弹幕套上来。
/// 与手机版 services/danmaku/title.ts 逐条对应。
/// </summary>
public static class DanmakuTitle
{
    private const RegexOptions Insensitive = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex Brackets = new(@"【[^】]*】|\[[^\]]*\]|（[^）]*）|\([^)]*\)");
    private static readonly Regex Whitespace = new(@"[\s\uFEFF]+");
    private static readonly Regex Noise = new(
        "[\\s\\uFEFF\\-_—–~·•:：,，.。!！?？'\"“”‘’`、/\\\\|()（）\\[\\]【】《》<>「」『』+*&^%$#@=;；]");
    private static readonly Regex Variants = new(
        @"(特别版|重制版|国语版|粤语版|日语版|英文版|英语版|中配版|中文版|普通话版|未删减版|加长版|精编版|会员版|tv版|国语|粤语|日语)\z",
        Insensitive);
    private static readonly Regex ChineseSeason = new(@"第[\s\uFEFF]*([0-9零一二两三四五六七八九十]+)[\s\uFEFF]*[季部]");
    private static readonly Regex EnglishSeason = new(
        @"(^|[^a-z])(?:season|s)[\s\uFEFF]*0*([0-9]{1,2})(?![0-9a-z])", Insensitive);
    private static readonly Regex TailNumber = new(@"^(.*[^0-9\s\uFEFF])[\s\uFEFF]*([0-9]{1,2})\z");
    private static readonly Regex AllDigits = new(@"^[0-9]+\z");
    private static readonly Regex YearPattern = new(@"(19|20)[0-9]{2}");
    private static readonly Regex EpisodeNumberPattern = new(@"^第?[\s\uFEFF]*0*([0-9]{1,4})[\s\uFEFF]*[集话話回期]?\z");
    private static readonly Regex Extras = new(
        "预告|花絮|彩蛋|片花|抢先看|特辑|幕后|采访|主题曲|片尾曲|MV|纯享|看点|速看|解说", Insensitive);

    private static readonly Dictionary<char, int> ChineseDigits = new()
    {
        ['零'] = 0,
        ['一'] = 1,
        ['二'] = 2,
        ['两'] = 2,
        ['三'] = 3,
        ['四'] = 4,
        ['五'] = 5,
        ['六'] = 6,
        ['七'] = 7,
        ['八'] = 8,
        ['九'] = 9,
    };

    /// <summary>"十二" → 12,"3" → 3;认不出返回 null(中文数字只支持 0~99)。</summary>
    public static int? ParseChineseNumber(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0)
        {
            return null;
        }

        if (AllDigits.IsMatch(value))
        {
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? number
                : int.MaxValue;
        }

        var tenIndex = value.IndexOf('十');
        if (tenIndex < 0)
        {
            return value.Length == 1 && ChineseDigits.TryGetValue(value[0], out var digit) ? digit : null;
        }

        var head = value[..tenIndex];
        var tail = value[(tenIndex + 1)..];
        if (head.Length > 1 || tail.Length > 1)
        {
            return null;
        }

        var tens = 1;
        var ones = 0;
        if (head.Length == 1 && !ChineseDigits.TryGetValue(head[0], out tens))
        {
            return null;
        }

        if (tail.Length == 1 && !ChineseDigits.TryGetValue(tail[0], out ones))
        {
            return null;
        }

        return tens * 10 + ones;
    }

    public static string NormalizeTitleText(string? text) =>
        Noise.Replace((text ?? "").ToLowerInvariant(), "");

    public static DanmakuParsedTitle ParseTitle(string? raw)
    {
        var source = raw ?? "";
        var cleaned = Whitespace.Replace(Brackets.Replace(source, " "), " ").Trim();
        if (cleaned.Length == 0)
        {
            cleaned = source.Trim();
        }

        var rest = cleaned;
        int? season = null;

        var cn = ChineseSeason.Match(rest);
        if (cn.Success)
        {
            var n = ParseChineseNumber(cn.Groups[1].Value);
            if (n is > 0)
            {
                season = n;
                rest = ReplaceFirst(rest, cn.Value, " ");
            }
        }

        if (season is null)
        {
            var en = EnglishSeason.Match(rest);
            if (en.Success)
            {
                season = int.Parse(en.Groups[2].Value, CultureInfo.InvariantCulture);
                rest = ReplaceFirst(rest, en.Value, en.Groups[1].Value + " ");
            }
        }

        if (season is null)
        {
            // "庆余年2":片名后直接跟 2~20 的数字按季号处理("请回答1988"这类不算)
            var tail = TailNumber.Match(rest);
            if (tail.Success)
            {
                var n = int.Parse(tail.Groups[2].Value, CultureInfo.InvariantCulture);
                if (n >= 2 && n <= 20)
                {
                    season = n;
                    rest = tail.Groups[1].Value;
                }
            }
        }

        var baseText = Whitespace.Replace(rest, " ").Trim();
        var baseName = NormalizeTitleText(baseText);
        var variant = false;
        var stripped = Variants.Replace(baseName, "", 1);
        if (stripped.Length > 0 && stripped != baseName)
        {
            baseName = stripped;
            variant = true;
        }

        return new DanmakuParsedTitle(cleaned, NormalizeTitleText(cleaned), baseName, baseText, season, variant);
    }

    /// <summary>
    /// 给候选打分;0 表示不是同一部片。
    /// 完全同名 100;片名相同且季号相同 90(带版本后缀 80);再按年份、电影/剧集、集数是否够用微调。
    /// </summary>
    public static int ScoreTitleCandidate(
        DanmakuParsedTitle query,
        DanmakuTitleCandidate candidate,
        string? year,
        int episodeCount,
        int episodeNumber)
    {
        var parsed = ParseTitle(candidate.Title);
        if (parsed.Normalized.Length == 0 || query.Normalized.Length == 0)
        {
            return 0;
        }

        int score;
        if (parsed.Normalized == query.Normalized)
        {
            score = 100;
        }
        else if (parsed.Base.Length > 0 && parsed.Base == query.Base && (parsed.Season ?? 1) == (query.Season ?? 1))
        {
            score = parsed.Variant || query.Variant ? 80 : 90;
        }
        else
        {
            return 0;
        }

        var wantYear = ParseYear(year);
        var gotYear = ParseYear(candidate.Year);
        if (wantYear is not null && gotYear is not null)
        {
            if (wantYear == gotYear)
            {
                score += 5;
            }
            else if (Math.Abs(wantYear.Value - gotYear.Value) > 1)
            {
                score -= 8;
            }
        }

        var wantMovie = episodeCount <= 1;
        if (candidate.IsMovie is { } isMovie)
        {
            if (wantMovie == isMovie)
            {
                score += 2;
            }
            else if (!wantMovie && isMovie)
            {
                score -= 20;
            }
        }

        if (candidate.EpisodeCount > 0 && episodeNumber > candidate.EpisodeCount)
        {
            score -= 10;
        }

        return Math.Max(score, 1);
    }

    /// <summary>从候选里挑得分最高的;同分取靠前的。没有同片候选时返回 -1。</summary>
    public static int PickBestCandidate(
        DanmakuParsedTitle query,
        IReadOnlyList<DanmakuTitleCandidate> candidates,
        string? year,
        int episodeCount,
        int episodeNumber)
    {
        var best = -1;
        var bestScore = 0;
        for (var index = 0; index < candidates.Count; index++)
        {
            var score = ScoreTitleCandidate(query, candidates[index], year, episodeCount, episodeNumber);
            if (score >= 60 && score > bestScore)
            {
                best = index;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>
    /// 在平台的分集列表里找当前集。
    /// 分集都带集号时只按集号找(找不到就是没有这一集);否则按下标。电影(只有 1 集)直接取第一条。
    /// </summary>
    public static int PickEpisodeIndex(
        IReadOnlyList<int?> episodeNumbers,
        int episodeIndex,
        int episodeNumber,
        int episodeCount)
    {
        if (episodeNumbers.Count == 0)
        {
            return -1;
        }

        if (episodeCount <= 1 && episodeNumbers.Count == 1)
        {
            return 0;
        }

        var allNumbered = true;
        for (var i = 0; i < episodeNumbers.Count; i++)
        {
            if (episodeNumbers[i] == episodeNumber)
            {
                return i;
            }

            if (episodeNumbers[i] is null)
            {
                allNumbered = false;
            }
        }

        if (allNumbered)
        {
            return -1;
        }

        return episodeIndex >= 0 && episodeIndex < episodeNumbers.Count ? episodeIndex : -1;
    }

    /// <summary>"12"、"第12集"、"12话" → 12;不是纯集号返回 null。</summary>
    public static int? ParseEpisodeNumber(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var match = EpisodeNumberPattern.Match(text.Trim());
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>预告、花絮之类不是正片的分集。</summary>
    public static bool IsExtraEpisodeTitle(string? title) =>
        !string.IsNullOrEmpty(title) && Extras.IsMatch(title);

    /// <summary>发给 danmu_api /api/v2/match 的文件名:剧集为 "片名 S01E05",电影只发片名。</summary>
    public static string BuildMatchFileName(string title, int episodeNumber, int episodeCount)
    {
        var parsed = ParseTitle(title);
        if (episodeCount <= 1)
        {
            return parsed.Cleaned;
        }

        var name = parsed.BaseText.Length > 0 ? parsed.BaseText : parsed.Cleaned;
        return $"{name} S{Pad(parsed.Season ?? 1)}E{Pad(episodeNumber)}";
    }

    /// <summary>由播放器里的集标题推出集号:"第 12 集" → 12;认不出时用下标 + 1。</summary>
    public static int EpisodeNumberFromLabel(string? label, int episodeIndex)
    {
        var text = label ?? "";
        var match = LabelEpisode.Match(text);
        if (!match.Success)
        {
            match = LabelDigits.Match(text.Trim());
        }

        return match.Success
               && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
               && parsed > 0
            ? parsed
            : episodeIndex + 1;
    }

    private static readonly Regex LabelEpisode = new(@"第[\s\uFEFF]*0*([0-9]{1,4})[\s\uFEFF]*[集话話回期]");
    private static readonly Regex LabelDigits = new(@"^0*([0-9]{1,4})\z");

    private static string Pad(int n) =>
        Math.Max(0, n).ToString(CultureInfo.InvariantCulture).PadLeft(2, '0');

    private static int? ParseYear(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var match = YearPattern.Match(value);
        return match.Success ? int.Parse(match.Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>与 JS 的 String.prototype.replace(字符串, 字符串) 相同:只替换第一次出现。</summary>
    private static string ReplaceFirst(string text, string search, string replacement)
    {
        var index = text.IndexOf(search, StringComparison.Ordinal);
        return index < 0 ? text : string.Concat(text.AsSpan(0, index), replacement, text.AsSpan(index + search.Length));
    }
}
