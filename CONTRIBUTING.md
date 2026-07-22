# Contributing

感谢参与 OrionTV 开发。公开仓库只接受不包含视频源的通用功能和修复。

## 提交要求

- 不提交真实视频接口、订阅地址、M3U 列表、播放地址或站点凭据。
- 不提交抓源代理、站点专用解析器及其运行时文件。
- 协议测试必须使用 `.example`、`example.com`、本机回环地址或进程内测试服务器。
- 不要把本机 `settings.json`、`extra_sources.json`、日志、缓存、截图或发布产物加入 Git。
- 修改源加载逻辑时，必须保持首次启动的订阅地址和 M3U 地址为空。

## 验证

```sh
yarn audit:public
yarn typecheck
yarn lint
yarn test-ci
dotnet test desktop\OrionTV.Desktop.sln
```

公开分支从内部开发线同步功能时，应选择性移植与功能有关的文件，检查差异后重新运行上述审计。不要直接合并包含私有配置或代理历史的分支。
