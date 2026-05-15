using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InterviewAssistant.App.Services;

/// <summary>Global End / Delete hooks (mirrors live.py on_press).</summary>
public sealed class InterviewHotkeyService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int VkEnd = 0x23;
    private const int VkDelete = 0x2E;

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hook = IntPtr.Zero;
    private GCHandle _gcHandle;

    public double EndCooldownSeconds { get; set; } = 0.8;
    public double DeleteCooldownSeconds { get; set; } = 0.8;

    private double _lastEnd;
    private double _lastDelete;

    public event Action? EndPressed;
    public event Action? DeletePressed;

    public InterviewHotkeyService()
    {
        _proc = HookCallback;
        _gcHandle = GCHandle.Alloc(_proc);
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero)
            return;
        try
        {
            // hMod = 0 is correct for WH_KEYBOARD_LL (in-process low-level hook).
            _hook = SetWindowsHookEx(WhKeyboardLl, _proc, IntPtr.Zero, 0);
            if (_hook == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                Trace.WriteLine($"[InterviewAssistant] Global hotkeys failed (error {err})");
                return;
            }

            Trace.WriteLine("[InterviewAssistant] Global hotkeys active (End/Delete)");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[InterviewAssistant] Global hotkeys failed: {ex.Message}");
            _hook = IntPtr.Zero;
        }
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WmKeydown && _hook != IntPtr.Zero)
        {
            try
            {
                var vk = Marshal.ReadInt32(lParam);
                var now = Environment.TickCount64 / 1000.0;
                switch (vk)
                {
                    case VkDelete:
                        if (now - _lastDelete >= DeleteCooldownSeconds)
                        {
                            _lastDelete = now;
                            DeletePressed?.Invoke();
                        }

                        break;
                    case VkEnd:
                        if (now - _lastEnd >= EndCooldownSeconds)
                        {
                            _lastEnd = now;
                            EndPressed?.Invoke();
                        }

                        break;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[InterviewAssistant] hotkey callback: {ex.Message}");
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        Stop();
        if (_gcHandle.IsAllocated)
            _gcHandle.Free();
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
}
