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
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorProc cb, IntPtr data);

    delegate bool MonitorProc(IntPtr mon, IntPtr dc, ref RECT r, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    const int SW_RESTORE = 9;
    const int SW_MAXIMIZE = 3;
    const int DWMWA_CLOAKED = 14;
    const uint MONITOR_DEFAULTTONEAREST = 2;
    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOACTIVATE = 0x0010;

    // Without this the process is DPI-virtualised: on a mixed-DPI setup Windows
    // silently rewrites the coordinates passed to SetWindowPos, so a move lands
    // at the wrong size. Per-monitor-v2 first, system-DPI on older builds.
    static void MakeDpiAware() {
        try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch (Exception) { }
        try { SetProcessDPIAware(); } catch (Exception) { }
    }

    static MONITORINFO Info(IntPtr mon) {
        MONITORINFO mi = new MONITORINFO();
        mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
        GetMonitorInfo(mon, ref mi);
        return mi;
    }

    // Sorted top-to-bottom then left-to-right, so "next monitor" is a stable
    // rotation whatever the physical arrangement - vertical stacks included.
    static List<IntPtr> Monitors() {
        List<IntPtr> mons = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            delegate(IntPtr mon, IntPtr dc, ref RECT r, IntPtr data) { mons.Add(mon); return true; },
            IntPtr.Zero);
        mons.Sort(delegate(IntPtr a, IntPtr b) {
            MONITORINFO ma = Info(a), mb = Info(b);
            if (ma.rcMonitor.Top != mb.rcMonitor.Top) return ma.rcMonitor.Top.CompareTo(mb.rcMonitor.Top);
            return ma.rcMonitor.Left.CompareTo(mb.rcMonitor.Left);
        });
        return mons;
    }

    static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

    // Move a window to the next/previous monitor, keeping its position and size
    // proportional to the work area so it stays usable on a differently-sized
    // display. Does not depend on Win+Shift+Arrow, which is direction-based and
    // does nothing when there is no neighbour that way.
    static bool MoveToMonitor(IntPtr h, int delta) {
        List<IntPtr> mons = Monitors();
        if (mons.Count < 2) return false;
        IntPtr cur = MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST);
        int idx = mons.IndexOf(cur);
        if (idx < 0) idx = 0;
        int want = ((idx + delta) % mons.Count + mons.Count) % mons.Count;
        if (want == idx) return false;

        bool wasMax = IsZoomed(h);
        if (wasMax) ShowWindow(h, SW_RESTORE);

        RECT r;
        GetWindowRect(h, out r);
        RECT from = Info(cur).rcWork;
        RECT to = Info(mons[want]).rcWork;

        double fw = (double)(from.Right - from.Left), fh = (double)(from.Bottom - from.Top);
        double tw = (double)(to.Right - to.Left), th = (double)(to.Bottom - to.Top);

        int w = (int)Math.Round((r.Right - r.Left) * tw / fw);
        int hh = (int)Math.Round((r.Bottom - r.Top) * th / fh);
        w = Clamp(w, 120, (int)tw);
        hh = Clamp(hh, 60, (int)th);

        int x = to.Left + (int)Math.Round((r.Left - from.Left) * tw / fw);
        int y = to.Top + (int)Math.Round((r.Top - from.Top) * th / fh);
        x = Clamp(x, to.Left, to.Right - w);
        y = Clamp(y, to.Top, to.Bottom - hh);

        // Crossing monitors with different DPI makes Windows send WM_DPICHANGED,
        // and the app then resizes itself, overriding the placement. So place it,
        // let that settle, and re-apply - the second pass triggers no DPI change
        // because the window is already on the target monitor.
        bool ok = SetWindowPos(h, IntPtr.Zero, x, y, w, hh, SWP_NOZORDER | SWP_NOACTIVATE);
        for (int pass = 0; pass < 3; pass++) {
            System.Threading.Thread.Sleep(70);
            RECT now;
            GetWindowRect(h, out now);
            int dw = Math.Abs((now.Right - now.Left) - w) + Math.Abs((now.Bottom - now.Top) - hh);
            int dp = Math.Abs(now.Left - x) + Math.Abs(now.Top - y);
            if (dw <= 4 && dp <= 4) break;
            ok = SetWindowPos(h, IntPtr.Zero, x, y, w, hh, SWP_NOZORDER | SWP_NOACTIVATE);
        }
        if (wasMax) ShowWindow(h, SW_MAXIMIZE);
        return ok;
    }

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
        MakeDpiAware();
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
        if (cmd == "monitors") {
            foreach (IntPtr m in Monitors()) {
                MONITORINFO mi = Info(m);
                Console.WriteLine(string.Join("\t", new string[] {
                    mi.rcMonitor.Left.ToString(CultureInfo.InvariantCulture),
                    mi.rcMonitor.Top.ToString(CultureInfo.InvariantCulture),
                    (mi.rcMonitor.Right - mi.rcMonitor.Left).ToString(CultureInfo.InvariantCulture),
                    (mi.rcMonitor.Bottom - mi.rcMonitor.Top).ToString(CultureInfo.InvariantCulture),
                    (mi.dwFlags & 1) != 0 ? "primary" : "-",
                    mi.rcWork.Left.ToString(CultureInfo.InvariantCulture),
                    mi.rcWork.Top.ToString(CultureInfo.InvariantCulture),
                    (mi.rcWork.Right - mi.rcWork.Left).ToString(CultureInfo.InvariantCulture),
                    (mi.rcWork.Bottom - mi.rcWork.Top).ToString(CultureInfo.InvariantCulture)
                }));
            }
            return 0;
        }
        if (cmd == "movemon" && args.Length > 2) {
            IntPtr h = new IntPtr(long.Parse(args[1], CultureInfo.InvariantCulture));
            int delta = int.Parse(args[2], CultureInfo.InvariantCulture);
            if (!MoveToMonitor(h, delta)) { Console.Error.WriteLine("move failed"); return 1; }
            System.Threading.Thread.Sleep(40);
            Print(h, GetForegroundWindow());
            return 0;
        }
        if (cmd == "focus" && args.Length > 1) {
            IntPtr h = new IntPtr(long.Parse(args[1], CultureInfo.InvariantCulture));
            bool ok = Focus(h);
            if (ok) { IntPtr now = GetForegroundWindow(); Print(now, now); }
            return ok ? 0 : 1;
        }
        Console.Error.WriteLine("usage: WinCtl [list|fg|monitors|focus <hwnd>|movemon <hwnd> <delta>]");
        return 2;
    }
}
