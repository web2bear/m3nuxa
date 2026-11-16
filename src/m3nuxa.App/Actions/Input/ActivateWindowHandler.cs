using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace m3nuxa.App;

// Prototype: the target belongs to one workflow run, not the captured Explorer context.
internal sealed class InputTarget
{
    public nint Window { get; set; }
    public uint ProcessId { get; set; }

    public nint GetWindow(nint source)
    {
        if (Window == 0) return source;
        ActivateWindowHandler.GetWindowThreadProcessId(Window, out var currentPid);
        if (!NativeMethods.IsWindow(Window) || currentPid != ProcessId)
            throw new WorkflowSafeException(L.Get("ActionSourceWindowClosed"));
        if (IntegrityLevel.Read(ProcessId) > IntegrityLevel.Read((uint)Environment.ProcessId))
            throw new WorkflowSafeException(L.Get("WindowElevated"));
        return Window;
    }
}

internal static class IntegrityLevel
{
    internal static int Read(uint pid)
    {
        var process = OpenProcess(0x1000, false, pid);
        if (process == 0) throw new WorkflowSafeException(L.Get("WindowIntegrityUnknown"));
        try
        {
            if (!OpenProcessToken(process, 0x0008, out var token))
                throw new WorkflowSafeException(L.Get("WindowIntegrityUnknown"));
            try
            {
                GetTokenInformation(token, 25, 0, 0, out var size);
                if (size <= 0) throw new WorkflowSafeException(L.Get("WindowIntegrityUnknown"));
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!GetTokenInformation(token, 25, buffer, size, out _))
                        throw new WorkflowSafeException(L.Get("WindowIntegrityUnknown"));
                    var sid = Marshal.ReadIntPtr(buffer);
                    var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                    if (count == 0) throw new WorkflowSafeException(L.Get("WindowIntegrityUnknown"));
                    return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(nint token, int kind, nint buffer, int size, out int length);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthority(nint sid, uint index);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}

internal sealed class ActivateWindowHandler : IActionHandler
{
    public ActionKind Kind => ActionKind.ActivateWindow;

    public async Task<ActionResult> ExecuteAsync(MenuAction action, InvocationContext context, nint owner, CancellationToken cancellationToken)
    {
        var processName = Path.GetFileNameWithoutExtension(action.Path.Trim());
        var timeout = action.TimeoutMilliseconds ?? 10000;
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = FindWindows(processName, action.Text.Trim());
            if (matches.Count > 1) throw new WorkflowSafeException(L.Get("WindowMultiple"));
            if (matches.Count == 1)
            {
                var (window, expectedPid) = matches[0];
                await WorkflowExecutor.InputGate.WaitAsync(cancellationToken);
                try
                {
                    GetWindowThreadProcessId(window, out var currentPid);
                    if (!NativeMethods.IsWindow(window) || currentPid != expectedPid) continue;
                    if (IsIconic(window)) ShowWindow(window, 9);
                    if (NativeMethods.GetForegroundWindow() != window && !NativeMethods.SetForegroundWindow(window))
                        throw new WorkflowSafeException(L.Get("WindowFocusFailed"));
                    if (NativeMethods.GetForegroundWindow() != window)
                        throw new WorkflowSafeException(L.Get("WindowFocusFailed"));
                    GetWindowThreadProcessId(window, out var pid);
                    context.InputTarget.ProcessId = pid;
                    context.InputTarget.Window = window;
                    return ActionExecutor.Ok();
                }
                finally { WorkflowExecutor.InputGate.Release(); }
            }
            var remaining = timeout - elapsed.ElapsedMilliseconds;
            if (remaining <= 0) throw new WorkflowSafeException(L.Get("WindowNotFound"));
            await Task.Delay((int)Math.Min(100, remaining), cancellationToken);
        }
    }

    private static List<(nint Window, uint ProcessId)> FindWindows(string processName, string title)
    {
        var result = new List<(nint, uint)>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindowTextLength(window) == 0) return true;
            var text = new StringBuilder(GetWindowTextLength(window) + 1);
            GetWindowText(window, text, text.Capacity);
            if (!text.ToString().Contains(title, StringComparison.CurrentCultureIgnoreCase)) return true;
            GetWindowThreadProcessId(window, out var pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase)) result.Add((window, pid));
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            return true;
        }, 0);
        return result;
    }

    private delegate bool EnumWindowsCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
