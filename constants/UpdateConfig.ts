export const UPDATE_CONFIG = {
  // 自动检查更新
  AUTO_CHECK: true,

  // 检查更新间隔（毫秒）
  CHECK_INTERVAL: 12 * 60 * 60 * 1000, // 12小时

  // GitHub相关URL
  GITHUB_RAW_URL:
    `https://raw.githubusercontent.com/zo1223/OrionTV/refs/heads/github-public/package.json?t=${Date.now()}`,

  // 获取平台特定的下载URL
  getDownloadUrl(version: string): string {
    return `https://github.com/zo1223/OrionTV/releases/download/v${version}/OpaiTV.${version}.apk`;
  },

  // 是否显示更新日志
  SHOW_RELEASE_NOTES: true,

  // 是否允许跳过版本
  ALLOW_SKIP_VERSION: true,

  // 下载超时时间（毫秒）
  DOWNLOAD_TIMEOUT: 10 * 60 * 1000, // 10分钟

  // 是否在WIFI下自动下载
  AUTO_DOWNLOAD_ON_WIFI: false,

  // 更新通知设置
  NOTIFICATION: {
    ENABLED: true,
    TITLE: "OpaiTV 更新",
    DOWNLOADING_TEXT: "正在下载新版本...",
    DOWNLOAD_COMPLETE_TEXT: "下载完成，点击安装",
  },
};
