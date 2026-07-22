# 便携 mpv 内嵌本地播放（方式 A，不改 PATH）

## 做什么

- **在线**：LibVLC（本窗口）
- **本地**（`merged.ts` / `file://`）：**便携 mpv 内嵌**（FFmpeg demux + d3d11va 硬解）
- **不需要**把 mpv 加入系统 PATH
- mpv 不可用时自动回退 LibVLC

## 便携目录

本机存在 `desktop/tools/mpv-extract` 时，构建/发布后会包含：

```text
…/OrionTV.Desktop.App/bin/…/mpv/mpv.exe
…/desktop/publish/win-x64-self-contained/mpv/mpv.exe
```

可选源文件目录：

```text
desktop/tools/mpv-extract/   （本地放置绿色版解压结果，csproj 会复制到输出 mpv\）
```

公开仓库和标准 Release 不捆绑第三方 mpv 二进制；未提供时自动回退 LibVLC。

## 用户侧（方式 A）

若自己准备绿色版：

1. 下载 Windows 便携 mpv  
2. 放到：

```text
desktop\publish\win-x64-self-contained\mpv\mpv.exe
```

3. 重启 OpaiTV，播本地 `merged.ts`

本地构建时若已准备 `desktop/tools/mpv-extract`，发布命令会自动复制其内容。

## 设置

- 「本地文件用 PotPlayer 播放」**默认关闭**  
- 仅当你想外启 PotPlayer 时再打开  

## 代码

- `LocalPlayback/MpvHost.cs` — 嵌入 HWND  
- `LocalPlayback/MpvLocalEngine.cs` — 进程 + IPC  
- `PlayerView` — 本地/在线分流  
- `OrionTV.Desktop.App.csproj` — Content 复制便携 mpv  

## 说明

- 本地模式开启 mpv OSC，避免 HwndHost airspace 挡 WPF 控制条  
- 硬解：`--hwdec=d3d11va`（不强制软解）  
- seek：`absolute+keyframes` 快速关键帧跳转  
