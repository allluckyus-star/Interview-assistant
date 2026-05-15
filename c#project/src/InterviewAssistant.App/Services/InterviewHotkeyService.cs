using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InterviewAssistant.App.Services;

/// <summary>Global low-level hook for End / Delete (interview).</summary>
public sealed class InterviewHotkeyService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmSyskeydown = 0x0104;
    private const int VkEnd = 0x23;
    private const int VkDelete = 0x2E;
    private const int LlkhfUp = unchecked((int)0x80000000);

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hook = IntPtr.Zero;
    private GCHandle _gcHandle;

    public double EndCooldownSeconds { get; set; } = 0.8;
    public double DeleteCooldownSeconds { get; set; } = 0.8;

    private double _lastEnd;
    private double _lastDelete;

    public bool IsActive => _hook != IntPtr.Zero;

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
            var moduleName = Process.GetCurrentProcess().MainModule?.ModuleName;
            var module = string.IsNullOrEmpty(moduleName)
                ? GetModuleHandle(null)
                : GetModuleHandle(moduleName);
            _hook = SetWindowsHookEx(WhKeyboardLl, _proc, module, 0);
            if (_hook == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                Trace.WriteLine($"[InterviewAssistant] End/Delete hotkey hook failed (error {err})");
                return;
            }

            Trace.WriteLine("[InterviewAssistant] End/Delete global hotkeys active");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[InterviewAssistant] End/Delete hotkey hook failed: {ex.Message}");
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
        if (nCode >= 0 && _hook != IntPtr.Zero && IsKeyDownMessage(wParam))
        {
            try
            {
                var vk = Marshal.ReadInt32(lParam, 0);
                var flags = Marshal.ReadInt32(lParam, 8);
                if ((flags & LlkhfUp) != 0)
                    return CallNextHookEx(_hook, nCode, wParam, lParam);

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

    private static bool IsKeyDownMessage(IntPtr wParam) =>
        wParam == (IntPtr)WmKeydown || wParam == (IntPtr)WmSyskeydown;

    public void Dispose()
    {
        Stop();
        if (_gcHandle.IsAllocated)
            _gcHandle.Free();
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
