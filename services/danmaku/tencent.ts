/**
 * 内置弹幕来源:腾讯视频公开接口(搜索 → 分集 → 弹幕分片),不需要登录和服务器。
 * 接口与字段参考 huangxd-/danmu_api 的 tencent 源。
 */
import { DANMAKU_USER_AGENT, DanmakuHttp } from "./http";
import {
  isExtraEpisodeTitle,
  parseEpisodeNumber,
  parseTitle,
  pickBestCandidate,
  pickEpisodeIndex,
  TitleCandidate,
} from "./title";
import { DanmakuComment, DanmakuMode, DanmakuProvider, DanmakuQuery, DanmakuTrack } from "./types";

const SEARCH_URL =
  "https://pbaccess.video.qq.com/trpc.videosearch.mobile_search.MultiTerminalSearch/MbSearch?vplatform=2";
const EPISODES_URL =
  "https://pbaccess.video.qq.com/trpc.universal_backend_service.page_server_rpc.PageServer/GetPageData?video_appid=3000010&vversion_name=8.2.96&vversion_platform=2";
const DANMAKU_BASE_URL = "https://dm.video.qq.com/barrage";
const ALLOWED_TYPES = ["电视剧", "动漫", "电影", "纪录片", "综艺", "综艺节目", "少儿"];
const DEFAULT_SEGMENT_MS = 30000;

export interface TencentCandidate extends TitleCandidate {
  cid: string;
}

interface TencentEpisode {
  vid: string;
  number: number | null;
  title: string;
}

interface TencentTab {
  begin: number;
  end: number;
  pageContext: string;
}

interface TencentSeries {
  cid: string;
  tabs: TencentTab[];
  /** 第一次请求直接带回的分集(没有分页时就是全部) */
  firstPage: TencentEpisode[];
  pages: Map<string, Promise<TencentEpisode[]>>;
}

const stripTags = (text: unknown) => String(text ?? "").replace(/<[^>]+>/g, "").trim();

export function parseTencentSearch(data: any): TencentCandidate[] {
  const root = data?.data;
  if (!root) return [];
  const items: any[] = [];
  for (const box of root.areaBoxList || []) {
    if (box?.boxId === "MainNeed" && Array.isArray(box.itemList)) items.push(...box.itemList);
  }
  if (Array.isArray(root.normalList?.itemList)) items.push(...root.normalList.itemList);

  const seen = new Set<string>();
  const result: TencentCandidate[] = [];
  for (const item of items) {
    const info = item?.videoInfo;
    const cid = item?.doc?.id;
    if (!info || !cid || seen.has(cid)) continue;
    if (!info.year) continue; // 没有年份的都是短视频、合集
    if (info.subTitle === "全网搜" || info.playFlag === 2) continue;
    const typeName = String(info.typeName || "");
    if (!ALLOWED_TYPES.includes(typeName)) continue;
    const sites: any[] = [...(info.playSites || []), ...(info.episodeSites || [])];
    if (sites.length > 0 && !sites.some((site) => site?.enName === "qq")) continue;
    const title = stripTags(info.title);
    if (!title) continue;
    seen.add(cid);
    result.push({
      cid,
      title,
      year: info.year,
      isMovie: typeName === "电影",
      episodeCount: typeName === "电影" ? 1 : Number(info.subjectDoc?.videoNum) || 0,
    });
  }
  return result;
}

export function parseTencentEpisodePage(data: any): { tabs: TencentTab[]; episodes: TencentEpisode[] } {
  const tabs: TencentTab[] = [];
  const episodes: TencentEpisode[] = [];
  const seen = new Set<string>();
  for (const list of data?.data?.module_list_datas || []) {
    for (const module of list?.module_datas || []) {
      const rawTabs = module?.module_params?.tabs;
      if (rawTabs && tabs.length === 0) {
        try {
          for (const tab of JSON.parse(rawTabs)) {
            if (tab?.page_context) {
              tabs.push({ begin: Number(tab.begin) || 0, end: Number(tab.end) || 0, pageContext: tab.page_context });
            }
          }
        } catch {
          // 分页信息解析失败时按单页处理
        }
      }
      for (const item of module?.item_data_lists?.item_datas || []) {
        const params = item?.item_params;
        if (!params?.vid || params.is_trailer === "1" || seen.has(params.vid)) continue;
        seen.add(params.vid);
        episodes.push({
          vid: params.vid,
          number: parseEpisodeNumber(params.title),
          title: String(params.play_title || params.union_title || params.title || ""),
        });
      }
    }
  }
  // 正片和花絮混在一起时只留正片
  const main = episodes.filter((episode) => !isExtraEpisodeTitle(episode.title));
  return { tabs, episodes: main.length > 0 ? main : episodes };
}

export function parseTencentSegment(data: any): DanmakuComment[] {
  const result: DanmakuComment[] = [];
  for (const item of data?.barrage_list || []) {
    const text = String(item?.content ?? "").trim();
    const time = Number(item?.time_offset) / 1000;
    if (!text || !Number.isFinite(time) || time < 0) continue;
    let color = 0xffffff;
    let mode: DanmakuMode = "scroll";
    if (item.content_style) {
      try {
        const style = typeof item.content_style === "string" ? JSON.parse(item.content_style) : item.content_style;
        const raw = style?.gradient_colors?.[0] || (style?.color && style.color !== "ffffff" ? style.color : "");
        const parsed = raw ? parseInt(String(raw).replace("#", ""), 16) : NaN;
        if (Number.isFinite(parsed)) color = parsed & 0xffffff;
        if (style?.position === 2) mode = "top";
        else if (style?.position === 3) mode = "bottom";
      } catch {
        // 样式解析失败用默认白色滚动
      }
    }
    result.push({ time, text, color, mode });
  }
  return spreadWithinSecond(result);
}

/**
 * 腾讯的弹幕时间只精确到秒,同一秒常有十几二十条。原样显示会每秒整排同时冒出来,
 * 所以把同一整秒内的弹幕按原顺序均匀摊到这一秒里。时间本来就带小数的不动。
 */
export function spreadWithinSecond(comments: DanmakuComment[]): DanmakuComment[] {
  const counts = new Map<number, number>();
  for (const comment of comments) {
    if (Number.isInteger(comment.time)) counts.set(comment.time, (counts.get(comment.time) || 0) + 1);
  }
  const used = new Map<number, number>();
  return comments.map((comment) => {
    const total = counts.get(comment.time);
    if (!total || total < 2) return comment;
    const order = used.get(comment.time) || 0;
    used.set(comment.time, order + 1);
    return order === 0 ? comment : { ...comment, time: comment.time + order / total };
  });
}

export function createTencentProvider(http: DanmakuHttp): DanmakuProvider {
  const seriesCache = new Map<string, Promise<TencentSeries | null>>();

  const jsonHeaders = (referer: string) => ({
    Origin: "https://v.qq.com",
    Referer: referer,
    "User-Agent": DANMAKU_USER_AGENT,
    Accept: "application/json, text/plain, */*",
  });

  const search = async (keyword: string): Promise<TencentCandidate[]> => {
    const data = await http.postJson(
      SEARCH_URL,
      {
        version: "25071701",
        clientType: 1,
        filterValue: "",
        uuid: "0379274D-05A0-4EB6-A89C-878C9A460426",
        query: keyword,
        retry: 0,
        pagenum: 0,
        isPrefetch: true,
        pagesize: 30,
        queryFrom: 0,
        searchDatakey: "",
        transInfo: "",
        isneedQc: true,
        preQid: "",
        adClientInfo: "",
        extraInfo: { multi_terminal_pc: "1", themeType: "1", sugRelatedIds: "{}", appVersion: "" },
      },
      jsonHeaders("https://v.qq.com/")
    );
    return data?.ret === 0 ? parseTencentSearch(data) : [];
  };

  const fetchEpisodePage = (cid: string, pageContext: string) =>
    http.postJson(
      EPISODES_URL,
      {
        has_cache: 1,
        page_params: {
          req_from: "web_vsite",
          page_id: "vsite_episode_list",
          page_type: "detail_operation",
          id_type: "1",
          page_size: "",
          cid,
          vid: "",
          lid: "",
          page_num: "",
          page_context: pageContext,
          detail_page_type: "1",
        },
      },
      jsonHeaders(`https://v.qq.com/x/cover/${cid}.html`)
    );

  const findSeries = async (query: DanmakuQuery): Promise<TencentSeries | null> => {
    const parsed = parseTitle(query.title);
    const keywords = parsed.baseText && parsed.baseText !== parsed.cleaned ? [parsed.cleaned, parsed.baseText] : [parsed.cleaned];
    for (const keyword of keywords) {
      if (!keyword) continue;
      const candidates = await search(keyword);
      const best = pickBestCandidate(parsed, candidates, query);
      if (best < 0) continue;
      const cid = candidates[best].cid;
      const first = parseTencentEpisodePage(
        await fetchEpisodePage(cid, `cid=${cid}&detail_page_type=1&req_from=web_vsite&req_from_second_type=&req_type=0`)
      );
      return { cid, tabs: first.tabs, firstPage: first.episodes, pages: new Map() };
    }
    return null;
  };

  const loadTab = (series: TencentSeries, tab: TencentTab): Promise<TencentEpisode[]> => {
    let page = series.pages.get(tab.pageContext);
    if (!page) {
      page = fetchEpisodePage(series.cid, tab.pageContext).then((data) => parseTencentEpisodePage(data).episodes);
      series.pages.set(tab.pageContext, page);
      page.catch(() => series.pages.delete(tab.pageContext));
    }
    return page;
  };

  const findEpisode = async (series: TencentSeries, query: DanmakuQuery): Promise<TencentEpisode | null> => {
    const pick = (episodes: TencentEpisode[], episodeIndex: number) => {
      const index = pickEpisodeIndex(
        episodes.map((episode) => episode.number),
        { ...query, episodeIndex }
      );
      return index >= 0 ? episodes[index] : null;
    };

    if (series.tabs.length <= 1) {
      return pick(series.firstPage, query.episodeIndex);
    }
    // 分页的长剧:只取当前集所在的那一页
    const tab = series.tabs.find((t) => query.episodeNumber >= t.begin && query.episodeNumber <= t.end);
    if (!tab) return null;
    return pick(await loadTab(series, tab), query.episodeNumber - tab.begin);
  };

  return {
    name: "tencent",
    async resolve(query) {
      const key = `${parseTitle(query.title).normalized}|${query.year || ""}|${query.episodeCount <= 1 ? "m" : "s"}`;
      let pending = seriesCache.get(key);
      if (!pending) {
        pending = findSeries(query);
        seriesCache.set(key, pending);
        if (seriesCache.size > 20) {
          seriesCache.delete(seriesCache.keys().next().value as string);
        }
        // 失败(网络错误)不缓存,下次进入播放页重试
        pending.catch(() => seriesCache.delete(key));
      }
      const series = await pending;
      if (!series) return null;
      const episode = await findEpisode(series, query);
      if (!episode) return null;

      const base = await http.getJson(`${DANMAKU_BASE_URL}/base/${episode.vid}`, { "User-Agent": DANMAKU_USER_AGENT });
      const segments = Object.values(base?.segment_index || {}) as { segment_start?: string; segment_name?: string }[];
      if (segments.length === 0) return null;
      const spanMs = Number(base?.segment_span) > 0 ? Number(base.segment_span) : DEFAULT_SEGMENT_MS;
      const names = new Map<number, string>();
      for (const segment of segments) {
        if (segment?.segment_name) names.set(Math.round(Number(segment.segment_start) / spanMs), segment.segment_name);
      }

      const track: DanmakuTrack = {
        provider: "tencent",
        title: episode.title,
        segmentSeconds: spanMs / 1000,
        async loadSegment(index) {
          const name = names.get(index);
          if (!name) return [];
          const data = await http.getJson(`${DANMAKU_BASE_URL}/segment/${episode.vid}/${name}`, {
            "User-Agent": DANMAKU_USER_AGENT,
          });
          return parseTencentSegment(data);
        },
      };
      return track;
    },
  };
}
