﻿using DoubleClickFix.Properties;
using Microsoft.Win32;
using System.Collections.Frozen;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static DoubleClickFix.NativeMethods;

namespace DoubleClickFix;

/// <summary>
/// The method HookCallback(..) implements low-level mouse interception to both suppress erroneous double-clicks and
/// enable robust drag-and-drop even on “bouncy” mice. The algorithm proceeds as follows:
/// 
/// 1. Marshal the MSLLHOOKSTRUCT for every event.
/// 2. On WM_MOUSEMOVE:
///    • Update lastMoveTime for each genuinely pressed button.
///    • Compute distance from initialDownPosition and time since initialDownTime.
///    • If the cursor has moved more than MovementThresholdPixels _and_ at least
///      DragReleaseTimeMilliseconds have elapsed since the real down, enter drag-lock:
///      further spurious Up/Down for that button will be suppressed until the drag ends.
/// 3. For button messages (Down/Up):
///    • Identify which MouseButtons value is active and load its threshold setting.
///    • If that button is in drag-lock:
///        – Suppress any Down events.
///        – Suppress Up events until movement has paused for at least DragReleaseTimeMilliseconds,
///          then release, exit drag-lock, and forward the genuine Up.
///    • Otherwise, apply the normal double-click suppression logic:
///        – On Down: compare time since previousUpTime to the per-button threshold and MinDelay.
///          Ignore if too soon; log otherwise.
///        – On Up: record previousUpTime for future comparisons.
/// 4. Track the set of currentlyDownButtons to know which buttons are genuinely pressed.
/// 5. Forward any non-suppressed events to CallNextHook to let the system handle them.
///
/// This ensures that during a true drag gesture—holding down, moving, and releasing—the user can
/// drag and drop even if the physical switch chatters, while still suppressing unwanted rapid clicks.
/// </summary>
internal class MouseHook : IDisposable
{
    // to make sure the hook returns as fast as possible, we cache some texts here.
    private static readonly FrozenDictionary<MouseButtons, string> buttonTextLookup = new Dictionary<MouseButtons, string> {
            { MouseButtons.Left, Resources.Left },
            { MouseButtons.Right, Resources.Right },
            { MouseButtons.Middle, Resources.Middle },
            { MouseButtons.XButton1, Resources.X1 },
            { MouseButtons.XButton2, Resources.X2 },
        }.ToFrozenDictionary();

    private static readonly string ignoredDoubleClickText = Resources.IgnoredDoubleClick;

    private readonly ISettings settings;
    private readonly ILogger logger;
    private readonly INativeMethods nativeMethods;

    // make sure we keep a reference so it's not garbage collected
    private LowLevelMouseProc? mouseProc;
    private nint hookHandle = nint.Zero;
    private const nint InvalidDevice = -1;
    private nint currentDevice = InvalidDevice;

    private readonly Dictionary<MouseButtons, uint> previousUpTime = new() { {MouseButtons.Left , 0 }, {MouseButtons.Right , 0}, {MouseButtons.Middle , 0}, {MouseButtons.XButton1 , 0}, {MouseButtons.XButton2 , 0} };
    private FrozenSet<nint> observedMessages = [];
    private uint ignoredClicks = 0;

    // for drag‐lock handling
    private readonly HashSet<MouseButtons> currentlyDownButtons = [];
    private readonly Dictionary<MouseButtons, POINT> initialDownPosition = [];
    private readonly Dictionary<MouseButtons, long> lastMoveTime = [];
    private readonly Dictionary<MouseButtons, bool> isDragLocked = [];
    private readonly Dictionary<MouseButtons, long> initialDownTime = [];

    public MouseHook(ISettings settings, ILogger logger, INativeMethods nativeMethods)
    {
        this.settings = settings;
        SettingsChanged();
        settings.RegisterSettingsChangedListener(SettingsChanged);
        this.logger = logger;
        this.nativeMethods = nativeMethods;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void SettingsChanged()
    {
        HashSet<nint> messages = [];
        if (settings.LeftThreshold >= 0) {
            messages.Add(WM_LBUTTONDOWN);
            messages.Add(WM_LBUTTONUP);
        }
        if (settings.RightThreshold >= 0)
        {
            messages.Add(WM_RBUTTONDOWN);
            messages.Add(WM_RBUTTONUP);
        }
        if (settings.MiddleThreshold >= 0)
        {
            messages.Add(WM_MBUTTONDOWN);
            messages.Add(WM_MBUTTONUP);
        }
        if (settings.X1Threshold >= 0 || settings.X2Threshold >= 0)
        {
            messages.Add(WM_XBUTTONDOWN);
            messages.Add(WM_XBUTTONUP);
        }
        if (!settings.IsDragCorrectionEnabled && currentlyDownButtons.Any())
        {
            ResetDragLockState();
        }
        observedMessages = messages.ToFrozenSet();
    }

    internal void ResetDragLockState()
    {
        currentlyDownButtons.Clear();
        initialDownPosition.Clear();
        lastMoveTime.Clear();
        isDragLocked.Clear();
        initialDownTime.Clear();
    }

    public bool Install()
    {
        if (settings.UseHook && hookHandle == nint.Zero)
        {
            try
            {
                mouseProc = HookCallback;
                hookHandle = SetHook(mouseProc);
            }
            catch (Win32Exception ex)
            {
                logger.Log($"{Resources.HookNotInstalled}: {ex.Message}");
                return false;
            }
        }
        return hookHandle != nint.Zero;
    }
    public void Uninstall()
    {
        if (settings.UseHook && hookHandle != nint.Zero)
        {
            try
            {
                UnhookWindowsHook(hookHandle);
            }
            catch (Win32Exception ex)
            {
                logger.Log($"Failed to uninstall hook: {ex.Message}");
            }
            finally
            {
                hookHandle = nint.Zero;
                mouseProc = null;
            }
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        // not strictly necessary, but in case of a mouse hook timeout, at least we get the hook back on resume. 
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                Uninstall();
                break;
            case PowerModes.Resume:
                if (!Install())
                {
                    logger.Log("Failed to reinstall mouse hook after Windows resume."); // TODO translate.
                }
                break;
            default:
                break;
        }
    }

    public void RegisterForRawInput(nint hwnd)
    {
        var device = new RAWINPUTDEVICE
        {
            UsagePage = HID_USAGE_PAGE_GENERIC,
            Usage = HID_USAGE_GENERIC_MOUSE,
            Flags = RIDEV_INPUTSINK,
            Target = hwnd
        };

        try
        {
            RegisterRawInputDevicesOrThrow([device], 1, (uint)Marshal.SizeOf(device));
        }
        catch (Win32Exception ex)
        {
            logger.Log($"Failed to register raw input device. {ex.Message}");  // TODO translate.
        }
    }

    public void ProcessRawInput(nint hRawInput)
    {
        uint dwSize = 0;
        _ = GetRawInputData(hRawInput, RID_INPUT, nint.Zero, ref dwSize, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
        nint buffer = Marshal.AllocHGlobal((int)dwSize);
        try
        {
            if (GetRawInputData(hRawInput, RID_INPUT, buffer, ref dwSize, (uint)Marshal.SizeOf<RAWINPUTHEADER>()) != dwSize)
                return;

            var raw = Marshal.PtrToStructure<RAWINPUT>(buffer);

            if (raw.Header.Type == RIM_TYPEMOUSE)
            {
                var device = raw.Header.Device;
                if (currentDevice != device)
                {
                    logger.Log($"{Resources.SwitchedDevice} {device}", true);
                    currentDevice = device;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        try
        {
            if (wParam == WM_MOUSEMOVE)
            {
                HandleMouseMove(lParam);
            }
            else
            {
                return HandleMouseButton(nCode, wParam, lParam);
            }
            return nativeMethods.CallNextHook(hookHandle, nCode, wParam, lParam);
        }
        catch (Exception ex)
        {
            logger.Log($"Error in hook callback: {ex}");
            return nativeMethods.CallNextHook(hookHandle, nCode, wParam, lParam);
        }
    }

    private void HandleMouseMove(nint lParam)
    {
        if (!settings.IsDragCorrectionEnabled || !currentlyDownButtons.Any())
        {
            return;
        }

        var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam)!;
        const int MovementThresholdPixels = 5;

        foreach (var button in currentlyDownButtons.ToList())
        {
            lastMoveTime[button] = hookStruct.time;

            if (!isDragLocked.GetValueOrDefault(button, false))
            {
                var start = initialDownPosition[button];
                int dx = hookStruct.pt.x - start.x;
                int dy = hookStruct.pt.y - start.y;
                int distSq = dx * dx + dy * dy;

                long elapsedSinceDown = hookStruct.time - initialDownTime[button];

                if (distSq >= MovementThresholdPixels * MovementThresholdPixels &&
                    elapsedSinceDown >= settings.DragStartTimeMilliseconds)
                {
                    isDragLocked[button] = true;
                    logger.Log(string.Format(Resources.EnterDragLock, buttonTextLookup[button], elapsedSinceDown), true);
                }
            }
        }
    }

    private nint HandleMouseButton(int nCode, nint wParam, nint lParam)
    {
        var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam)!;
        const nint IgnoreMouseEvent = 1;

        if (!ProcessMouseEvent(nCode, wParam))
        {
            return nativeMethods.CallNextHook(hookHandle, nCode, wParam, lParam);
        }

        if (!TryGetMouseButton(wParam, hookStruct.mouseData, out var activeButton, out var buttonDown, out var buttonUp))
        {
            return nativeMethods.CallNextHook(hookHandle, nCode, wParam, lParam);
        }

        int threshold = GetThreshold(activeButton);

        // If we're in drag‐lock, suppress spurious downs/ups
        if (settings.IsDragCorrectionEnabled && isDragLocked.GetValueOrDefault(activeButton, false))
        {
            if (buttonDown)
            {
                // drop any extra presses
                return IgnoreMouseEvent;
            }
            else if (buttonUp)
            {
                // only allow the real release once movement has paused long enough
                long elapsedSinceMove = hookStruct.time - lastMoveTime[activeButton];
                if (elapsedSinceMove >= settings.DragStopTimeMilliseconds)
                {
                    // exit drag‐lock, forward genuine release
                    isDragLocked[activeButton] = false;
                    currentlyDownButtons.Remove(activeButton);
                    previousUpTime[activeButton] = hookStruct.time;
                    logger.Log(string.Format(Resources.ExitDragLock, buttonTextLookup[activeButton]), true);

                    return nativeMethods.CallNextHook(hookHandle, nCode, wParam, lParam);
                }
                // still jittering: suppress
                return IgnoreMouseEvent;
            }
        }

        // Normal double‐click suppression and down/up tracking
        // We take the elapsed time between the last mouse up and the current mouse down event.
        // If it's smaller than the minimal delay, we ignore the current mouse down event.
        if (buttonDown)
        {
            // record genuine down
            currentlyDownButtons.Add(activeButton);
            initialDownPosition[activeButton] = hookStruct.pt;
            initialDownTime[activeButton] = hookStruct.time;   // ensure Dictionary<MouseButtons,long> exists
            lastMoveTime[activeButton] = hookStruct.time;

            long delta = hookStruct.time - previousUpTime[activeButton];
            bool belowMin = settings.MinDelay >= 0 && delta <= settings.MinDelay;
            bool ignore = delta < threshold && !belowMin;

            if (ignore)
            {
                ignoredClicks++;
                logger.Log(
                $"{ignoredDoubleClickText} ({buttonTextLookup[activeButton]}): {delta} ms (#{ignoredClicks})"
                );
                previousUpTime[activeButton] = 0;
                return IgnoreMouseEvent;
            }
            else if (delta < settings.WindowsDoubleClickTimeMilliseconds)
            {
                logger.Log($"{delta} ms ({buttonTextLookup[activeButton]})", true);
            }
        }
        else if (buttonUp)
        {
            // genuine up
            currentlyDownButtons.Remove(activeButton);
            previousUpTime[activeButton] = hookStruct.time;
        }

        // forward everything else
        return nativeMethods.CallNextHook(hookHandle, nCode, wParam, lParam);
    }

    private bool TryGetMouseButton(nint wParam, uint mouseData, out MouseButtons activeButton, out bool buttonDown, out bool buttonUp)
    {
        buttonDown = false;
        buttonUp = false;
        activeButton = MouseButtons.None;

        switch (wParam)
        {
            case WM_LBUTTONDOWN:
                buttonDown = true;
                activeButton = MouseButtons.Left;
                return true;

            case WM_LBUTTONUP:
                buttonUp = true;
                activeButton = MouseButtons.Left;
                return true;

            case WM_RBUTTONDOWN:
                buttonDown = true;
                activeButton = MouseButtons.Right;
                return true;

            case WM_RBUTTONUP:
                buttonUp = true;
                activeButton = MouseButtons.Right;
                return true;

            case WM_MBUTTONDOWN:
                buttonDown = true;
                activeButton = MouseButtons.Middle;
                return true;

            case WM_MBUTTONUP:
                buttonUp = true;
                activeButton = MouseButtons.Middle;
                return true;

            case WM_XBUTTONDOWN:
                buttonDown = true;
                activeButton = GetXButton(mouseData);
                return true;

            case WM_XBUTTONUP:
                buttonUp = true;
                activeButton = GetXButton(mouseData);
                return true;

            default:
                return false;
        }
    }

    private int GetThreshold(MouseButtons button)
    {
        return button switch
        {
            MouseButtons.Left => settings.LeftThreshold,
            MouseButtons.Right => settings.RightThreshold,
            MouseButtons.Middle => settings.MiddleThreshold,
            MouseButtons.XButton1 => settings.X1Threshold,
            MouseButtons.XButton2 => settings.X2Threshold,
            _ => -1,
        };
    }

    private bool ProcessMouseEvent(int nCode, nint wParam)
    {
        return nCode >= 0
            && settings.IgnoredDevice != currentDevice
            && observedMessages.Contains(wParam);
    }

    private MouseButtons GetXButton(uint mouseData)
    {
        const int MK_XBUTTON1_DOWN = 0x0020;
        const int MK_XBUTTON2_DOWN = 0x0040;
        const int XBUTTON1_UP = 0x0001;
        const int XBUTTON2_UP = 0x0002;

        ushort loWord = unchecked((ushort)(ulong)mouseData);
        ushort hiWord = unchecked((ushort)((ulong)mouseData >> 16));
        if (settings.X1Threshold >=0 && ((loWord & MK_XBUTTON1_DOWN) != 0 || (hiWord & XBUTTON1_UP) != 0))
        {
            return MouseButtons.XButton1;
        }
        else if (settings.X2Threshold >= 0 && ((loWord & MK_XBUTTON2_DOWN) != 0 || (hiWord & XBUTTON2_UP) != 0))
        {
            return MouseButtons.XButton2;
        }
        return MouseButtons.None;
    }
      
    private static nint SetHook(LowLevelMouseProc proc)
    {
        using var currentModule = Process.GetCurrentProcess().MainModule!;
        var moduleHandle = GetModuleHandleOrThrow(currentModule.ModuleName!);
        return SetWindowsHook(WH_MOUSE_LL, proc, moduleHandle, 0);
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Uninstall();
    }
}