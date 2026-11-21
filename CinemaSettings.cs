// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 baobao-7799
using System;

namespace FishLeia.AutoCinema;

[Serializable]
public class CinemaSettings
{
    public bool Enabled = true;
    public bool ShowWindow = true;
    public bool RequireAutopilot = true;
    public bool UseChannelDetection = true;
    public bool UseTelemetryHeuristic = true;
    public bool RequireMoving = true;
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
