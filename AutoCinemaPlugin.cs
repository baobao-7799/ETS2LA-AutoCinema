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
    // ---- Win32: 键鼠空闲检测 + 覆盖层隐藏 ----
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

    // ---- XInput: 手柄活动检测（自动驾驶时机器人自己的方向盘输入不算"人"）----
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState(int dwUserIndex, ref XINPUT_STATE pState);

    private const int SwHide = 0;
    private const int SwShowNa = 8;

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

    private float _sysIdle;
    private bool _liiFailed;
    private float _padIdle = float.MaxValue;
    private bool _padOk;
    private bool _padActive;
    private float _lastPadAct = float.MinValue;
    private int _padPoll;
    private float _lastUserAct = float.MinValue;
    private bool _userActNow;
    private bool _humanActivityNow;
    private float _idleSeconds;
    private float _speed;
    private bool _autopilotChannelActive;
    private DateTime? _apSince;
    private bool _autopilotSustained;

    private readonly Stopwatch _cinemaT = new Stopwatch();
    private bool _overlayHidden;
    private bool _gameHudToggled;
    private bool _gameHudEverOn;
    private bool _manualCinema;
    private string _status = "待机中";

    private bool _channelActive;
    private bool _cruiseOn;
    private bool _gameAiOn;

    private readonly ConcurrentDictionary<string, DateTime> _otherChannels = new();

    public override PluginInformation Info => new()
    {
        Id = "fishleia.autocinema",
        Name = "baobao-7799 自动电影镜头",
        Description = "长时间未操作电脑时自动切换电影镜头（类 GTA），并隐藏 ETS2LA 覆盖层；动键鼠/手柄立刻恢复。自动驾驶由 ETS2LA 插件控制时，机器人的方向盘输入不会干扰空闲判定。",
        Version = "1.2.0",
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
                Height = 520,
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

    private void PollGamepad()
    {
        try
        {
            var st = new XINPUT_STATE();
            int hr = XInputGetState(0, ref st);
            if (hr != 0)
            {
                _padOk = false;
                _padIdle = float.MaxValue;
                return;
            }
            _padOk = true;
            var g = st.Gamepad;
            bool act = g.wButtons != 0
                || g.bLeftTrigger > 30 || g.bRightTrigger > 30
                || Math.Abs(g.sThumbLX) > 8000 || Math.Abs(g.sThumbLY) > 8000
                || Math.Abs(g.sThumbRX) > 8000 || Math.Abs(g.sThumbRY) > 8000;
            if (act) _lastPadAct = _now;
            _padActive = act;
            _padIdle = _now - _lastPadAct;
        }
        catch
        {
            _padOk = false;
        }
    }

    public override void Tick()
    {
        try
        {
            _now = (float)_sw.Elapsed.TotalSeconds;

            var td = GameTelemetry.Current.GetCurrentData();
            _speed = td.truckFloat.speed;

            // ---- 自动驾驶通道检测（AutoRestPark/pathfinding/ACC 等插件正在发控制事件）----
            DateTime now = DateTime.UtcNow;
            _channelActive = false;
            foreach (var kv in _otherChannels)
            {
                if ((now - kv.Value).TotalSeconds < 1.0) { _channelActive = true; break; }
            }
            _autopilotChannelActive = _channelActive;
            _cruiseOn = td.truckBool.cruiseControl;
            _gameAiOn = Math.Abs(td.truckFloat.gameSteer) > 0.02f || td.truckFloat.gameThrottle > 0.05f;
            bool detected = (_cfg.UseChannelDetection && _channelActive)
                || (_cfg.UseTelemetryHeuristic && td.truckBool.engineEnabled && _speed > _cfg.MinSpeed && (_gameAiOn || _cruiseOn));
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

            // ---- 人类活动检测（三分量）----
            _sysIdle = GetSystemIdleSeconds(out bool liiFailed);
            _liiFailed = liiFailed;

            if ((_padPoll++ % 3) == 0) PollGamepad();

            float userAct = Math.Abs(td.truckFloat.userSteer)
                + td.truckFloat.userThrottle
                + td.truckFloat.userBrake
                + td.truckFloat.userClutch;
            _userActNow = userAct > 0.05f;
            if (_userActNow) _lastUserAct = _now;
            float teleIdle = _now - _lastUserAct;

            // 自动驾驶进行中：踏板/方向盘数据是机器人自己输入的，不算"人在"，
            // 只有键鼠和手柄才代表真人。
            bool kbMouseActive = !_liiFailed && _sysIdle < 0.2f;
            _humanActivityNow = kbMouseActive || _padActive || (!_autopilotChannelActive && _userActNow);

            float idle = float.MaxValue;
            if (!_liiFailed) idle = Math.Min(idle, _sysIdle);
            if (_padOk) idle = Math.Min(idle, _padIdle);
            if (!_autopilotChannelActive) idle = Math.Min(idle, teleIdle);
            _idleSeconds = idle;

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

        if (td.paused)
        {
            _status = "待机中 · 游戏暂停";
            return;
        }
        if (!td.sdkActive)
        {
            _status = "待机中 · 等待游戏数据";
            return;
        }

        // 至少有一种输入检测可用，才能判定"人没动"
        bool anyDetector = (!_liiFailed) || _padOk || !_autopilotChannelActive;
        if (!anyDetector)
        {
            _status = "输入检测不可用（键鼠失败且自动驾驶中）";
            return;
        }

        bool want = (!_cfg.RequireMoving || _speed > 1f)
            && (!_cfg.RequireAutopilot || _autopilotSustained)
            && (_manualCinema || _idleSeconds >= _cfg.IdleSeconds);

        if (want)
        {
            EnterCinematic();
        }
        else
        {
            string why = (_cfg.RequireMoving && _speed <= 1f) ? "车辆未行驶"
                : (_cfg.RequireAutopilot && !_autopilotSustained) ? "未检测到自动驾驶"
                : $"空闲 {_idleSeconds:F0}/{_cfg.IdleSeconds:F0}s";
            _status = "待机中 · " + why + "（键鼠 " + (_liiFailed ? "?" : _sysIdle.ToString("F0") + "s")
                + " | 手柄 " + (_padOk ? _padIdle.ToString("F0") + "s" : "无")
                + " | " + (_autopilotChannelActive ? "自动驾驶中(踏板忽略)" : "手动(踏板计入)") + "）";
        }
    }

    private void TickCinematic(GameTelemetryData td)
    {
        bool userBack = _cinemaT.Elapsed.TotalSeconds > 3.0 && _humanActivityNow;
        bool exit = !_cfg.Enabled
            || !td.sdkActive
            || td.paused
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
                if (_gameHudEverOn) PublishHudHold();
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

        if (_cfg.Mode == 1)
        {
            int seg = (int)(t / _cfg.OrbitFlipSeconds);
            float dir = (seg % 2 == 0) ? 1f : -1f;
            lr = dir * _cfg.OrbitSpeed;
            ud = 0f;
        }
        else if (_cfg.Mode == 2)
        {
            int seg = (int)(t / _cfg.OrbitFlipSeconds);
            float dir = (seg % 2 == 0) ? 1f : -1f;
            lr = dir * _cfg.OrbitSpeed
                + (float)Math.Sin(2 * Math.PI * t / _cfg.PanPeriodSeconds) * _cfg.PanAmplitude * 0.5f;
            ud = (float)Math.Sin(2 * Math.PI * t / _cfg.VerticalPeriodSeconds + 1.3f) * _cfg.VerticalAmplitude;
        }
        else
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
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.3f, 1f), "键鼠输入检测失败");
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
