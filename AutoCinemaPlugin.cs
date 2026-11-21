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
    private const int SwShowNa = 8; // show without activating / stealing focus
    private const string OverlayWindowTitle = "ETS2LA Overlay";

    private const string CameraChannelId = "fishleia.autocinema";
    private const string HudChannelId = "fishleia.autocinema.hud";
    private const string WindowTitle = "baobao-7799 自动电影镜头";
    private const string SettingsFile = "fishleia.autocinema.json";
    private const string OutputTopic = "ETS2LA.Game.Output.ControlEvent";

    private enum Mode { Monitoring, Cinematic, Cooldown }

    private readonly SettingsHandler _settingsHandler = new();
    private CinemaSettings _cfg = new();
    private volatile bool _settingsDirty = false;
    private DateTime _lastSave = DateTime.MinValue;

    private Mode _state = Mode.Monitoring;
    private DateTime _cooldownUntil = DateTime.MinValue;
    private DateTime? _apSince = null;
    private bool _autopilotSustained = false;
    private float _idleSeconds = 0f;
    private float _speed = 0f;
    private readonly Stopwatch _cinemaT = new Stopwatch();
    private bool _overlayHidden = false;
    private bool _gameHudToggled = false;
    private bool _manualCinema = false;
    private string _status = "待机中";

    private readonly ConcurrentDictionary<string, DateTime> _otherChannels = new();

    public override PluginInformation Info => new()
    {
        Id = "fishleia.autocinema",
        Name = "baobao-7799 自动电影镜头",
        Description = "自动驾驶期间长时间无操作时自动切换电影镜头（类 GTA），并隐藏 ETS2LA 覆盖层。",
        Version = "1.0.0",
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
        Events.Current.Subscribe<ControlEvent>(OutputTopic, OnAnyControlEvent);
        OverlayHandler.Current.RegisterWindow(new WindowDefinition
        {
            Title = WindowTitle,
            Width = 440,
            Height = 480,
            Alpha = 0.92f,
            Open = _cfg.ShowWindow,
        }, RenderUI);
    }

    public override void OnDisable()
    {
        base.OnDisable();
        Events.Current.Unsubscribe<ControlEvent>(OutputTopic, OnAnyControlEvent);
        OverlayHandler.Current.UnregisterWindow(new WindowDefinition { Title = WindowTitle });
        ExitCinematicSafe();
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

    private static float GetIdleSeconds()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii)) return 0f;
        uint now = GetTickCount();
        uint idle = now - lii.dwTime;
        return idle / 1000f;
    }

    public override void Tick()
    {
        try
        {
            var td = GameTelemetry.Current.GetCurrentData();
            _speed = td.truckFloat.speed;
            _idleSeconds = GetIdleSeconds();

            DateTime now = DateTime.UtcNow;
            bool rawActive = DetectAutopilotRaw(td);
            if (rawActive)
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
                case Mode.Monitoring:
                    TickMonitoring(td);
                    break;
                case Mode.Cinematic:
                    TickCinematic(td);
                    break;
                case Mode.Cooldown:
                    if (now >= _cooldownUntil)
                    {
                        _state = Mode.Monitoring;
                        _status = "待机中";
                    }
                    else
                    {
                        double left = (_cooldownUntil - now).TotalSeconds;
                        _status = $"冷却中 {left:F0}s";
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex.ToString(), "AutoCinema tick error");
        }

        SaveIfDirty();
    }

    private bool DetectAutopilotRaw(GameTelemetryData td)
    {
        bool channelActive = false;
        DateTime now = DateTime.UtcNow;
        foreach (var kv in _otherChannels)
        {
            if ((now - kv.Value).TotalSeconds < 0.6)
            {
                channelActive = true;
                break;
            }
        }

        bool heuristic = td.truckFloat.speed > _cfg.MinSpeed
            && Math.Abs(td.truckFloat.userSteer) < 0.02f
            && Math.Abs(td.truckFloat.userThrottle) < 0.02f
            && Math.Abs(td.truckFloat.userBrake) < 0.02f
            && (Math.Abs(td.truckFloat.gameSteer) > 0.02f
                || td.truckFloat.gameThrottle > 0.05f
                || td.truckBool.cruiseControl);

        bool detected = (_cfg.UseChannelDetection && channelActive)
            || (_cfg.UseTelemetryHeuristic && heuristic);
        return detected;
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
                : (_cfg.RequireMoving && _speed <= 1f) ? "车辆未行驶"
                : (_cfg.RequireAutopilot && !_autopilotSustained) ? "未检测到自动驾驶"
                : $"空闲 {_idleSeconds:F0}/{_cfg.IdleSeconds:F0}s";
            _status = "待机中 · " + why;
        }
    }

    private void TickCinematic(GameTelemetryData td)
    {
        bool userBack = _cinemaT.Elapsed.TotalSeconds > 3.0 && _idleSeconds < 0.2f;
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
        if (_state != Mode.Cinematic)
            return;

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
    }

    private void HideOverlay(bool hide)
    {
        try
        {
            IntPtr hwnd = FindWindowW(null, OverlayWindowTitle);
            if (hwnd == IntPtr.Zero)
            {
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
        _gameHudToggled = intoCinematic;
        try
        {
            Events.Current.Publish(OutputTopic, new ControlEvent
            {
                ChannelDefinition = new ControlChannelDefinition { Id = HudChannelId, Timeout = 0.5f },
                Properties = new ControlProperties { Weight = 1f, BooleanType = ControlBooleanType.TrueToToggle },
                Variables = new ControlVariables { showhud = true },
            });
            // Remove the pulse channel shortly after so it can pulse again later.
            _ = Task.Run(async () =>
            {
                await Task.Delay(400);
                PublishRemove(HudChannelId);
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex.ToString(), "AutoCinema: game HUD toggle failed.");
        }
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
        ImGui.Text(_autopilotSustained ? "自动驾驶：检测到" : "自动驾驶：未检测到");
        ImGui.Separator();

        ImGui.Text("镜头模式");
        if (ImGui.Button("摇镜(车内/追尾通用)")) { _cfg.Mode = 0; _settingsDirty = true; }
        ImGui.SameLine();
        if (ImGui.Button("GTA 环绕")) { _cfg.Mode = 1; _settingsDirty = true; }
        ImGui.SameLine();
        if (ImGui.Button("混合")) { _cfg.Mode = 2; _settingsDirty = true; }
        ImGui.Separator();

        if (ImGui.Checkbox("启用插件", ref _cfg.Enabled)) _settingsDirty = true;
        if (ImGui.Checkbox("要求自动驾驶开启", ref _cfg.RequireAutopilot)) _settingsDirty = true;
        if (ImGui.Checkbox("要求车辆行驶中", ref _cfg.RequireMoving)) _settingsDirty = true;
        if (ImGui.Checkbox("隐藏 ETS2LA 覆盖层", ref _cfg.HideOverlay)) _settingsDirty = true;
        if (ImGui.Checkbox("同时隐藏游戏 HUD", ref _cfg.HideGameHud)) _settingsDirty = true;
        if (ImGui.Checkbox("输出通道检测", ref _cfg.UseChannelDetection)) _settingsDirty = true;
        if (ImGui.Checkbox("遥测推断检测", ref _cfg.UseTelemetryHeuristic)) _settingsDirty = true;
        ImGui.Separator();

        if (ImGui.SliderFloat("进入空闲秒数", ref _cfg.IdleSeconds, 5f, 300f)) _settingsDirty = true;
        if (ImGui.SliderFloat("自动驾驶确认秒数", ref _cfg.AutoPilotConfirmSeconds, 1f, 30f)) _settingsDirty = true;
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
    }
}
