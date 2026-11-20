using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// The parent holds the write side of our input pipe for the duration of wallpaper mode.
// Closing the pipe, including on a parent crash, restores the original taskbar windows.
internal static class WallpaperTaskbar
{
    delegate bool WindowVisitor(IntPtr hwnd, IntPtr unused);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowVisitor visit, IntPtr unused);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder text, int capacity);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hwnd, int command);
    static readonly string Marker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeskShelf", "taskbars-" + Process.GetCurrentProcess().SessionId + ".txt");
    static readonly Dictionary<IntPtr, int> InitiallyVisible = new Dictionary<IntPtr, int>();

    static bool IsTaskbar(IntPtr hwnd, out int pid)
    {
        pid = 0;
        var text = new StringBuilder(64);
        if (GetClassName(hwnd, text, text.Capacity) == 0) return false;
        string name = text.ToString();
        if (name != "Shell_TrayWnd" && name != "Shell_SecondaryTrayWnd") return false;
        GetWindowThreadProcessId(hwnd, out pid);
        try { using (var p = Process.GetProcessById(pid)) return string.Equals(p.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    static void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Marker));
        using (var writer = new StreamWriter(Marker + ".tmp", false, Encoding.ASCII))
            foreach (var item in InitiallyVisible)
                writer.WriteLine(item.Key.ToInt64().ToString(CultureInfo.InvariantCulture) + " " + item.Value.ToString(CultureInfo.InvariantCulture));
        if (File.Exists(Marker)) File.Replace(Marker + ".tmp", Marker, null);
        else File.Move(Marker + ".tmp", Marker);
    }

    static void Restore()
    {
        if (File.Exists(Marker))
            foreach (string line in File.ReadAllLines(Marker))
            {
                string[] parts = line.Split(' ');
                long handle; int pid;
                if (parts.Length == 2 && long.TryParse(parts[0], out handle) && int.TryParse(parts[1], out pid))
                    InitiallyVisible[new IntPtr(handle)] = pid;
            }
        foreach (var item in InitiallyVisible)
        {
            int pid;
            if (IsTaskbar(item.Key, out pid) && item.Value == pid)
                ShowWindowAsync(item.Key, 4); // SW_SHOWNOACTIVATE
        }
        InitiallyVisible.Clear();
        if (File.Exists(Marker)) File.Delete(Marker);
    }

    static void HideVisible()
    {
        var visible = new List<KeyValuePair<IntPtr,int>>();
        EnumWindows(delegate(IntPtr hwnd, IntPtr unused) {
            int pid;
            if (IsWindowVisible(hwnd) && IsTaskbar(hwnd, out pid))
                visible.Add(new KeyValuePair<IntPtr,int>(hwnd, pid));
            return true;
        }, IntPtr.Zero);
        bool changed = false;
        foreach (var bar in visible)
        {
            int pid;
            if (!InitiallyVisible.TryGetValue(bar.Key, out pid) || pid != bar.Value)
            { InitiallyVisible[bar.Key] = bar.Value; changed = true; }
        }
        if (changed) Persist();
        foreach (var bar in visible) ShowWindowAsync(bar.Key, 0); // SW_HIDE
    }

    static int Main(string[] args)
    {
        if (args.Length != 1 || (args[0] != "--hold" && args[0] != "--recover")) return 2;
        using (var mutex = new Mutex(false, @"Local\DeskShelf.WallpaperTaskbar"))
        {
            bool acquired;
            try { acquired = mutex.WaitOne(args[0] == "--hold" ? 1500 : 0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return 0;
            try {
                Restore();
                if (args[0] == "--recover") return 0;
                using (var ended = new ManualResetEvent(false))
                {
                    var listener = new Thread(delegate() {
                        try { Console.OpenStandardInput().ReadByte(); }
                        catch (IOException) { }
                        finally { ended.Set(); }
                    }) { IsBackground = true };
                    listener.Start();
                    while (!ended.WaitOne(60))
                    {
                        HideVisible();
                        if (ended.WaitOne(180)) break;
                    }
                }
                return 0;
            } catch (IOException) { return 1; }
              catch (UnauthorizedAccessException) { return 1; }
            finally {
                try { Restore(); } finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
