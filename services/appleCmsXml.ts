// AppleCMS / MacCMS 经典 XML 接口(TVBox type=0)的解析。
// 把 <rss><list><video>…</video></list><class><ty/></class></rss> 转成与 JSON 接口相同的字段形状
// (class / list / page / pagecount / total),后续的映射、筛选、翻页逻辑与 JSON 源共用。

interface XmlNode {
  name: string;
  attrs: Record<string, string>;
  children: XmlNode[];
  text: string;
}

export interface AppleCmsXmlData {
  class: { type_id: string; type_name: string }[];
  list: Record<string, string>[];
  page?: number;
  pagecount?: number;
  limit?: number;
  total?: number;
}

const NAMED_ENTITIES: Record<string, string> = { amp: "&", lt: "<", gt: ">", quot: '"', apos: "'", nbsp: " " };

const decodeEntities = (value: string) =>
  value.replace(/&(#x[0-9a-f]+|#\d+|[a-z]+);/gi, (match, entity: string) => {
    if (entity[0] !== "#") {
      return NAMED_ENTITIES[entity.toLowerCase()] ?? match;
    }
    const code = /^#x/i.test(entity) ? parseInt(entity.slice(2), 16) : parseInt(entity.slice(1), 10);
    return code > 0 && code <= 0x10ffff ? String.fromCodePoint(code) : match;
  });

// CDATA | 注释 | 声明(<?xml ?>、<!DOCTYPE>) | 结束标签 | 开始标签(含属性) | 文本
const XML_TOKEN =
  /<!\[CDATA\[([\s\S]*?)\]\]>|<!--[\s\S]*?-->|<[?!][^>]*>|<\/\s*([^\s>]+)\s*>|<([^\s/>!?]+)((?:"[^"]*"|'[^']*'|[^>"'])*)>|([^<]+)/g;
const XML_ATTR = /([^\s=/]+)\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'>/]+))/g;

const parseAttrs = (source: string) => {
  const attrs: Record<string, string> = {};
  for (const match of source.matchAll(XML_ATTR)) {
    attrs[match[1].toLowerCase()] = decodeEntities(match[2] ?? match[3] ?? match[4] ?? "");
  }
  return attrs;
};

// 宽容解析:不校验文档合法性,未闭合或多余的标签直接忽略,只取得元素树、属性和文本
const parseXml = (xml: string): XmlNode => {
  const root: XmlNode = { name: "", attrs: {}, children: [], text: "" };
  const stack = [root];

  for (const match of xml.matchAll(XML_TOKEN)) {
    const current = stack[stack.length - 1];
    if (match[1] !== undefined) {
      current.text += match[1];
    } else if (match[2] !== undefined) {
      const name = match[2].toLowerCase();
      for (let i = stack.length - 1; i > 0; i--) {
        if (stack[i].name === name) {
          stack.length = i;
          break;
        }
      }
    } else if (match[3] !== undefined) {
      const rest = match[4] || "";
      const node: XmlNode = { name: match[3].toLowerCase(), attrs: parseAttrs(rest), children: [], text: "" };
      current.children.push(node);
      if (!/\/\s*$/.test(rest)) {
        stack.push(node);
      }
    } else if (match[5] !== undefined) {
      current.text += decodeEntities(match[5]);
    }
  }

  return root;
};

const findAll = (node: XmlNode, name: string, found: XmlNode[] = []) => {
  for (const child of node.children) {
    if (child.name === name) {
      found.push(child);
    }
    findAll(child, name, found);
  }
  return found;
};

const childText = (node: XmlNode, names: string[]) => {
  for (const name of names) {
    const text = node.children.find((child) => child.name === name)?.text.trim();
    if (text) {
      return text;
    }
  }
  return "";
};

// JSON 字段名 -> XML 标签名。首项是标准写法,其余是少数站点的变体(直接沿用 JSON 字段名等)
const VIDEO_FIELD_TAGS: Record<string, string[]> = {
  vod_id: ["id", "vod_id"],
  vod_name: ["name", "vod_name", "title"],
  vod_pic: ["pic", "vod_pic", "img"],
  type_id: ["tid", "type_id"],
  type_name: ["type", "type_name"],
  vod_remarks: ["note", "vod_remarks", "remarks"],
  vod_year: ["year", "vod_year"],
  vod_area: ["area", "vod_area"],
  vod_lang: ["lang", "vod_lang"],
  vod_actor: ["actor", "vod_actor"],
  vod_director: ["director", "vod_director"],
  vod_content: ["des", "vod_content", "content"],
  vod_time: ["last", "vod_time"],
};

const mapVideo = (video: XmlNode) => {
  const item: Record<string, string> = {};
  for (const [field, tags] of Object.entries(VIDEO_FIELD_TAGS)) {
    const text = childText(video, tags);
    if (text) {
      item[field] = text;
    }
  }

  // <dl><dd flag="线路">第1集$地址#第2集$地址</dd>…</dl>:线路名和播放组取自同一批 dd,
  // 这样缺 flag 或内容为空的 dd 不会让后面的线路名错位
  const groups = findAll(video, "dd")
    .map((dd) => ({ flag: (dd.attrs.flag || "").trim(), urls: dd.text.trim() }))
    .filter((group) => group.urls);
  if (groups.length > 0) {
    item.vod_play_from = groups.map((group) => group.flag).join("$$$");
    item.vod_play_url = groups.map((group) => group.urls).join("$$$");
  } else {
    const playUrl = childText(video, ["dl", "vod_play_url", "playurl"]);
    const playFrom = childText(video, ["vod_play_from", "playfrom"]);
    if (playUrl) {
      item.vod_play_url = playUrl;
      if (playFrom) {
        item.vod_play_from = playFrom;
      }
    }
  }

  return item;
};

const toNumber = (value: string | undefined) => {
  const parsed = Number(value);
  return value !== undefined && value.trim() !== "" && Number.isFinite(parsed) ? parsed : undefined;
};

const hasCmsNode = (nodes: XmlNode[]) => nodes.some((node) => node.name === "list" || node.name === "class");

/**
 * 解析 AppleCMS XML 应答。不是 AppleCMS XML(JSON、HTML 错误页、其他 XML)时返回 null,
 * 由调用方按原来的 JSON 失败路径处理。
 */
export const parseAppleCmsXml = (text: string): AppleCmsXmlData | null => {
  const xml = text.replace(/^\uFEFF/, "").trim();
  if (!xml.startsWith("<") || /^<(?:!doctype\s+html|html)[\s>]/i.test(xml)) {
    return null;
  }

  const document = parseXml(xml);
  const top = document.children;
  if (!top.some((node) => node.name === "rss") && !hasCmsNode(top) && !top.some((node) => hasCmsNode(node.children))) {
    return null;
  }

  const paging = findAll(document, "list")[0]?.attrs ?? {};
  const page = toNumber(paging.page);
  const pagecount = toNumber(paging.pagecount);
  const limit = toNumber(paging.pagesize);
  const total = toNumber(paging.recordcount);

  return {
    class: findAll(document, "ty")
      .map((ty) => ({ type_id: (ty.attrs.id || "").trim(), type_name: ty.text.trim() }))
      .filter((category) => category.type_id && category.type_name),
    list: findAll(document, "video").map(mapVideo),
    ...(page !== undefined ? { page } : {}),
    ...(pagecount !== undefined ? { pagecount } : {}),
    ...(limit !== undefined ? { limit } : {}),
    ...(total !== undefined ? { total } : {}),
  };
};
