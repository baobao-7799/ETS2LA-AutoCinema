// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 baobao-7799
using System;

namespace FishLeia.AutoCinema;

[Serializable]
public class CinemaSettings
{
    public bool Enabled = true;
    public bool ShowWindow = true;

    // 默认不强制要求自动驾驶：真正的"空闲"（键鼠 + 方向盘/油门/刹车都没动）即可进入。
    // 需要"必须在自动驾驶时才进入"时再打开 RequireAutopilot。
    public bool RequireAutopilot = false;
    public bool UseChannelDetection = true;
    public bool UseTelemetryHeuristic = true;
    public bool RequireMoving = false;

    public bool HideOverlay = true;
    public bool HideGameHud = false;

    public float IdleSeconds = 30f;
    public float AutoPilotConfirmSeconds = 3f;
    public float MinSpeed = 5f;
    public int Mode = 0;

    public float PanPeriodSeconds = 40f;
    public float PanAmplitude = 0.25f;
    public float VerticalPeriodSeconds = 25f;
    public float VerticalAmplitude = 0.06f;
    public float OrbitSpeed = 0.05f;
    public float OrbitFlipSeconds = 20f;
    public float ZoomPulseIntervalSeconds = 12f;
    public float ZoomStrength = 0.25f;

    public float UserCooldownSeconds = 8f;
}
