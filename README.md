# baobao-7799 自动电影镜头 / ETS2LA Auto Cinema

**Author: baobao-7799** · GPL-3.0-or-later · ETS2LA v3 (C#) plugin

开启自动驾驶后，长时间不碰键鼠就自动进入「电影镜头」：镜头像 GTA 电影视角一样缓缓摇镜 / 环绕 / 变焦，同时隐藏 ETS2LA 覆盖层 HUD。一动鼠标键盘立刻退出并把一切恢复原样。

## 功能

- 空闲检测：Windows `GetLastInputInfo`，真·检测键鼠输入，而不是只测游戏内操作。
- 自动驾驶检测（二选一或同时）：
  - **输出通道检测**：ETS2LA 里任何插件（ACC/车道保持/自动超车等）在发控制输出即视为自动驾驶开启；
  - **遥测推断**：车辆在动、没有玩家输入、方向盘/油门由游戏控制或巡航开启。
- 三种镜头模式：正弦摇镜 / GTA 式环绕 / 混合。
- 变焦脉冲：周期性推拉镜头，像电影运镜。
- 隐藏 ETS2LA 覆盖层（可选，另可发 `showhud` 脉冲尝试隐藏游戏内 HUD）。
- 用户一碰鼠标/键盘（宽限 3 秒防误触）立即退出并恢复覆盖层与 HUD，再进入冷却。
- 设置窗口可热调所有参数，1 秒节流自动保存。

## 安装

1. 复制 `AutoCinema.dll` 到 `%LOCALAPPDATA%\ETS2LA\current\Plugins\`（或用 `install.ps1`）。
2. 重启 ETS2LA → 设置 → 插件 → 启用「baobao-7799 自动电影镜头」。
3. 游戏内按住 **右 Alt** 打开插件窗口调参数。

## 前置条件

| 依赖 | 说明 |
| --- | --- |
| ETS2LA 本体 | v2026.11.0+ |
| ETS2LA Game Plugin（游戏内） | 相机轴输出走旧版 SCSControls 虚拟手柄 |
| scs-sdk-controller（SCSControls 虚拟手柄） | 同上，传送相机控制到游戏 |

## 设置参数（fishleia.autocinema.json）

| 参数 | 默认 | 说明 |
| --- | --- | --- |
| Enabled | true | 总开关 |
| RequireAutopilot | true | 要求检测到自动驾驶 |
| RequireMoving | true | 要求车辆行驶中 |
| HideOverlay | true | 隐藏 ETS2LA 覆盖层窗口 |
| HideGameHud | false | 额外发 showhud 脉冲隐藏游戏 HUD |
| IdleSeconds | 30 | 空闲多少秒进入 |
| AutoPilotConfirmSeconds | 3 | 自动驾驶需持续多久确认 |
| Mode | 0 | 0=摇镜 1=环绕 2=混合 |
| PanPeriodSeconds / PanAmplitude | 40 / 0.25 | 摇镜周期/幅度 |
| VerticalPeriodSeconds / VerticalAmplitude | 25 / 0.06 | 俯仰周期/幅度 |
| OrbitSpeed / OrbitFlipSeconds | 0.05 / 20 | 环绕速度/换向周期 |
| ZoomPulseIntervalSeconds / ZoomStrength | 12 / 0.25 | 变焦脉冲 |
| UserCooldownSeconds | 8 | 退出后冷却 |

## 原理

- 相机控制：通过 ETS2LA 官方 GameOutput 输出通道发布 `j_cam_lk_lr / j_cam_lk_ud / j_trzoom_in` 相机轴（无键盘注入、无内存修改）。
- 覆盖层隐藏：找到 GLFW 窗口 "ETS2LA Overlay" 后 `ShowWindow(SW_HIDE)`，退出时 `SW_SHOWNA` 恢复。
- 安全：任何时刻用户输入即接管退出；释放通道时先发零值再删除通道，避免残留输出。

## 构建（无需 .NET SDK）

`build.ps1` 会从 ETS2LA 安装目录取 Roslyn 编译器，用 .NET 10 运行时做 bootstrap 编译。装好 .NET 10 运行时后直接 `powershell -ExecutionPolicy Bypass -File .\build.ps1`。

## License

GPL-3.0-or-later，见 LICENSE。
