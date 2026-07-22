# OpaiTV

OpaiTV 是一个跨平台视频播放器项目，包含 React Native TV/Android 客户端和 Windows 桌面客户端。项目提供播放器、搜索、收藏、播放记录、直播 M3U、本地视频和用户自定义 AppleCMS/MacCMS 订阅等通用能力。

## 公开仓库说明

本仓库是无内置视频源的公开版本：

- 不包含、托管、维护或推荐任何视频源、直播列表或站点接口。
- 应用首次启动时，视频订阅地址和 M3U 地址均为空。
- 不包含抓源代理、站点适配代理或其运行时。
- 测试中的 `.example` 地址是 IANA 保留的示例域名，不会作为实际内容源使用。

用户可以在应用设置中填写自己管理的兼容服务。仓库维护者不会通过 Issue、Pull Request 或 Release 分发源配置。

## 平台

- React Native TV/Android：Expo、React Native TVOS、TypeScript、Zustand
- Windows：WPF、.NET 8、LibVLCSharp

## 下载

Windows 自包含版本发布在 [GitHub Releases](https://github.com/zo1223/OrionTV/releases)。下载 `OpaiTV-*-win-x64-self-contained.zip`，解压后运行 `OpaiTV.exe`，无需另外安装 .NET。

## 环境要求

- Node.js 18 或更高版本
- Yarn 1.22
- Android Studio 和 JDK 17（Android 构建）
- Xcode（Apple TV 构建）
- .NET 8 SDK（Windows 构建）

## React Native 开发

```sh
yarn install --frozen-lockfile
yarn typecheck
yarn lint
yarn test-ci
yarn start
```

生成并运行原生项目：

```sh
yarn prebuild
yarn android
# 或
yarn ios
```

## Windows 开发

```powershell
dotnet build desktop\OrionTV.Desktop.sln
dotnet test desktop\OrionTV.Desktop.sln
dotnet run --project desktop\OrionTV.Desktop.App\OrionTV.Desktop.App.csproj
```

Windows 自包含发布：

```powershell
dotnet publish desktop\OrionTV.Desktop.App\OrionTV.Desktop.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -o desktop\publish\win-x64-self-contained
```

## 配置

两端默认均不携带视频订阅或直播地址。运行应用后，在设置页配置：

- 视频源订阅：兼容项目支持的 AppleCMS/MacCMS JSON 或 XML 格式
- 直播源：用户自行管理的 M3U 地址
- 服务端地址：可选的自建后端

Windows 端还可以通过 AI 工具窗口启动本机已安装的 Claude Code 或 Codex。CLI 会先读取随应用发布的 `desktop/docs/ORION_SOURCE_GUIDE.md`，并把验证后的兼容配置写入用户数据目录；仓库本身不会接收或提交这些本地配置。

## 公开树审计

提交前运行：

```sh
yarn audit:public
```

该检查会拒绝已知代理目录、内置源标识、未审核的真实视频接口 URL、常见凭据格式和超过 GitHub 单文件限制的内容。Pull Request 和 `github-public` 分支推送也会自动执行同一检查。

## 贡献

请先阅读 [CONTRIBUTING.md](CONTRIBUTING.md)。功能修复和通用协议改进可以提交，但请勿在代码、测试、文档、日志或截图中加入真实视频源。

## 许可证

本项目采用 [MIT License](LICENSE)。第三方依赖仍分别适用其自身许可证。

## 免责声明

OpaiTV 只提供通用客户端代码，不存储、上传或分发视频内容。使用者应确保其配置和访问的内容符合适用法律及服务条款。
