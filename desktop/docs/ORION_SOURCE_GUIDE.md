# OpaiTV 桌面端 AI 源站适配指南

> 契约版本：1.5
>
> 本文件随桌面发布包内置，是 OpaiTV 的源适配契约。每次启动新的 Claude Code / Codex 会话，第一项操作必须完整读取本文件；不要只依赖提示词中的摘要。

## 0. 执行要点

1. 先用文件读取工具完整读取本文件，读取完成后第一条进度消息明确报告已读完 `ORION_SOURCE_GUIDE.md`。
2. 当前任务提示词中的绝对路径和目标网址是本次任务的输入，读取后直接执行。
3. 桌面端配置结果采用 AppleCMS / 类 MacCMS JSON / MacCMS XML；TVBox `csp`、`jar`、`drpy` 等配置没有对应执行器。
4. 只有经过“接口结构 + 详情数据 + 可解析播放地址”三步验证的源才算可用，只有可用源才允许写入。
5. AI 找到的候选源网络条件未知，必须本地网络直连优先；只有直连因网络原因不可用、且代理完成同样的三步验证后，才允许按代理路径配置。
6. 配置结果写入用户数据目录中的 `extra_sources.json`，并合并保留已有源和无关设置。
7. 找不到真实可用源时，明确报告搜索和验证结果；配置文件只收录经过验证的源。

## 1. 产品和文件位置

OpaiTV 桌面端（WPF）是订阅模式影视播放器：

- 点播：AppleCMS 兼容 JSON、MacCMS JSON/XML（订阅源 + `extra_sources.json`）
- 直播：M3U
- 本地视频：本机文件
- 无 TVBox 代理、csp/jar/drpy 执行器

发布包典型结构：

```
OpaiTV.exe
docs/
  ORION_SOURCE_GUIDE.md
```

用户数据目录（可写；为兼容旧版保留原目录名）：

```
%AppData%\OrionTV.Desktop\
  settings.json
  extra_sources.json
  ai_prompts\
```

提示词给出的绝对路径优先于示例路径；本地 `extra_sources.json` 使用普通 UTF-8 JSON，而不是 Base58 或订阅链接编码。

## 2. 支持范围和协议

唯一接入线是当前机器能实际访问并提供以下接口的 HTTP/HTTPS 服务：

| `protocol` | 适用形态 | 必要说明 |
| --- | --- | --- |
| `applecms` | AppleCMS 兼容 JSON | `ac` 参数按第 3 节执行 |
| `maccms_json` | MacCMS 类 JSON | 可用 `options` 调整各接口的 `ac` 值 |
| `maccms_xml` | MacCMS 经典 XML | 请求带 `at=xml`，响应含可解析的视频节点 |

桌面端无法解析：

- TVBox `sites` + spider jar、`csp_*`、type3/DEX/ftyguard
- 依赖 TVBox 宿主的纯 drpy 脚本

如果目标只有这些形态，说明桌面端无法接入，保持原始类型即可。

### 网络路径优先级（硬性）

- AI 找到的候选源网络条件未知，不得按站名预设直连或代理。每个候选都必须先验证本地网络直连，再决定是否需要代理。
- 第一阶段必须绕过 Windows 系统代理以及 `HTTP_PROXY`、`HTTPS_PROXY`、`ALL_PROXY` 等代理配置；使用 curl 时执行 `curl --noproxy '*' ...`，其它工具必须采用等价的禁用代理方式，依次验证 API、详情和至少一个播放地址。
- 直连三步验证全部通过时，必须采用直连源站 API，不得为了统一配置或提高成功率而改走代理。
- 只有直连因连接失败、超时、DNS、地域限制或访问限制等网络原因不能完成验证时，才进入代理阶段。接口结构错误、没有真实数据、没有播放地址等内容或协议问题不属于代理回退条件。
- 代理阶段必须使用当前机器已经配置或用户明确提供的代理，重新完成 API、详情和播放地址三步验证；只有三步全部通过，才允许把该候选作为“代理必需源”写入并在汇报中明确标记。代理仍失败就跳过。
- 使用 Windows 系统代理时，`extra_sources.json` 仍写源站 API，不得虚构客户端不识别的 `proxy` 字段。若使用带 `?url=` 的兼容代理 API，必须验证包装后的完整请求链后再写入完整包装 URL。

## 3. 实际请求矩阵

所有查询参数必须正确 URL 编码。下面的 `{api}` 是配置中的 `api`，`{id}` 是接口返回的真实视频 ID。

### AppleCMS JSON

| 用途 | 请求 |
| --- | --- |
| 分类 | `{api}?ac=list` |
| 搜索 | `{api}?ac=detail&wd={keyword}` |
| 按 ID 取详情和剧集 | `{api}?ac=detail&ids={id}` |
| 最新/分页 | `{api}?ac=detail&pg={page}` |
| 分类分页 | `{api}?ac=detail&t={type_id}&pg={page}` |

响应至少应能解析 JSON 根对象中的 `list` 数组；分类接口通常还提供 `class` 数组。

### MacCMS JSON

客户端识别的 `options` 键为：

- `searchAc`：搜索接口的 `ac`
- `detailAc`：按 ID 取详情的 `ac`
- `pageAc` 或 `listAc`：分页/分类列表的 `ac`
- `categoryAc`：分类接口的 `ac`

未配置时客户端有默认回退，但不同站点方言不一致。发现 `search_ac_list`、`videolist` 等方言时，必须把实际验证过的值写入 `dialect` 或 `options`，不要凭站点名称猜测。

### MacCMS XML

请求在对应 JSON 参数上增加 `at=xml`：

```
{api}?ac=list&at=xml
{api}?ac=list&at=xml&wd={keyword}
{api}?ac=detail&at=xml&ids={id}
{api}?ac=list&at=xml&t={type_id}&pg={page}
```

客户端从 `<class>`/`<ty>` 读取分类，从 `<video>` 读取视频，从 `<dd>` 或 `<dl>/<playurl>` 读取播放组。

如果 `api` 是带 `?url=` 的代理包装，保留完整包装 URL，不要自行解码、删除或改写它；客户端会在原 URL 上追加查询参数。新增此类地址前，必须先确认源站直连不可用，再对完整包装 URL 完成第 5 节的三步验证。

## 4. `extra_sources.json` 唯一格式

推荐写成以下普通 JSON：

```json
{
  "api_site": {
    "apple_demo": {
      "name": "AppleCMS 示例",
      "api": "https://example.com/api.php/provide/vod",
      "protocol": "applecms"
    },
    "mac_demo": {
      "name": "MacCMS JSON 示例",
      "api": "https://example.net/api.php/provide/vod",
      "protocol": "maccms_json",
      "dialect": "videolist",
      "options": {
        "searchAc": "list",
        "detailAc": "detail",
        "pageAc": "videolist",
        "categoryAc": "list"
      }
    }
  }
}
```

规则：

- `key`（例如 `apple_demo`）必须稳定、唯一，建议使用 ASCII 小写加下划线；不要每次搜索都生成随机 key。
- `name`、`api` 必填；`api` 必须是当前机器实际可访问的 HTTP/HTTPS API 地址。
- `protocol` 只能是 `applecms`、`maccms_json`、`maccms_xml`；AppleCMS 可省略该字段。
- `detail` 在该地址实际能接受 `ac=detail&ids=...` 并返回有效详情时填写；不确定时留空。
- `dialect`/`options` 使用客户端能够识别的字段；TVBox、jar、drpy 或其它执行字段不会被桌面端读取。
- 同 key 的额外源会覆盖订阅源；无关 key 必须保留。

## 5. 验证流程（写入前必须完成）

### A. 找到候选接口

解析用户指定 URL，或在“AI 寻源”模式下搜索候选站点。检查 HTML、JavaScript 和公开接口。

### B. 验证接口结构

对每个候选 API 实际发起请求并记录：

1. 先按“网络路径优先级”禁用代理验证；直连因网络原因失败时才用代理重试。最终采用的路径必须 HTTP 状态成功，响应不是错误页伪装的 HTML。
2. JSON 能解析且包含 `list`；XML 能解析且包含客户端需要的节点。
3. 列表中至少有一条真实记录，包含非空 `vod_id`/`id` 和 `vod_name`/`name`。
4. 如果分类是产品目标的一部分，分类接口能返回可用的 `class` 或 `<ty>`。

### C. 验证详情和播放地址

1. 使用列表中的真实 ID 请求详情，以详情结果而不是搜索元数据判定成功。
2. 详情返回至少一条影视记录，并提供非空播放字段（JSON 的 `vod_play_url`，或 XML 的 `<dd>`/`playurl`）。
3. 按播放器规则解析播放字段：`$$$` 分播放组，`#` 分剧集，`$` 前面可为剧集名称；至少得到一个绝对或可相对解析的 HTTP/HTTPS 播放地址。
4. `vod_play_from` 与播放组数量不一致时，记录实际情况；空组、占位地址、`javascript:`、`file:`、`magnet:` 等不可接受。
5. 必须以最终采用的同一网络路径请求至少一个播放地址或检查其响应头：直连通过的源继续直连；代理必需源的详情和播放地址也必须通过代理验证。

只有 A、B、C 都通过，才允许写入源。搜索接口没有播放地址时，必须继续按 ID 取详情；详情仍没有可解析地址，就跳过该源。

## 6. 写入流程

1. 读取现有 `extra_sources.json`，解析并按稳定 key 合并，保留已有源。
2. 只写用户数据目录，不修改 `settings.json` 的无关字段；主订阅为空时保持为空。
3. 使用 UTF-8、缩进 JSON，先写同目录临时文件再替换目标文件，避免桌面端读到半截内容。
4. 写入后重新解析文件，确认 `api_site`、key、协议和 URL 都有效。

## 7. AI 主动寻源模式

此模式不需要用户提供网址。AI 按用户配置的工具权限和搜索范围主动寻找候选，逐个按“直连优先、失败后才代理”的顺序完成第 5 节验证。直连通过就配置直连；直连因网络原因失败但代理三步验证全部通过时，可以配置并标记为代理必需源。无法确认接口或播放地址时如实记录结果。找不到技术合格源时只汇报结果，不生成猜测配置。

## 8. 汇报格式

完成后用中文汇报：

```
指南：已完整读取 ORION_SOURCE_GUIDE.md
任务：指定 URL 解析 或 AI 主动寻源
候选与验证：API、协议、直连命令与结果；如直连失败，再列代理方式、三步验证结果和最终网络路径
写入：extra_sources.json 的路径、写入/更新的 key
跳过：未写入候选及具体原因
下一步：回到 OpaiTV 点击“立即刷新源”
```

## 9. 桌面端兼容性说明

- `csp_*`、jar、drpy 不是桌面端可执行的 `provide/vod` 接口
- 写入 `extra_sources.json` 时按 key 合并保留已有数据
- 三步验证通过的地址才是播放器可用源

开始执行：先完整读取本文件，再按当前任务模式实际抓取或搜索，不要停留在计划说明。
