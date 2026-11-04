using System.Runtime.InteropServices;

namespace Pocofarm.Platform;

/// <summary>VRChat クライアント領域のデスクトップ座標とモニター情報。</summary>
public sealed record ClientGeometry(int Left, int Top, int Width, int Height, int MonLeft, int MonTop, int MonWidth, int MonHeight, string Device);

/// <summary>Win32 API ラッパー: ウィンドウ探索・前面化・クリック送信。ウィンドウサイズは変更しない。</summary>
public static partial class WinApi
{
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MonitorInfoEx
    {
        public int Size; public Rect Monitor, Work; public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MouseInput { public int Dx, Dy, MouseData, Flags, Time; public nuint ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct Input { public int Type; public MouseInput Mi; }

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowW(string? cls, string title);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWindowVisible(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWindow(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ClientToScreen(nint h, ref Point p);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetClientRect(nint h, out Rect r);
    [LibraryImport("user32.dll")] private static partial nint MonitorFromWindow(nint h, int flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint m, ref MonitorInfoEx i);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsIconic(nint h);
    [LibraryImport("user32.dll")] private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] private static partial short GetAsyncKeyState(int vk);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ShowWindow(nint h, int cmd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetForegroundWindow(nint h);
    [LibraryImport("user32.dll")] private static partial void keybd_event(byte vk, byte scan, int flags, nuint extra);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetCursorPos(out Point p);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetCursorPos(int x, int y);
    [LibraryImport("user32.dll", SetLastError = true)] private static partial uint SendInput(uint n, ref Input input, int size);
    [LibraryImport("dwmapi.dll")] private static partial int DwmGetWindowAttribute(nint h, int attr, out Rect r, int size);

    [LibraryImport("winmm.dll")] private static partial uint timeBeginPeriod(uint ms);

    /// <summary>Thread.Sleep の精度を 1ms にする(既定は約 15ms で、4ms ループが鈍る)。</summary>
    public static void HighResolutionTimer() => timeBeginPeriod(1);

    /// <summary>ハンドルが今も生きた可視ウィンドウか。FindWindow と違い他プロセスへメッセージを送らないので軽い。</summary>
    public static bool IsAlive(nint h) => h != 0 && IsWindow(h) && IsWindowVisible(h);

    /// <summary>タイトル完全一致の可視ウィンドウ。見つからなければ 0。</summary>
    public static nint FindWindow(string title)
    {
        var h = FindWindowW(null, title);
        return h != 0 && IsWindowVisible(h) ? h : 0;
    }

    /// <summary>クライアント領域の位置・サイズと、載っているモニターの矩形。</summary>
    public static ClientGeometry Geometry(nint hwnd)
    {
        var pt = new Point();
        ClientToScreen(hwnd, ref pt);
        GetClientRect(hwnd, out var rc);
        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info);
        var m = info.Monitor;
        return new(pt.X, pt.Y, rc.Right, rc.Bottom, m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top, info.Device);
    }

    /// <summary>ウィンドウの可視枠(影を除く)の左上のデスクトップ座標。WGC のフレーム原点と一致する。</summary>
    public static (int X, int Y, int W, int H)? FrameBounds(nint hwnd) =>
        DwmGetWindowAttribute(hwnd, 9, out var r, Marshal.SizeOf<Rect>()) == 0 ? (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : null;

    public static bool IsMinimized(nint hwnd) => IsIconic(hwnd);

    public static bool IsForeground(nint hwnd) => GetForegroundWindow() == hwnd;

    /// <summary>仮想キーが今押されているか。ウィンドウがどれでも前面でなくても拾える。</summary>
    public static bool KeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>前面化する。最小化時のみ復元し、最大化状態やサイズは変えない。</summary>
    public static bool EnsureForeground(nint hwnd)
    {
        if (IsForeground(hwnd)) return true;
        if (IsMinimized(hwnd)) { ShowWindow(hwnd, 9); Thread.Sleep(300); }    // SW_RESTORE: 最大化していたら最大化のまま戻る
        // Alt キー押下を挟むと SetForegroundWindow がフォーカス窃取制限に弾かれにくい
        keybd_event(0x12, 0, 0, 0);
        keybd_event(0x12, 0, 2, 0);
        SetForegroundWindow(hwnd);
        Thread.Sleep(150);
        return IsForeground(hwnd);
    }

    /// <summary>マウスカーソルが VRChat のクライアント領域内にあるか。</summary>
    public static bool CursorInside(nint hwnd)
    {
        GetCursorPos(out var p);
        var g = Geometry(hwnd);
        return p.X >= g.Left && p.X < g.Left + g.Width && p.Y >= g.Top && p.Y < g.Top + g.Height;
    }

    /// <summary>カーソルを動かさずに左クリックを送る。</summary>
    public static void Click()
    {
        foreach (var flag in new[] { 0x0002, 0x0004 })
        {
            var input = new Input { Type = 0, Mi = new MouseInput { Flags = flag } };
            if (SendInput(1, ref input, Marshal.SizeOf<Input>()) != 1)
                throw new InvalidOperationException($"SendInput failed: {Marshal.GetLastWin32Error()}");
            Thread.Sleep(12);
        }
    }

    /// <summary>スキャンコード指定でキーを押して離す(Unity は仮想キーだけだと拾わないことがある)。</summary>
    public static void KeyTap(byte vk, byte scan, int holdMs = 80)
    {
        keybd_event(vk, scan, 0x8, 0);
        Thread.Sleep(holdMs);
        keybd_event(vk, scan, 0xA, 0);
    }

    /// <summary>いま前面にあるウィンドウ(補正後に戻す用)。</summary>
    public static nint ForegroundWindow() => GetForegroundWindow();

    /// <summary>
    /// マウスを相対移動する(MOUSEEVENTF_MOVE)。VRChat のデスクトップ操作の視点移動に使う。前面のウィンドウに届くので、呼び出し側で VRChat を前面にしておくこと。
    /// 大きな量は細かく分けて送り、取りこぼしを避ける。
    /// </summary>
    public static void MouseMove(int dx, int dy)
    {
        int steps = Math.Max(1, (Math.Max(Math.Abs(dx), Math.Abs(dy)) + 99) / 100);
        int sx = 0, sy = 0;
        for (int i = 1; i <= steps; i++)
        {
            int tx = dx * i / steps, ty = dy * i / steps;
            var input = new Input { Type = 0, Mi = new MouseInput { Dx = tx - sx, Dy = ty - sy, Flags = 0x0001 } };
            if (SendInput(1, ref input, Marshal.SizeOf<Input>()) != 1)
                throw new InvalidOperationException($"SendInput failed: {Marshal.GetLastWin32Error()}");
            sx = tx; sy = ty;
            Thread.Sleep(6);
        }
    }

    /// <summary>カーソルの現在位置(デスクトップ座標)。</summary>
    public static (int X, int Y) CursorPos() { GetCursorPos(out var p); return (p.X, p.Y); }

    /// <summary>カーソルを動かしてクリックする。</summary>
    public static void ClickAt(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(120);
        Click();
    }

    public static void MoveCursor(int x, int y) => SetCursorPos(x, y);
}
