import { api } from "./api";
import { loadRecommendationPool } from "./recommendationPool";
import { isSourceOff } from "./sourceMode";

/**
 * 详情页“相关推荐”用的候选:本机候选池(首页加载过的条目)按与首页本地推荐相同的两条规则筛一遍
 * ——成人内容按 18+ 开关(规则在 api.isAdultItemHidden 里,这里只是照用)、模式为“关闭”的片源不出现。
 * 只读本机数据,不发任何请求。单独成一个文件是为了让详情页不必加载整个首页 store。
 */
export const loadVisibleRecommendationCandidates = async () => {
  const pool = await loadRecommendationPool();
  const modes = api.getSourceModes();
  return pool.filter((item) => !api.isAdultItemHidden(item) && !isSourceOff(modes, item.source));
};
