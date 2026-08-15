// Громкость приложения-источника (Core Audio) и глобальные сочетания клавиш.
// Ни того, ни другого SMTC не даёт — это добирается мимо него.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

// ---------------------------------------------------------------- Core Audio

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                 [MarshalAs(UnmanagedType.IUnknown)] out object iface);
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionManager2
{
    int NotImpl1(); int NotImpl2();
    int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionEnumerator
{
    int GetCount(out int count);
    int GetSession(int index, out IAudioSessionControl session);
}

[ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionControl { }

// Порядок методов обязан повторять таблицу COM целиком: сначала девять от
// IAudioSessionControl, затем два идентификатора сессии и только потом PID.
// Пропущенный метод молча читает соседний слот и возвращает мусор.
[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionControl2
{
    int GetState(out int state);
    int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
    int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
    int GetGroupingParam(out Guid group);
    int SetGroupingParam(ref Guid group, ref Guid ctx);
    int RegisterAudioSessionNotification(IntPtr notify);
    int UnregisterAudioSessionNotification(IntPtr notify);
    int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    int GetProcessId(out int pid);
    int IsSystemSoundsSession();
    int SetDuckingPreference(bool optOut);
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISimpleAudioVolume
{
    int SetMasterVolume(float level, ref Guid eventContext);
    int GetMasterVolume(out float level);
    int SetMute(bool mute, ref Guid eventContext);
    int GetMute(out bool mute);
}

/// <summary>
/// Громкость только того процесса, который сейчас играет. Системную не трогаем:
/// подменять её молча — не то, чего ждёт пользователь.
/// </summary>
public static class AppVolume
{
    static Guid _ctx = Guid.Empty;
    static DateTime _cacheStamp;
    static ISimpleAudioVolume _cached;
    static string _cachedFor = "";

    // Угадывать имя процесса по AppUserModelId бесполезно: у Яндекс Музыки
    // процесс называется «Яндекс Музыка» кириллицей, а идентификатор —
    // ru.yandex.desktop.music. Поэтому идём наоборот: перебираем живые
    // аудиосессии и ищем ту, чей путь к exe перекликается с идентификатором.

    static readonly HashSet<string> Noise = new HashSet<string> {
        "exe", "app", "com", "desktop", "windows", "microsoft", "program",
        "programs", "users", "local", "appdata", "files", "http", "https"
    };

    static List<string> Tokens(string s)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(s)) return list;
        var cur = new System.Text.StringBuilder();
        foreach (char c in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) cur.Append(c);
            else { if (cur.Length >= 4) list.Add(cur.ToString()); cur.Length = 0; }
        }
        if (cur.Length >= 4) list.Add(cur.ToString());
        list.RemoveAll(delegate (string t) { return Noise.Contains(t); });
        return list;
    }

    static int Score(string appId, string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return 0;
        string haystack = exePath.ToLowerInvariant();
        int score = 0;
        foreach (string t in Tokens(appId))
            if (haystack.Contains(t)) score++;
        return score;
    }

    static ISimpleAudioVolume Resolve(string appId)
    {
        if (string.IsNullOrEmpty(appId)) return null;

        // Перебор сессий не бесплатный — держим найденное 5 секунд
        if (_cached != null && appId == _cachedFor && (DateTime.UtcNow - _cacheStamp).TotalSeconds < 5)
            return _cached;

        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDevice device;
            if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 0 /* eConsole */, out device) != 0)
                return null;

            var iid = typeof(IAudioSessionManager2).GUID;
            object raw;
            if (device.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out raw) != 0)
                return null;

            var mgr = (IAudioSessionManager2)raw;
            IAudioSessionEnumerator sessions;
            if (mgr.GetSessionEnumerator(out sessions) != 0) return null;

            int count;
            sessions.GetCount(out count);

            ISimpleAudioVolume best = null;
            int bestScore = 0;

            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl ctl;
                if (sessions.GetSession(i, out ctl) != 0 || ctl == null) continue;

                var ctl2 = ctl as IAudioSessionControl2;
                if (ctl2 == null) continue;

                int spid;
                if (ctl2.GetProcessId(out spid) != 0 || spid <= 0) continue;

                string path = "";
                try { path = Process.GetProcessById(spid).MainModule.FileName; }
                catch
                {
                    // защищённый процесс — довольствуемся идентификатором сессии
                    try { ctl2.GetSessionIdentifier(out path); } catch { }
                }

                int score = Score(appId, path);
                if (score <= bestScore) continue;

                var vol = ctl as ISimpleAudioVolume;
                if (vol == null) continue;

                best = vol;
                bestScore = score;
            }

            if (best != null)
            {
                _cached = best; _cachedFor = appId; _cacheStamp = DateTime.UtcNow;
            }
            return best;
        }
        catch { }
        return null;
    }

    /// <summary>Громкость 0..1, либо -1 если сессию найти не удалось.</summary>
    public static float Get(string appId)
    {
        var v = Resolve(appId);
        if (v == null) return -1;
        try { float level; return v.GetMasterVolume(out level) == 0 ? level : -1; }
        catch { _cached = null; return -1; }
    }

    public static void Set(string appId, float level)
    {
        var v = Resolve(appId);
        if (v == null) return;
        try { v.SetMasterVolume(Math.Max(0, Math.Min(1, level)), ref _ctx); }
        catch { _cached = null; }
    }

    public static void Nudge(string appId, float delta)
    {
        float cur = Get(appId);
        if (cur < 0) return;
        Set(appId, cur + delta);
    }
}

// ---------------------------------------------------------------- хоткеи

/// <summary>
/// Глобальные сочетания. Регистрация может не удаться, если комбинация уже занята
/// другой программой — тогда остальные всё равно работают, а эта помечается.
/// </summary>
public static class Hotkeys
{
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    public const int WM_HOTKEY = 0x0312;

    static IntPtr _hwnd;
    static readonly Dictionary<int, string> Actions = new Dictionary<int, string>();
    static readonly List<string> Failed = new List<string>();

    public static string[] NotRegistered { get { return Failed.ToArray(); } }

    struct Spec { public string Key; public string Def; public string Action; }

    static readonly Spec[] Defaults = new Spec[] {
        new Spec { Key = "HotkeyPlayPause", Def = "Ctrl+Alt+Space", Action = "playpause" },
        new Spec { Key = "HotkeyNext",      Def = "Ctrl+Alt+Right", Action = "next" },
        new Spec { Key = "HotkeyPrev",      Def = "Ctrl+Alt+Left",  Action = "prev" },
        new Spec { Key = "HotkeyVolUp",     Def = "Ctrl+Alt+Up",    Action = "vol+" },
        new Spec { Key = "HotkeyVolDown",   Def = "Ctrl+Alt+Down",  Action = "vol-" }
    };

    public static void Register(IntPtr hwnd)
    {
        _hwnd = hwnd;
        Actions.Clear();
        Failed.Clear();
        if (!Settings.GetBool("Hotkeys", true)) return;

        int id = 0xB000;
        foreach (var spec in Defaults)
        {
            string combo = Settings.Get(spec.Key, spec.Def);
            if (combo.Equals("off", StringComparison.OrdinalIgnoreCase)) continue;

            uint mods, vk;
            if (!Parse(combo, out mods, out vk)) { Failed.Add(combo); continue; }

            if (RegisterHotKey(hwnd, id, mods | MOD_NOREPEAT, vk))
                Actions[id] = spec.Action;
            else
                Failed.Add(combo);
            id++;
        }
    }

    public static void Unregister()
    {
        if (_hwnd == IntPtr.Zero) return;
        foreach (int id in Actions.Keys) UnregisterHotKey(_hwnd, id);
        Actions.Clear();
    }

    /// <summary>Действие по идентификатору хоткея, либо null.</summary>
    public static string Lookup(int id)
    {
        string a;
        return Actions.TryGetValue(id, out a) ? a : null;
    }

    static bool Parse(string combo, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        foreach (string raw in combo.Split('+'))
        {
            string p = raw.Trim();
            if (p.Length == 0) continue;
            switch (p.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= MOD_CONTROL; continue;
                case "alt": mods |= MOD_ALT; continue;
                case "shift": mods |= MOD_SHIFT; continue;
                case "win": mods |= MOD_WIN; continue;
            }
            if (!Key(p, out vk)) return false;
        }
        return vk != 0;
    }

    static bool Key(string name, out uint vk)
    {
        vk = 0;
        switch (name.ToLowerInvariant())
        {
            case "space": vk = 0x20; return true;
            case "left": vk = 0x25; return true;
            case "up": vk = 0x26; return true;
            case "right": vk = 0x27; return true;
            case "down": vk = 0x28; return true;
            case "home": vk = 0x24; return true;
            case "end": vk = 0x23; return true;
            case "pgup": vk = 0x21; return true;
            case "pgdn": vk = 0x22; return true;
            case "insert": vk = 0x2D; return true;
            case "delete": vk = 0x2E; return true;
        }
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) { vk = c; return true; }
        }
        if (name.Length >= 2 && (name[0] == 'F' || name[0] == 'f'))
        {
            int n;
            if (int.TryParse(name.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)
                && n >= 1 && n <= 24) { vk = (uint)(0x70 + n - 1); return true; }
        }
        return false;
    }
}
