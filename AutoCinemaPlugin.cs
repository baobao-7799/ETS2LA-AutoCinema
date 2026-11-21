// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 baobao-7799
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

using ETS2LA.Shared;
using ETS2LA.Logging;
using ETS2LA.Backend.Events;
using ETS2LA.Game.Output;
using ETS2LA.Game.Telemetry;
using ETS2LA.Settings;
using ETS2LA.Overlay;
using Hexa.NET.ImGui;

namespace FishLeia.AutoCinema;

public class AutoCinemaPlugin : Plugin
{
    // ---- Win32: idle detection + overlay hiding ----
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("kernel32.dll")]
    private static extern uint GetTickCount();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowW(string? lpClassName, string lpWindowName);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SwHide = 0;
    private const int SwShowNa = 8; // show without stealing focus

    private const string OverlayWindowTitle = "ETS2LA Overlay";
    private const string CameraChannelId = "fishleia.autocinema";
    private const string HudChannelId = "fishleia.autocinema.hud";
    private const string WindowTitle = "baobao-7799 自动电影镜头";
    private const string SettingsFile = "fishleia.autocinema.json";
    private const string OutputTopic = "ETS2LA.Game.Output.ControlEvent";

    private enum Mode { Monitoring, Cinematic, Cooldown }

    private readonly SettingsHandler _settingsHandler = new();
    private CinemaSettings _cfg = new();
    private volatile bool _settingsDirty;
    private DateTime _lastSave = DateTime.MinValue;

    private Mode _state = Mode.Monitoring;
    private DateTime _cooldownUntil = DateTime.MinValue;

    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private float _now;
    private float _lastUserAct = float.MinValue;   // 最近一次方向盘/踏板输入（秒）
    private bool _liiFailed;                        // GetLastInputInfo 是否失败
    private DateTime? _apSince;
    private bool _autopilotSustained;
    private float _idleSeconds;
    private float _speed;
    private readonly Stopwatch _cinemaT = new Stopwatch();
    private bool _overlayHidden;
    private bool _gameHudToggled;                   // 游戏 HUD 是否被本插件切走
    private bool _gameHudEverOn;                    // 是否曾经切过 HUD（退出时只回切一次）
    private bool _manualCinema;
    private string _status = "待机中";

    // 诊断量（UI 显示）
    private bool _channelActive;
    private bool _cruiseOn;
    private bool _gameAiOn;
    private bool _userActNow;

    private readonly ConcurrentDictionary<string, DateTime> _otherChannels = new();

    public override PluginInformation Info => new()
    {
        Id = "fishleia.autocinema",
        Name = "baobao-7799 自动电影镜头",
        Description = "长时间未操作电脑时自动切换电影镜头（类 GTA），并隐藏 ETS2LA 覆盖层；动键鼠/方向盘立刻恢复。",
        Version = "1.1.0",
        AuthorName = "baobao-7799",
        AppVersionSupport = new PluginVersionSupport { LowerBound = "2026.11.0" },
        Tags = new[] { "camera", "cinematic", "idle", "hud" },
    };

    public override float TickRate => 30.0f;

    public override void Init()
    {
        _cfg = _settingsHandler.Load<CinemaSettings>(SettingsFile) ?? new CinemaSettings();
        Logger.Info("AutoCinema initialized.");
    }

    public override void OnEnable()
    {
        base.OnEnable();
        try { Events.Current.Subscribe<ControlEvent>(OutputTopic, OnAnyControlEvent); }
        catch (Exception ex) { Logger.Error(ex.ToString(), "AutoCinema: subscribe failed."); }
        try
        {
            OverlayHandler.Current.RegisterWindow(new WindowDefinition
            {
                Title = WindowTitle,
                Width = 440,
                Height = 500,
                Alpha = 0.92f,
                Open = _cfg.ShowWindow,
            }, RenderUI);
        }
        catch (Exception ex) { Logger.Error(ex.ToString(), "AutoCinema: register window failed."); }
        _lastUserAct = _now;
    }

    public override void OnDisable()
    {
        try { Events.Current.Unsubscribe<ControlEvent>(OutputTopic, OnAnyControlEvent); } catch { }
        try { OverlayHandler.Current.UnregisterWindow(new WindowDefinition { Title = WindowTitle }); } catch { }
        ExitCinematicSafe();
        base.OnDisable();
    }

    public override void Shutdown()
    {
        ExitCinematicSafe();
    }

    private void OnAnyControlEvent(ControlEvent e)
    {
        try
        {
            if (e == null || e.ChannelDefinition == null) return;
            string id = e.ChannelDefinition.Id;
            if (id == CameraChannelId || id == HudChannelId) return;
            _otherChannels[id] = DateTime.UtcNow;
        }
        catch { }
    }

    private static float GetSystemIdleSeconds(out bool failed)
    {
        failed = false;
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii))
        {
            failed = true;
            return 0f;
        }
        return (GetTickCount() - lii.dwTime) / 1000f;
    }

    public override void Tick()
    {
        try
        {
            _now = (float)_sw.Elapsed.TotalSeconds;

            var td = GameTelemetry.Current.GetCurrentData();
            _speed = td.truckFloat.speed;

            // ---- 空闲：键鼠空闲 与 游戏输入空闲 取较小者（手柄/方向盘也算"人还在"）----
            float sysIdle = GetSystemIdleSeconds(out bool liiFailed);
            _liiFailed = liiFailed;
            float userAct = Math.Abs(td.truckFloat.userSteer)
                + td.truckFloat.userThrottle
                + td.truckFloat.userBrake
                + td.truckFloat.userClutch;
            _userActNow = userAct > 0.05f;
            if (_userActNow) _lastUserAct = _now;
            float teleIdle = _now - _lastUserAct;
            _idleSeconds = _liiFailed ? teleIdle : Math.Min(sysIdle, teleIdle);

            // ---- 自动驾驶检测（诊断用；RequireAutopilot 才强制要求）----
            DateTime now = DateTime.UtcNow;
            _channelActive = false;
            foreach (var kv in _otherChannels)
            {
                if ((now - kv.Value).TotalSeconds < 1.0) { _channelActive = true; break; }
            }
            _cruiseOn = td.truckBool.cruiseControl;
            _gameAiOn = Math.Abs(td.truckFloat.gameSteer) > 0.02f || td.truckFloat.gameThrottle > 0.05f;
            bool userRecent = (_now - _lastUserAct) < 1.5f;
            bool heuristic = td.truckBool.engineEnabled && _speed > _cfg.MinSpeed && !userRecent && (_gameAiOn || _cruiseOn);
            bool detected = (_cfg.UseChannelDetection && _channelActive) || (_cfg.UseTelemetryHeuristic && heuristic);

            if (detected)
            {
                if (!_apSince.HasValue) _apSince = now;
            }
            else
            {
                _apSince = null;
            }
            _autopilotSustained = _apSince.HasValue
                && (now - _apSince.Value).TotalSeconds >= _cfg.AutoPilotConfirmSeconds;

            switch (_state)
            {
                case Mode.Monitoring: TickMonitoring(td); break;
                case Mode.Cinematic: TickCinematic(td); break;
                case Mode.Cooldown:
                    if (now >= _cooldownUntil) { _state = Mode.Monitoring; _status = "待机中"; }
                    else _status = $"冷却中 {(_cooldownUntil - now).TotalSeconds:F0}s";
                    break;
            }

            // ---- 看门狗：只要不在电影镜头状态，覆盖层和游戏 HUD 必须恢复 ----
            if (_state != Mode.Cinematic)
            {
                if (_overlayHidden) HideOverlay(false);
                if (_gameHudToggled) ToggleGameHud(false);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex.ToString(), "AutoCinema tick error");
            // 出异常也不能把 HUD 留在藏起来的状态
            if (_overlayHidden) HideOverlay(false);
            if (_gameHudToggled) ToggleGameHud(false);
        }

        SaveIfDirty();
    }

    private void TickMonitoring(GameTelemetryData td)
    {
        if (!_cfg.Enabled)
        {
            _status = "已停用";
            return;
        }

        bool want = td.sdkActive
            && !td.paused
            && td.truckBool.engineEnabled
            && (!_cfg.RequireMoving || _speed > 1f)
            && (!_cfg.RequireAutopilot || _autopilotSustained)
            && (_manualCinema || _idleSeconds >= _cfg.IdleSeconds);

        if (want)
        {
            EnterCinematic();
        }
        else
        {
            string why = !td.sdkActive ? "等待游戏"
                : td.paused ? "游戏暂停"
                : !td.truckBool.engineEnabled ? "车辆未点火"
                : (_cfg.RequireMoving && _speed <= 1f) ? "车辆未行驶"
                : (_cfg.RequireAutopilot && !_autopilotSustained) ? "未检测到自动驾驶"
                : $"空闲 {_idleSeconds:F0}/{_cfg.IdleSeconds:F0}s";
            _status = "待机中 · " + why;
        }
    }

    private void TickCinematic(GameTelemetryData td)
    {
        bool userBack = _cinemaT.Elapsed.TotalSeconds > 3.0 && (_idleSeconds < 0.2f || _userActNow);
        bool exit = !_cfg.Enabled
            || !td.sdkActive
            || td.paused
            || !td.truckBool.engineEnabled
            || userBack
            || (_cfg.RequireAutopilot && !_autopilotSustained)
            || (_cfg.RequireMoving && _speed < 0.5f);

        if (exit)
        {
            ExitCinematic();
            return;
        }

        _status = "电影镜头中";
        PublishCameraFrame();
    }

    private void EnterCinematic()
    {
        _state = Mode.Cinematic;
        _cinemaT.Restart();
        _status = "电影镜头中";

        if (_cfg.HideOverlay) HideOverlay(true);
        if (_cfg.HideGameHud) ToggleGameHud(true);

        Logger.Info("AutoCinema: entered cinematic mode.");
    }

    private void ExitCinematic()
    {
        if (_state != Mode.Cinematic) return;

        _state = Mode.Cooldown;
        _cooldownUntil = DateTime.UtcNow.AddSeconds(_cfg.UserCooldownSeconds);
        _cinemaT.Stop();
        _manualCinema = false;
        _status = "冷却中";

        if (_overlayHidden) HideOverlay(false);
        if (_gameHudToggled) ToggleGameHud(false);
        ReleaseCamera();

        Logger.Info("AutoCinema: exited cinematic mode.");
    }

    private void ExitCinematicSafe()
    {
        if (_overlayHidden) HideOverlay(false);
        if (_gameHudToggled) ToggleGameHud(false);
        ReleaseCamera();
        _state = Mode.Monitoring;
        _manualCinema = false;
        _status = "待机中";
    }

    private void HideOverlay(bool hide)
    {
        try
        {
            IntPtr hwnd = FindWindowW(null, OverlayWindowTitle);
            if (hwnd == IntPtr.Zero)
            {
                _overlayHidden = false;
                Logger.Warn("AutoCinema: ETS2LA Overlay window not found.");
                return;
            }
            ShowWindow(hwnd, hide ? SwHide : SwShowNa);
            _overlayHidden = hide;
        }
        catch (Exception ex)
        {
            Logger.Error(ex.ToString(), "AutoCinema: hide overlay failed.");
        }
    }

    // 游戏 HUD 切换：Direct 持续按住 400ms（比 50ms 脉冲可靠，虚拟手柄轮询不会漏掉）
    private void ToggleGameHud(bool intoCinematic)
    {
        try
        {
            if (intoCinematic)
            {
                if (_gameHudToggled) return;
                _gameHudToggled = true;
                _gameHudEverOn = true;
                PublishHudHold();
            }
            else
            {
                if (!_gameHudToggled) return;
                _gameHudToggled = false;
                if (_gameHudEverOn) PublishHudHold(); // 再按一次切回原状态
            }
            _ = Task.Run(async () =>
            {
                await Task.Delay(400);
                PublishHudRelease();
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex.ToString(), "AutoCinema: game HUD toggle failed.");
        }
    }

    private void PublishHudHold()
    {
        Events.Current.Publish(OutputTopic, new ControlEvent
        {
            ChannelDefinition = new ControlChannelDefinition { Id = HudChannelId, Timeout = 0.5f },
            Properties = new ControlProperties { Weight = 1f, BooleanType = ControlBooleanType.Direct },
            Variables = new ControlVariables { showhud = true },
        });
    }

    private void PublishHudRelease()
    {
        // 先归零再删通道，防止 bool 残留在旧版 mmap
        Events.Current.Publish(OutputTopic, new ControlEvent
        {
            ChannelDefinition = new ControlChannelDefinition { Id = HudChannelId, Timeout = 0.5f },
            Properties = new ControlProperties { Weight = 1f, BooleanType = ControlBooleanType.Direct },
            Variables = new ControlVariables { showhud = false },
        });
        PublishRemove(HudChannelId);
    }

    private void PublishCameraFrame()
    {
        float t = (float)_cinemaT.Elapsed.TotalSeconds;
        float lr;
        float ud;

        if (_cfg.Mode == 1) // GTA orbit
        {
            int seg = (int)(t / _cfg.OrbitFlipSeconds);
            float dir = (seg % 2 == 0) ? 1f : -1f;
            lr = dir * _cfg.OrbitSpeed;
            ud = 0f;
        }
        else if (_cfg.Mode == 2) // mixed
        {
            int seg = (int)(t / _cfg.OrbitFlipSeconds);
            float dir = (seg % 2 == 0) ? 1f : -1f;
            lr = dir * _cfg.OrbitSpeed
                + (float)Math.Sin(2 * Math.PI * t / _cfg.PanPeriodSeconds) * _cfg.PanAmplitude * 0.5f;
            ud = (float)Math.Sin(2 * Math.PI * t / _cfg.VerticalPeriodSeconds + 1.3f) * _cfg.VerticalAmplitude;
        }
        else // pan
        {
            lr = (float)Math.Sin(2 * Math.PI * t / _cfg.PanPeriodSeconds) * _cfg.PanAmplitude;
            ud = (float)Math.Sin(2 * Math.PI * t / _cfg.VerticalPeriodSeconds + 1.3f) * _cfg.VerticalAmplitude;
        }

        float zoom = 0f;
        if (_cfg.ZoomPulseIntervalSeconds > 0.5f)
        {
            float p = t % _cfg.ZoomPulseIntervalSeconds;
            if (p < 0.8f)
                zoom = (float)Math.Sin(Math.PI * p / 0.8f) * _cfg.ZoomStrength;
        }

        lr = Math.Clamp(lr, -1f, 1f);
        ud = Math.Clamp(ud, -1f, 1f);
        zoom = Math.Clamp(zoom, -1f, 1f);

        Events.Current.Publish(OutputTopic, new ControlEvent
        {
            ChannelDefinition = new ControlChannelDefinition { Id = CameraChannelId, Timeout = 0.5f },
            Properties = new ControlProperties { Weight = 1f, BooleanType = ControlBooleanType.Direct },
            Variables = new ControlVariables
            {
                j_cam_lk_lr = lr,
                j_cam_lk_ud = ud,
                j_trzoom_in = zoom,
            },
        });
    }

    private void ReleaseCamera()
    {
        try
        {
            Events.Current.Publish(OutputTopic, new ControlEvent
            {
                ChannelDefinition = new ControlChannelDefinition { Id = CameraChannelId, Timeout = 0.5f },
                Properties = new ControlProperties { Weight = 1f },
                Variables = new ControlVariables
                {
                    j_cam_lk_lr = 0f,
                    j_cam_lk_ud = 0f,
                    j_trzoom_in = 0f,
                },
            });
            PublishRemove(CameraChannelId);
        }
        catch (Exception ex)
        {
            Logger.Error(ex.ToString(), "AutoCinema: release camera failed.");
        }
    }

    private void PublishRemove(string channelId)
    {
        try
        {
            Events.Current.Publish(OutputTopic, new ControlEvent
            {
                ChannelDefinition = new ControlChannelDefinition { Id = channelId },
                Properties = null!,
                Variables = null!,
            });
        }
        catch { }
    }

    private void SaveIfDirty()
    {
        if (!_settingsDirty) return;
        DateTime now = DateTime.UtcNow;
        if ((now - _lastSave).TotalSeconds < 1.0) return;
        _lastSave = now;
        _settingsDirty = false;
        _settingsHandler.Save(SettingsFile, _cfg);
    }

    private void RenderUI()
    {
        ImGui.TextColored(new Vector4(1f, 0.9f, 0.4f, 1f), _status);
        ImGui.Text($"空闲 {_idleSeconds:F1}s | 速度 {_speed * 3.6f:F0} km/h");
        if (_liiFailed)
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.3f, 1f), "键鼠输入检测失败，改用方向盘/踏板空闲判定");
        ImGui.Text($"通道输出:{(_channelActive ? "有" : "无")} 巡航:{(_cruiseOn ? "开" : "关")} AI驾驶:{(_gameAiOn ? "有" : "无")} 玩家输入:{(_userActNow ? "有" : "无")}");
        ImGui.Separator();

        ImGui.Text("镜头模式");
        if (ImGui.Button("摇镜(默认)")) { _cfg.Mode = 0; _settingsDirty = true; }
        ImGui.SameLine();
        if (ImGui.Button("GTA 环绕")) { _cfg.Mode = 1; _settingsDirty = true; }
        ImGui.SameLine();
        if (ImGui.Button("混合")) { _cfg.Mode = 2; _settingsDirty = true; }
        ImGui.Separator();

        if (ImGui.Checkbox("启用插件", ref _cfg.Enabled)) _settingsDirty = true;
        if (ImGui.Checkbox("要求自动驾驶开启", ref _cfg.RequireAutopilot)) _settingsDirty = true;
        if (ImGui.Checkbox("要求车辆行驶中", ref _cfg.RequireMoving)) _settingsDirty = true;
        if (ImGui.Checkbox("隐藏 ETS2LA 覆盖层", ref _cfg.HideOverlay)) _settingsDirty = true;
        if (ImGui.Checkbox("同时切换游戏 HUD", ref _cfg.HideGameHud)) _settingsDirty = true;
        if (ImGui.Checkbox("通道检测(诊断)", ref _cfg.UseChannelDetection)) _settingsDirty = true;
        if (ImGui.Checkbox("遥测推断(诊断)", ref _cfg.UseTelemetryHeuristic)) _settingsDirty = true;
        ImGui.Separator();

        if (ImGui.SliderFloat("进入空闲秒数", ref _cfg.IdleSeconds, 5f, 300f)) _settingsDirty = true;
        if (ImGui.SliderFloat("自动驾驶确认秒数", ref _cfg.AutoPilotConfirmSeconds, 1f, 30f)) _settingsDirty = true;
        if (ImGui.SliderFloat("推断最低车速 m/s", ref _cfg.MinSpeed, 1f, 20f)) _settingsDirty = true;
        if (ImGui.SliderFloat("摇镜周期(秒)", ref _cfg.PanPeriodSeconds, 10f, 120f)) _settingsDirty = true;
        if (ImGui.SliderFloat("摇镜幅度", ref _cfg.PanAmplitude, 0.02f, 1f)) _settingsDirty = true;
        if (ImGui.SliderFloat("俯仰周期(秒)", ref _cfg.VerticalPeriodSeconds, 10f, 60f)) _settingsDirty = true;
        if (ImGui.SliderFloat("俯仰幅度", ref _cfg.VerticalAmplitude, 0f, 0.5f)) _settingsDirty = true;
        if (ImGui.SliderFloat("环绕速度", ref _cfg.OrbitSpeed, 0.01f, 0.3f)) _settingsDirty = true;
        if (ImGui.SliderFloat("环绕换向(秒)", ref _cfg.OrbitFlipSeconds, 5f, 60f)) _settingsDirty = true;
        if (ImGui.SliderFloat("变焦脉冲间隔(0=关)", ref _cfg.ZoomPulseIntervalSeconds, 0f, 60f)) _settingsDirty = true;
        if (ImGui.SliderFloat("变焦强度", ref _cfg.ZoomStrength, 0f, 0.6f)) _settingsDirty = true;
        if (ImGui.SliderFloat("退出后冷却(秒)", ref _cfg.UserCooldownSeconds, 1f, 60f)) _settingsDirty = true;
        ImGui.Separator();

        if (_state == Mode.Cinematic)
        {
            if (ImGui.Button("立即退出电影镜头"))
            {
                _manualCinema = false;
                ExitCinematic();
            }
        }
        else
        {
            if (ImGui.Button("手动进入电影镜头"))
            {
                _manualCinema = true;
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("强制恢复 HUD"))
        {
            HideOverlay(false);
            ToggleGameHud(false);
            _status = "已强制恢复 HUD";
        }
    }
}
