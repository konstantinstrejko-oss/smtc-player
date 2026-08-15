// Иконка в трее. Сделана на голом Shell_NotifyIcon, а не на WinForms NotifyIcon:
// тот не отдаёт колесо мыши, а колесо над иконкой — основной способ крутить
// громкость не открывая окна.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

public class TrayIcon : IDisposable
{
    // ------------------------------------------------------------ интероп

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool AppendMenu(IntPtr menu, int flags, IntPtr idOrSubmenu, string item);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")]
    static extern int TrackPopupMenuEx(IntPtr menu, int flags, int x, int y, IntPtr hwnd, IntPtr param);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    const int NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_STATE = 0x08, NIF_SHOWTIP = 0x80;
    const int NOTIFYICON_VERSION_4 = 4;

    const int WM_APP_TRAY = 0x0400 + 1024;
    const int WM_LBUTTONUP = 0x0202, WM_MBUTTONUP = 0x0208, WM_CONTEXTMENU = 0x007B;
    const int WM_MOUSEWHEEL = 0x020A, WM_LBUTTONDBLCLK = 0x0203;

    const int MF_STRING = 0x0, MF_SEPARATOR = 0x800, MF_POPUP = 0x10,
              MF_CHECKED = 0x8, MF_DISABLED = 0x2, MF_GRAYED = 0x1;
    const int TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80;

    // ------------------------------------------------------------ поля

    readonly HwndSource _source;
    readonly DispatcherTimer _timer;
    WheelHook _wheel;
    IntPtr _icon = IntPtr.Zero;
    bool _added;
    string _lastTip = "";
    int _lastFrame = -1;
    Color _accent = Color.FromArgb(120, 140, 200);
    string _accentFor = "";

    public IntPtr Handle { get { return _source.Handle; } }

    /// <summary>Клик по иконке — открыть или закрыть окно плеера.</summary>
    public event Action ToggleWindow;
    /// <summary>Пользователь выбрал «Выход».</summary>
    public event Action ExitRequested;
    /// <summary>Пользователь выбрал «История».</summary>
    public event Action HistoryRequested;
    /// <summary>Раскладка окна сменилась.</summary>
    public event Action LayoutChanged;

    public TrayIcon()
    {
        // Скрытое окно нужно как приёмник сообщений трея и хоткеев.
        var parameters = new HwndSourceParameters("Shard Player messages")
        {
            Width = 0,
            Height = 0,
            PositionX = -10000,
            PositionY = -10000,
            WindowStyle = unchecked((int)0x80000000)   // WS_POPUP
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        Add();
        Hotkeys.Register(_source.Handle);
        _wheel = new WheelHook(_source.Handle, delegate (int delta) { Do(delta > 0 ? "vol+" : "vol-"); });

        PlayerState.Changed += OnStateChanged;

        // Кольцо прогресса перерисовывается раз в секунду: чаще смысла нет,
        // а каждый кадр — это создание HICON.
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1000)
        };
        _timer.Tick += delegate { Refresh(); };
        _timer.Start();
    }

    void OnStateChanged()
    {
        // приходит из фонового потока
        _source.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Refresh));
    }

    // ------------------------------------------------------------ иконка

    NotifyIconData Base()
    {
        return new NotifyIconData
        {
            cbSize = Marshal.SizeOf(typeof(NotifyIconData)),
            hWnd = _source.Handle,
            uID = 1,
            uVersion = NOTIFYICON_VERSION_4
        };
    }

    void Add()
    {
        var data = Base();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        data.uCallbackMessage = WM_APP_TRAY;
        data.hIcon = _icon = TrayArt.BuildIcon(0, 0, _accent);
        data.szTip = "Shard Player";
        _added = Shell_NotifyIcon(NIM_ADD, ref data);

        var ver = Base();
        Shell_NotifyIcon(NIM_SETVERSION, ref ver);

        Diag.Log(_added
            ? "иконка трея добавлена, размер " + TrayArt.IconSize + "px"
            : "иконку трея добавить не удалось");
    }

    public void Refresh()
    {
        if (!_added) return;

        if (PlayerState.CoverPath != _accentFor)
        {
            _accentFor = PlayerState.CoverPath;
            _accent = Palette.Dominant(_accentFor, Color.FromArgb(120, 140, 200));
        }

        // кадр кольца — 60 положений, чаще перерисовывать нечего
        int frame = (int)Math.Round(PlayerState.Progress * 60) * 10 + PlayerState.State;
        string tip = PlayerState.Tooltip;
        if (frame == _lastFrame && tip == _lastTip) return;
        _lastFrame = frame;
        _lastTip = tip;

        IntPtr fresh = TrayArt.BuildIcon(PlayerState.Progress, PlayerState.State, _accent);

        var data = Base();
        data.uFlags = NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        data.hIcon = fresh;
        data.szTip = tip;
        Shell_NotifyIcon(NIM_MODIFY, ref data);

        TrayArt.Release(_icon);
        _icon = fresh;
    }

    // ------------------------------------------------------------ сообщения

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Hotkeys.WM_HOTKEY)
        {
            string action = Hotkeys.Lookup(wParam.ToInt32());
            if (action != null) { Do(action); handled = true; }
            return IntPtr.Zero;
        }

        if (msg != WM_APP_TRAY) return IntPtr.Zero;

        // версия 4: событие лежит в младшем слове lParam, курсор — в wParam
        int evt = (int)((long)lParam & 0xFFFF);

        if (Settings.GetBool("Debug", false))
            Diag.Log(string.Format("трей: evt=0x{0:X4} wParam=0x{1:X} lParam=0x{2:X}",
                                   evt, (long)wParam, (long)lParam));
        switch (evt)
        {
            case WM_LBUTTONUP:
            case WM_LBUTTONDBLCLK:
                Raise(ToggleWindow);
                handled = true;
                break;
            case WM_MBUTTONUP:
                Do("playpause");
                handled = true;
                break;
            case WM_CONTEXTMENU:
                ShowMenu();
                handled = true;
                break;
            // WM_MOUSEWHEEL здесь бесполезен: в схеме NOTIFYICON_VERSION_4 в
            // wParam лежат координаты курсора, а величины прокрутки нет вовсе.
            // Колесо ловится хуком — см. WheelHook.
        }
        return IntPtr.Zero;
    }

    static void Raise(Action a) { if (a != null) a(); }

    void Do(string action)
    {
        if (action == "vol+") { AppVolume.Nudge(PlayerState.AppId, +0.04f); return; }
        if (action == "vol-") { AppVolume.Nudge(PlayerState.AppId, -0.04f); return; }
        PlayerState.Send(action);
    }

    // ------------------------------------------------------------ меню

    void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        IntPtr sources = IntPtr.Zero;
        var sourceIds = new Dictionary<int, string>();

        try
        {
            int state = PlayerState.State;
            AppendMenu(menu, MF_STRING | (state == 0 ? MF_GRAYED : 0), (IntPtr)1,
                       state == 1 ? "Пауза" : "Играть");
            AppendMenu(menu, MF_STRING | (state == 0 ? MF_GRAYED : 0), (IntPtr)2, "Следующий");
            AppendMenu(menu, MF_STRING | (state == 0 ? MF_GRAYED : 0), (IntPtr)3, "Предыдущий");
            AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);

            bool vertical = Settings.Get("Layout", "horizontal") == "vertical";
            AppendMenu(menu, MF_STRING | (vertical ? 0 : MF_CHECKED), (IntPtr)10, "Горизонтальный вид");
            AppendMenu(menu, MF_STRING | (vertical ? MF_CHECKED : 0), (IntPtr)11, "Вертикальный вид");

            string[] list = PlayerState.Sessions;
            if (list.Length > 1)
            {
                sources = CreatePopupMenu();
                string prefer = Settings.Get("PreferApp", "");
                int id = 40;
                AppendMenu(sources, MF_STRING | (prefer.Length == 0 ? MF_CHECKED : 0), (IntPtr)id,
                           "Автоматически");
                sourceIds[id++] = "";
                foreach (string app in list)
                {
                    AppendMenu(sources, MF_STRING | (app == prefer ? MF_CHECKED : 0), (IntPtr)id, Short(app));
                    sourceIds[id++] = app;
                }
                AppendMenu(menu, MF_POPUP, sources, "Источник");
            }

            AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
            AppendMenu(menu, MF_STRING, (IntPtr)20, "История…");
            AppendMenu(menu, MF_STRING | (Autostart.Enabled ? MF_CHECKED : 0), (IntPtr)21,
                       "Запускать с Windows");

            string[] failed = Hotkeys.NotRegistered;
            if (failed.Length > 0)
                AppendMenu(menu, MF_STRING | MF_GRAYED, (IntPtr)99,
                           "Заняты хоткеи: " + string.Join(", ", failed));

            AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
            AppendMenu(menu, MF_STRING, (IntPtr)30, "Выход");

            POINT p;
            GetCursorPos(out p);
            SetForegroundWindow(_source.Handle);   // иначе меню не закроется по клику мимо

            int cmd = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY,
                                       p.X, p.Y, _source.Handle, IntPtr.Zero);
            Handle_(cmd, sourceIds);
        }
        finally
        {
            if (sources != IntPtr.Zero) DestroyMenu(sources);
            DestroyMenu(menu);
        }
    }

    void Handle_(int cmd, Dictionary<int, string> sourceIds)
    {
        switch (cmd)
        {
            case 1: Do("playpause"); return;
            case 2: Do("next"); return;
            case 3: Do("prev"); return;
            case 10: Settings.Set("Layout", "horizontal"); Raise(LayoutChanged); return;
            case 11: Settings.Set("Layout", "vertical"); Raise(LayoutChanged); return;
            case 20: Raise(HistoryRequested); return;
            case 21: Autostart.Enabled = !Autostart.Enabled; return;
            case 30: Raise(ExitRequested); return;
        }
        string app;
        if (sourceIds.TryGetValue(cmd, out app))
        {
            Settings.Set("PreferApp", app);
            PlayerState.Send("prefer:" + app);
        }
    }

    static string Short(string appId)
    {
        if (string.IsNullOrEmpty(appId)) return "—";
        string s = appId;
        int bang = s.IndexOf('!');
        if (bang > 0) s = s.Substring(0, bang);
        if (s.Length > 40) s = s.Substring(0, 39) + "…";
        return s;
    }

    // ------------------------------------------------------------ уборка

    public void Dispose()
    {
        _timer.Stop();
        PlayerState.Changed -= OnStateChanged;
        if (_wheel != null) { _wheel.Dispose(); _wheel = null; }
        Hotkeys.Unregister();
        if (_added)
        {
            var data = Base();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        TrayArt.Release(_icon);
        _icon = IntPtr.Zero;
        _source.Dispose();
    }
}

/// <summary>
/// Колесо над иконкой трея.
///
/// Штатного пути нет: схема NOTIFYICON_VERSION_4 сообщает о прокрутке, но не
/// передаёт её величину — в wParam лежат координаты курсора. Поэтому величина
/// берётся из низкоуровневого хука, а принадлежность прокрутки нашей иконке
/// проверяется по прямоугольнику от Shell_NotifyIconGetRect.
///
/// Если иконка спрятана под шеврон, Windows отдаёт прямоугольник самой кнопки
/// переполнения — прокрутка работает над ней, но не над иконкой внутри
/// раскрытой панели: там колесо забирает себе оболочка.
/// </summary>
public class WheelHook : IDisposable
{
    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier id, out RECT r);

    [StructLayout(LayoutKind.Sequential)]
    struct NotifyIconIdentifier { public int cbSize; public IntPtr hWnd; public int uID; public Guid guidItem; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINTL { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    struct MouseLowLevel { public POINTL pt; public int mouseData; public int flags; public int time; public IntPtr extra; }

    const int WH_MOUSE_LL = 14;
    const int WM_MOUSEWHEEL = 0x020A;

    readonly HookProc _proc;     // держим ссылку: иначе делегат соберёт GC
    readonly IntPtr _hook;
    readonly IntPtr _owner;
    readonly Action<int> _onWheel;

    RECT _rect;
    DateTime _rectStamp = DateTime.MinValue;

    public WheelHook(IntPtr ownerWindow, Action<int> onWheel)
    {
        _owner = ownerWindow;
        _onWheel = onWheel;
        _proc = Callback;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero) Diag.Log("хук колеса не установлен");
    }

    bool OverIcon(int x, int y)
    {
        // прямоугольник иконки меняется редко, а запрос не бесплатный
        if ((DateTime.UtcNow - _rectStamp).TotalSeconds > 2)
        {
            var id = new NotifyIconIdentifier { hWnd = _owner, uID = 1, guidItem = Guid.Empty };
            id.cbSize = Marshal.SizeOf(typeof(NotifyIconIdentifier));
            RECT r;
            if (Shell_NotifyIconGetRect(ref id, out r) == 0) _rect = r;
            else _rect = new RECT();
            _rectStamp = DateTime.UtcNow;
        }
        return x >= _rect.Left && x < _rect.Right && y >= _rect.Top && y < _rect.Bottom;
    }

    IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (int)wParam == WM_MOUSEWHEEL)
        {
            try
            {
                var data = (MouseLowLevel)Marshal.PtrToStructure(lParam, typeof(MouseLowLevel));
                if (OverIcon(data.pt.X, data.pt.Y))
                {
                    int delta = (short)((data.mouseData >> 16) & 0xFFFF);
                    _onWheel(delta);
                    return new IntPtr(1);   // прокрутка наша, панели задач её не отдаём
                }
            }
            catch { }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
    }
}

/// <summary>Автозапуск. По умолчанию выключен — в Run без спроса не лезем.</summary>
public static class Autostart
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Shard Player";

    public static bool Enabled
    {
        get
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key))
                    return k != null && k.GetValue(Name) != null;
            }
            catch { return false; }
        }
        set
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key, true))
                {
                    if (k == null) return;
                    if (value)
                    {
                        string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                        k.SetValue(Name, "\"" + exe + "\" -Tray 1");
                    }
                    else k.DeleteValue(Name, false);
                }
                Settings.Set("Autostart", value);
            }
            catch { }
        }
    }
}
