# 便携 mpv 内嵌本地播放（方式 A，不改 PATH）

## 做什么

- **在线**：LibVLC（本窗口）
- **本地**（`merged.ts` / `file://`）：**便携 mpv 内嵌**（FFmpeg demux + d3d11va 硬解）
- **不需要**把 mpv 加入系统 PATH
- mpv 不可用时自动回退 LibVLC

## 便携目录

构建/发布后应存在：

```text
…/OrionTV.Desktop.App/bin/…/mpv/mpv.exe
…/desktop/publish/win-x64-self-contained/mpv/mpv.exe
```

源文件在仓库：

```text
desktop/tools/mpv-extract/   （绿色版解压结果，csproj 会复制到输出 mpv\）
```

当前便携包约 **118 MB**（mpv.exe ~112 MB）。

## 用户侧（方式 A）

若自己准备绿色版：

1. 下载 Windows 便携 mpv  
2. 放到：

```text
desktop\publish\win-x64-self-contained\mpv\mpv.exe
```

3. 重启 Orion，播本地 `merged.ts`

本仓库发布时会自动带上 `desktop/tools/mpv-extract` 内容，一般**不用再手动拷**。

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
