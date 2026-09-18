// WinCtl - list and focus Windows top-level windows.
// Compiled once by lib/wincl.js with the .NET Framework csc.exe, so it must
// stay C# 5 clean: no interpolated strings, no out-var, no expression bodies.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

class WinCtl {
    delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    const int SW_RESTORE = 9;
    const int DWMWA_CLOAKED = 14;

    // UWP keeps invisible shell windows around; they are "visible" but cloaked.
    static bool IsCloaked(IntPtr h) {
        int cloaked = 0;
        try {
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out cloaked, sizeof(int)) != 0) return false;
        } catch (Exception) { return false; }
        return cloaked != 0;
    }

    static string ProcName(uint pid) {
        try { return Process.GetProcessById((int)pid).ProcessName; }
        catch (Exception) { return "?"; }
    }

    static List<IntPtr> Handles() {
        List<IntPtr> found = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            if (!IsWindowVisible(h)) return true;
            if (GetWindowTextLength(h) == 0) return true;
            if (IsCloaked(h)) return true;
            StringBuilder cls = new StringBuilder(256);
            GetClassName(h, cls, 256);
            string c = cls.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd") return true;
            found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // hwnd \t pid \t process \t x \t y \t w \t h \t focused \t title
    // fg is passed in so one enumeration reports a single consistent winner.
    static void Print(IntPtr h, IntPtr fgWin) {
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        StringBuilder sb = new StringBuilder(1024);
        GetWindowText(h, sb, 1024);
        RECT r;
        GetWindowRect(h, out r);
        bool fg = (h == fgWin);
        Console.WriteLine(string.Join("\t", new string[] {
            h.ToInt64().ToString(CultureInfo.InvariantCulture),
            pid.ToString(CultureInfo.InvariantCulture),
            ProcName(pid),
            r.Left.ToString(CultureInfo.InvariantCulture),
            r.Top.ToString(CultureInfo.InvariantCulture),
            (r.Right - r.Left).ToString(CultureInfo.InvariantCulture),
            (r.Bottom - r.Top).ToString(CultureInfo.InvariantCulture),
            fg ? "1" : "0",
            sb.ToString().Replace('\t', ' ')
        }));
    }

    // SetForegroundWindow is refused unless the caller owns the foreground, so
    // borrow the foreground thread's input queue for the duration.
    static bool Focus(IntPtr h) {
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        uint dummy;
        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out dummy);
        uint myThread = GetCurrentThreadId();
        bool attached = false;
        if (fgThread != 0 && fgThread != myThread) attached = AttachThreadInput(myThread, fgThread, true);
        BringWindowToTop(h);
        bool ok = SetForegroundWindow(h);
        if (attached) AttachThreadInput(myThread, fgThread, false);
        if (!ok) { System.Threading.Thread.Sleep(60); ok = (GetForegroundWindow() == h); }
        return ok;
    }

    static int Main(string[] args) {
        string cmd = args.Length > 0 ? args[0] : "list";
        if (cmd == "list") {
            IntPtr fgWin = GetForegroundWindow();
            foreach (IntPtr h in Handles()) Print(h, fgWin);
            return 0;
        }
        if (cmd == "fg") {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return 1;
            Print(h, h);
            return 0;
        }
        if (cmd == "focus" && args.Length > 1) {
            IntPtr h = new IntPtr(long.Parse(args[1], CultureInfo.InvariantCulture));
            bool ok = Focus(h);
            if (ok) { IntPtr now = GetForegroundWindow(); Print(now, now); }
            return ok ? 0 : 1;
        }
        Console.Error.WriteLine("usage: WinCtl [list|fg|focus <hwnd>]");
        return 2;
    }
}
