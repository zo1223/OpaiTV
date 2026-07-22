# 方案 A：本地 PotPlayer + 在线 LibVLC

## 行为

| 内容 | 播放方式 |
|------|----------|
| 在线 HTTP/HLS | Orion 内置 **LibVLC**（本窗口） |
| 本地文件（`file://`、本地下载 `merged.ts` 等） | 外部 **PotPlayer**（默认开启） |
| 未找到 PotPlayer 或开关关闭 | 本地也回退 **LibVLC** |

## 设置

- **本地文件用 PotPlayer 播放**：总开关（默认开）
- **PotPlayer 路径**：`PotPlayerMini64.exe`；可留空自动探测  
  默认探测包含：`E:\potPlayer\PotPlayerMini64.exe`

## 启动参数

```text
PotPlayerMini64.exe "C:\path\file.ts" /current /seek=hh:mm:ss.ms
```

- `/current`：尽量复用已打开实例  
- `/seek`：带上 Orion 续播进度  

## 代码

- `LocalPlayback/PotPlayerLauncher.cs`：路径探测与启动  
- `Views/PlayerView.xaml.cs`：播集时本地分流  
- `Settings` / `SettingsView`：开关与路径持久化  

## 说明

- 本地片会打开/激活 PotPlayer 窗口（非窗口内嵌）。  
- 在线片不受影响，仍在 Orion 内播放。  
- 不依赖 mpv，不逆向 PotPlayer 私有 DLL。
