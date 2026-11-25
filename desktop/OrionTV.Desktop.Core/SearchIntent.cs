namespace OrionTV.Desktop.Core;

/// <summary>一组自然语言检索意图：说法（patterns）→ 题材关键词（genres）。</summary>
public sealed record SearchIntent(
    string Label,
    IReadOnlyList<string> Patterns,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string>? ExcludeGenres = null);

/// <summary>一次查询命中的意图。Genres 已去掉被否定提示（“不要爱情”等）排除的题材。</summary>
public sealed record SearchIntentMatch(
    string Label,
    IReadOnlyList<string> Matched,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> ExcludeGenres);

/// <summary>
/// 自然语言搜索：把天气 / 心情 / 场景类说法（回南天、梅雨、高铁候机、夜宵、聚会、家务背景音…）
/// 翻成题材检索意图。纯本地规则表，不联网、不上传查询内容。
/// 题材关键词用来匹配各片源自己的分类名（“喜剧片”“纪录片”“国产动漫”…），再与关键词搜索结果合并去重。
/// </summary>
public static class SearchIntents
{
    public static IReadOnlyList<SearchIntent> All { get; } =
    [
        new("晴日清爽",
            ["晴天", "大晴天", "阳光", "好天气", "阳光明媚", "晒太阳", "春游", "踏青", "野餐", "郊游", "户外", "散步", "遛弯", "春日", "风和日丽"],
            ["喜剧", "动画", "家庭", "爱情", "冒险"], ["恐怖", "惊悚"]),
        new("阴天絮语",
            ["阴天", "多云", "灰蒙", "起雾", "大雾", "迷雾天", "潮湿", "回南天", "梅雨", "湿冷", "惆怅", "寡淡"],
            ["剧情", "悬疑", "爱情"], ["恐怖"]),
        new("雪夜寒冬",
            ["下雪", "雪天", "初雪", "冬天", "冬日", "寒冷", "严寒", "雪地", "滑雪", "暴风雪", "圣诞", "飘雪"],
            ["剧情", "家庭", "奇幻", "爱情"], ["恐怖"]),
        new("风雨风暴",
            ["大风", "台风", "暴风雨", "风暴", "雷阵雨", "打雷", "闪电", "电闪雷鸣", "暴雨"],
            ["冒险", "动作", "悬疑", "惊悚"]),
        new("炎炎夏日",
            ["夏天", "炎热", "酷暑", "高温", "消暑", "纳凉", "暑假", "泳池", "海边", "海滩", "冲浪", "海岛", "烈日"],
            ["冒险", "奇幻", "爱情", "喜剧"], ["恐怖"]),
        new("焦躁求治愈",
            ["焦虑", "烦躁", "心烦", "压力大", "内耗", "自闭", "emo了", "难过", "郁闷", "不开心", "想被治愈", "疗愈", "平静", "静心", "心累"],
            ["动画", "喜剧", "家庭", "音乐"], ["恐怖", "惊悚", "犯罪"]),
        new("无聊杀时间",
            ["无聊", "打发时间", "杀时间", "摸鱼", "划水", "放空", "随便看看", "都行", "剧荒", "不知道看什么"],
            ["喜剧", "动画", "冒险", "悬疑"], ["恐怖"]),
        new("打起精神",
            ["励志", "打鸡血", "鸡血", "干劲", "冲刺", "逆袭", "奋斗", "拼搏", "振作", "燃一下", "打起精神", "重新开始"],
            ["剧情", "动作", "历史"]),
        new("愤怒发泄",
            ["生气", "愤怒", "火大", "暴躁", "窝火", "出气", "发泄", "气死我了"],
            ["动作", "惊悚", "犯罪"]),
        new("心碎碎片",
            ["失恋", "分手", "心碎", "白月光", "意难平", "放不下", "舔狗", "复合"],
            ["剧情", "爱情"], ["恐怖"]),
        new("怀旧青春",
            ["怀旧", "复古", "童年", "小时候", "青春", "校园", "年代感", "旧时光", "80年代", "90年代", "千禧", "学生时代"],
            ["剧情", "爱情", "喜剧", "动画"]),
        new("好奇探索",
            ["好奇", "揭秘", "未知", "探索", "冷知识", "涨知识", "真实事件", "大开眼界", "长见识"],
            ["纪录", "悬疑", "冒险"]),
        new("出差远行",
            ["出差", "酒店", "宾馆", "民宿", "候机", "飞机上", "高铁", "火车", "大巴", "长途", "倒时差", "绿皮车", "候车"],
            ["喜剧", "冒险", "动画", "奇幻"], ["恐怖"]),
        new("通勤路上",
            ["通勤", "地铁", "公交", "上下班", "坐车", "堵车", "挤地铁", "班车"],
            ["喜剧", "动画", "冒险"], ["恐怖"]),
        new("公路露营",
            ["自驾", "公路旅行", "公路", "露营", "徒步", "登山", "爬山", "旅行", "旅游", "度假", "国家公园", "野餐旅行"],
            ["冒险", "奇幻", "纪录"], ["恐怖"]),
        new("小酌夜宵",
            ["喝酒", "小酌", "独酌", "下酒", "酒吧", "夜宵", "烧烤", "大排档", "配酒", "微醺"],
            ["犯罪", "惊悚", "喜剧", "剧情"]),
        new("朋友聚会",
            ["聚会", "团建", "派对", "轰趴", "闺蜜", "兄弟", "几个朋友", "多人一起", "一起看", "热闹"],
            ["喜剧", "动作", "奇幻", "冒险"]),
        new("家务背景音",
            ["打扫", "做家务", "收拾", "整理", "洗衣服", "做饭", "烹饪", "边做边看", "当背景音", "背景音", "大扫除", "拖地"],
            ["纪录", "喜剧", "音乐", "综艺"], ["恐怖", "惊悚"]),
    ];

    private const string FillerIntentLabel = "无聊杀时间";

    private static readonly (string[] Patterns, string[] Genres)[] NegativeHints =
    [
        (["不要爱情", "不想看爱情", "别爱情", "不谈情说爱", "没有爱情"], ["爱情"]),
        (["不要纪录片", "别纪录片", "不想看纪录片"], ["纪录"]),
        (["不要动画", "别动画", "不想看动画"], ["动画"]),
        (["不要战争", "别战争", "不想看战争"], ["战争", "历史"]),
        (["不要悲伤", "不想伤感", "别虐"], ["剧情"]),
    ];

    // 题材关键词 → 片源分类名里常见的写法。
    private static readonly Dictionary<string, string[]> GenreAliases = new(StringComparer.Ordinal)
    {
        ["动画"] = ["动画", "动漫"],
        ["纪录"] = ["纪录", "记录"],
        // 片源很少单列“奇幻”，通常并在“科幻片”里。
        ["奇幻"] = ["奇幻", "科幻"],
    };

    /// <summary>
    /// 解析查询里的意图；普通片名（不含任何说法）返回 null，搜索行为与以前完全一致。
    /// 多组同时命中时取“命中说法总长度”最大的一组（更具体的说法优先，口头语降权），并列取表中靠前者。
    /// </summary>
    public static SearchIntentMatch? Resolve(string? query)
    {
        var text = Normalize(query);
        if (text.Length == 0)
        {
            return null;
        }

        SearchIntent? best = null;
        List<string> bestMatched = [];
        var bestScore = 0d;
        foreach (var intent in All)
        {
            var matched = intent.Patterns
                .Where(pattern => text.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                .ToList();
            // “打发时间 / 随便看看 / 都行”这类口头语常和具体场景一起出现（“高铁上打发时间”），
            // 降权后只在没有更具体的天气 / 场景说法时才生效。
            var score = matched.Sum(pattern => pattern.Length) * (intent.Label == FillerIntentLabel ? 0.4 : 1.0);
            if (score > bestScore)
            {
                best = intent;
                bestMatched = matched;
                bestScore = score;
            }
        }

        if (best is null)
        {
            return null;
        }

        var excluded = new List<string>(best.ExcludeGenres ?? []);
        foreach (var (patterns, genres) in NegativeHints)
        {
            if (patterns.Any(pattern => text.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            {
                excluded.AddRange(genres);
            }
        }

        var excludedSet = excluded.Distinct(StringComparer.Ordinal).ToList();
        var genresLeft = best.Genres.Where(genre => !excludedSet.Contains(genre)).ToList();
        return new SearchIntentMatch(best.Label, bestMatched, genresLeft, excludedSet);
    }

    /// <summary>
    /// 按意图的题材顺序，从一个片源的分类表里挑出对应分类（每个题材最多取一个，总数不超过 limit）。
    /// 分类名命中被排除题材的不取。
    /// </summary>
    public static IReadOnlyList<VideoSourceCategory> MatchCategories(
        IEnumerable<VideoSourceCategory> categories,
        SearchIntentMatch intent,
        int limit)
    {
        var all = categories.Where(category => !string.IsNullOrWhiteSpace(category.Name)).ToList();
        var picked = new List<VideoSourceCategory>();
        foreach (var genre in intent.Genres)
        {
            if (picked.Count >= limit)
            {
                break;
            }

            var match = all.FirstOrDefault(category =>
                NameHasGenre(category.Name, genre)
                && !intent.ExcludeGenres.Any(excluded => NameHasGenre(category.Name, excluded))
                && !picked.Contains(category));
            if (match is not null)
            {
                picked.Add(match);
            }
        }

        return picked;
    }

    /// <summary>界面提示文案，如：已按“阴天絮语”扩展检索：剧情 / 悬疑 / 爱情。</summary>
    public static string Describe(SearchIntentMatch intent) =>
        intent.Genres.Count == 0
            ? $"已识别为“{intent.Label}”"
            : $"已按“{intent.Label}”扩展检索：{string.Join(" / ", intent.Genres)}";

    private static bool NameHasGenre(string name, string genre) =>
        Aliases(genre).Any(alias => name.Contains(alias, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> Aliases(string genre) =>
        GenreAliases.TryGetValue(genre, out var aliases) ? aliases : [genre];

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? ""
            : new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
}
