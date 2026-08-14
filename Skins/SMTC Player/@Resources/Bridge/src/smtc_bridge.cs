// SMTC Bridge для Rainmeter
// -----------------------------------------------------------------------------
// Читает Windows SMTC (Global System Media Transport Controls) — тот же источник,
// что рисует системный медиа-оверлей. Работает с любым приложением, которое туда
// репортит: Яндекс Музыка, Spotify, браузеры, VLC.
//
// Пишет в <DataDir>\nowplaying.txt 9 строк (их читает Rainmeter через WebParser):
//   1 Title, 2 Artist, 3 Album, 4 State (0 нет сессии / 1 играет / 2 пауза),
//   5 Position m:ss, 6 Duration m:ss, 7 Progress 0..100, 8 AppId, 9 путь к обложке
//
// Читает команды из <DataDir>\cmd.ini:
//   [Command]
//   Action=playpause|<tick>      playpause | play | pause | next | prev | seek:<0..100>
// Хвост после "|" — счётчик Rainmeter: два одинаковых нажатия подряд должны
// считаться двумя разными командами.
//
// Сборка: build.ps1 (csc из .NET Framework 4 + Windows.winmd).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

public static class SmtcBridge
{
    static string DataDir, DataFile, TmpFile, CmdFile, LogFile;
    // Канал команд лежит в папке скина, а не рядом с данными: !WriteKeyValue
    // отказывается писать за пределы каталога скинов Rainmeter («Illegal path»).
    static string CmdFileArg = "";
    static string PreferApp = "";
    static string Config = "SMTC Player\\Player";
    static int PollMs = 250;

    static GlobalSystemMediaTransportControlsSessionManager _manager;

    // ------------------------------------------------------------ IPC Rainmeter
    // WebParser этой сборки Rainmeter локальные файлы не читает (WinInet без
    // протокола file:), поэтому значения не выкладываются для опроса, а пушатся
    // в скин штатным каналом Rainmeter — WM_COPYDATA с dwData=1 (бэнг).

    const int WM_COPYDATA = 0x004A;
    const int BANG = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct CopyDataStruct
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam,
        ref CopyDataStruct lParam, uint flags, uint timeout, out IntPtr result);

    static IntPtr _rainmeterWnd = IntPtr.Zero;

    static IntPtr RainmeterWindow
    {
        get
        {
            if (_rainmeterWnd == IntPtr.Zero)
                _rainmeterWnd = FindWindow("DummyRainWClass", "Rainmeter control window");
            return _rainmeterWnd;
        }
    }

    static bool SendBang(string bang)
    {
        IntPtr hwnd = RainmeterWindow;
        if (hwnd == IntPtr.Zero) return false;

        IntPtr ptr = Marshal.StringToHGlobalUni(bang);
        try
        {
            var cds = new CopyDataStruct
            {
                dwData = new IntPtr(BANG),
                cbData = (bang.Length + 1) * 2,
                lpData = ptr
            };
            IntPtr res;
            IntPtr ok = SendMessageTimeout(hwnd, WM_COPYDATA, IntPtr.Zero, ref cds,
                                           0x0002 /* SMTO_ABORTIFHUNG */, 1000, out res);
            if (ok == IntPtr.Zero) { _rainmeterWnd = IntPtr.Zero; return false; }
            return true;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    // Кавычки внутри значения оборвали бы разбор бэнга.
    static string Quote(string value)
    {
        if (value == null) value = "";
        return "\"" + value.Replace("\"", "''") + "\"";
    }

    static readonly Dictionary<string, string> Sent = new Dictionary<string, string>();

    static void SetVar(string name, string value, bool force)
    {
        string prev;
        if (!force && Sent.TryGetValue(name, out prev) && prev == value) return;
        if (SendBang("!SetVariable " + name + " " + Quote(value) + " " + Quote(Config)))
            Sent[name] = value;
        else
            Sent.Remove(name);   // не дошло — повторим на следующем витке
    }

    // ---------------------------------------------------------------- утилиты

    static void Log(string msg)
    {
        try
        {
            if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 256 * 1024)
                File.Delete(LogFile);
            File.AppendAllText(LogFile,
                string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}{2}", DateTime.Now, msg, Environment.NewLine),
                new UTF8Encoding(false));
        }
        catch { }
    }

    static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        TimeSpan ts = TimeSpan.FromSeconds(Math.Floor(seconds));
        if (ts.TotalHours >= 1)
            return string.Format("{0}:{1:d2}:{2:d2}", (int)Math.Floor(ts.TotalHours), ts.Minutes, ts.Seconds);
        return string.Format("{0}:{1:d2}", (int)ts.TotalMinutes, ts.Seconds);
    }

    static string OneLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    // ---------------------------------------------------------------- SMTC

    static GlobalSystemMediaTransportControlsSessionManager Manager
    {
        get
        {
            if (_manager == null)
            {
                _manager = GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().Result;
                Log("SMTC manager получен");
            }
            return _manager;
        }
    }

    static GlobalSystemMediaTransportControlsSession GetSession()
    {
        var mgr = Manager;
        if (!string.IsNullOrEmpty(PreferApp))
        {
            foreach (var s in mgr.GetSessions())
                if (string.Equals(s.SourceAppUserModelId, PreferApp, StringComparison.OrdinalIgnoreCase))
                    return s;
        }

        var current = mgr.GetCurrentSession();
        if (current != null && IsPlaying(current)) return current;

        // Системная «текущая» сессия — это последняя, куда уходят медиа-клавиши,
        // и она вполне может стоять на паузе (закрытая вкладка с видео). Если так,
        // показываем то, что реально звучит.
        foreach (var s in mgr.GetSessions())
            if (IsPlaying(s)) return s;

        return current;
    }

    static bool IsPlaying(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var pb = session.GetPlaybackInfo();
            return pb != null && pb.PlaybackStatus ==
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch { return false; }
    }

    static string SaveCover(IRandomAccessStreamReference reference, int slot)
    {
        if (reference == null) return "";
        try
        {
            using (IRandomAccessStreamWithContentType stream = reference.OpenReadAsync().AsTask().Result)
            {
                if (stream == null || stream.Size == 0) return "";
                uint size = (uint)stream.Size;
                var reader = new DataReader(stream.GetInputStreamAt(0));
                reader.LoadAsync(size).AsTask().Wait();
                byte[] bytes = new byte[size];
                reader.ReadBytes(bytes);
                reader.Dispose();

                string path = Path.Combine(DataDir, "cover" + slot + ".png");
                File.WriteAllBytes(path, bytes);
                return path;
            }
        }
        catch (Exception ex)
        {
            Log("обложка не сохранена: " + ex.Message);
            return "";
        }
    }

    static void RunAction(string action, GlobalSystemMediaTransportControlsSession session)
    {
        if (session == null || string.IsNullOrEmpty(action)) return;
        try
        {
            if (action == "playpause") session.TryTogglePlayPauseAsync().AsTask().Wait();
            else if (action == "play") session.TryPlayAsync().AsTask().Wait();
            else if (action == "pause") session.TryPauseAsync().AsTask().Wait();
            else if (action == "next") session.TrySkipNextAsync().AsTask().Wait();
            else if (action == "prev") session.TrySkipPreviousAsync().AsTask().Wait();
            else if (action.StartsWith("seek:"))
            {
                double pct;
                if (double.TryParse(action.Substring(5).Replace(',', '.'),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out pct))
                {
                    var tl = session.GetTimelineProperties();
                    double len = (tl.EndTime - tl.StartTime).TotalSeconds;
                    if (len > 0)
                    {
                        pct = Math.Max(0, Math.Min(100, pct));
                        double target = tl.StartTime.TotalSeconds + len * pct / 100.0;
                        session.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(target).Ticks).AsTask().Wait();
                    }
                }
            }
            else Log("неизвестная команда: " + action);
        }
        catch (Exception ex)
        {
            Log("команда \"" + action + "\" провалилась: " + ex.Message);
        }
    }

    static string ReadCommand()
    {
        try
        {
            foreach (string line in File.ReadAllLines(CmdFile))
            {
                string t = line.Trim();
                if (t.StartsWith("Action", StringComparison.OrdinalIgnoreCase))
                {
                    int eq = t.IndexOf('=');
                    if (eq < 0) continue;
                    string val = t.Substring(eq + 1).Trim().Trim('"');
                    int bar = val.IndexOf('|');
                    if (bar >= 0) val = val.Substring(0, bar);
                    return val.Trim().ToLowerInvariant();
                }
            }
        }
        catch { }
        return "";
    }

    // ---------------------------------------------------------------- main

    // Возвращает true, если работу подхватила копия в %LOCALAPPDATA% и этот
    // процесс должен просто выйти.
    static bool RelaunchFromLocalCopy(string[] args)
    {
        try
        {
            string self = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string runDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RainmeterSMTC");
            string runPath = Path.Combine(runDir, "smtc_bridge.exe");

            if (string.Equals(self, runPath, StringComparison.OrdinalIgnoreCase))
                return false;   // уже работаем из локальной копии

            Directory.CreateDirectory(runDir);

            // старую копию нужно погасить, иначе файл занят и не перезапишется
            int me = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcessesByName("smtc_bridge"))
            {
                if (p.Id == me) continue;
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }

            for (int attempt = 0; attempt < 10; attempt++)
            {
                try { File.Copy(self, runPath, true); break; }
                catch (IOException) { Thread.Sleep(200); }
            }

            string cmdLine = "\"" + runPath + "\" " + BuildArguments(args);
            if (!StartDetached(runPath, cmdLine, runDir))
            {
                // не отвязались — пробуем обычным способом, лучше так, чем никак
                var psi = new ProcessStartInfo(runPath);
                psi.Arguments = BuildArguments(args);
                psi.UseShellExecute = false;
                psi.WorkingDirectory = runDir;
                Process.Start(psi);
            }
            return true;
        }
        catch (Exception ex)
        {
            // не вышло — не страшно, отработаем прямо отсюда
            Log("перезапуск из локальной копии не удался: " + ex.Message);
            return false;
        }
    }

    // Rainmeter держит запущенные им процессы в своём job-объекте: стоит
    // родителю выйти, как job закрывается и уносит с собой всех потомков.
    // Поэтому копию поднимаем отвязанной от job.
    const uint DETACHED_PROCESS = 0x00000008;
    const uint CREATE_NO_WINDOW = 0x08000000;
    const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string applicationName, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
        IntPtr environment, string currentDirectory, ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    static bool StartDetached(string exePath, string commandLine, string workingDir)
    {
        var si = new StartupInfo();
        si.cb = Marshal.SizeOf(typeof(StartupInfo));
        ProcessInformation pi;

        uint flags = DETACHED_PROCESS | CREATE_NO_WINDOW | CREATE_BREAKAWAY_FROM_JOB;
        bool ok = CreateProcess(exePath, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero,
                                false, flags, IntPtr.Zero, workingDir, ref si, out pi);

        if (!ok)
        {
            // job может запрещать breakaway — тогда пробуем без него
            flags = DETACHED_PROCESS | CREATE_NO_WINDOW;
            ok = CreateProcess(exePath, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero,
                               false, flags, IntPtr.Zero, workingDir, ref si, out pi);
        }

        if (ok)
        {
            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
        }
        return ok;
    }

    static string BuildArguments(string[] args)
    {
        var sb = new StringBuilder();
        foreach (string a in args)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append('"').Append(a.Replace("\"", "\\\"")).Append('"');
        }
        return sb.ToString();
    }

    static int Main(string[] args)
    {
        DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RainmeterSMTC");

        for (int i = 0; i < args.Length - 1; i++)
        {
            string a = args[i];
            if (a.Equals("-DataDir", StringComparison.OrdinalIgnoreCase) && args[i + 1].Length > 0)
                DataDir = args[i + 1];
            else if (a.Equals("-PreferApp", StringComparison.OrdinalIgnoreCase))
                PreferApp = args[i + 1];
            else if (a.Equals("-Config", StringComparison.OrdinalIgnoreCase) && args[i + 1].Length > 0)
                Config = args[i + 1];
            else if (a.Equals("-CmdFile", StringComparison.OrdinalIgnoreCase) && args[i + 1].Length > 0)
                CmdFileArg = args[i + 1];
            else if (a.Equals("-PollMs", StringComparison.OrdinalIgnoreCase))
                int.TryParse(args[i + 1], out PollMs);
        }
        if (PollMs < 50) PollMs = 250;

        Directory.CreateDirectory(DataDir);
        LogFile = Path.Combine(DataDir, "bridge.log");

        // Запущенный exe нельзя ни перезаписать, ни перенести, а лежит он в
        // папке скина — установщик Rainmeter спотыкается об это при каждом
        // обновлении («Unable to move to ...\@Backup»). Поэтому из папки скина
        // мост только переносит себя в %LOCALAPPDATA% и работает уже оттуда.
        if (RelaunchFromLocalCopy(args)) return 0;

        bool created;
        using (var mutex = new Mutex(true, "Local\\RainmeterSMTCBridge", out created))
        {
            if (!created) return 0;   // мост уже запущен

            Directory.CreateDirectory(DataDir);
            DataFile = Path.Combine(DataDir, "nowplaying.txt");
            TmpFile = Path.Combine(DataDir, "nowplaying.tmp");
            CmdFile = CmdFileArg.Length > 0 ? CmdFileArg : Path.Combine(DataDir, "cmd.ini");
            LogFile = Path.Combine(DataDir, "bridge.log");

            // !WriteKeyValue из Rainmeter умеет писать только в существующий
            // файл, поэтому канал команд создаём сами.
            if (!File.Exists(CmdFile))
            {
                string cmdDir = Path.GetDirectoryName(CmdFile);
                if (!string.IsNullOrEmpty(cmdDir)) Directory.CreateDirectory(cmdDir);
                File.WriteAllText(CmdFile, "[Command]\r\nAction=\r\n", new UTF8Encoding(false));
            }

            Log("bridge стартовал, DataDir=" + DataDir + ", config=" + Config + ", cmd=" + CmdFile);

            DateTime lastCmdStamp = File.Exists(CmdFile)
                ? File.GetLastWriteTimeUtc(CmdFile)
                : DateTime.MinValue;

            var utf8 = new UTF8Encoding(false);
            string lastPayload = "";
            string lastTrackKey = "";
            string coverPath = "";
            int coverSlot = 0;

            GlobalSystemMediaTransportControlsSessionMediaProperties props = null;
            int propsAge = int.MaxValue;
            int propsEvery = Math.Max(1, 1000 / PollMs);
            int aliveTick = 0;
            int aliveEvery = Math.Max(1, 10000 / PollMs);
            int rainmeterMissing = 0;
            int resyncTick = 0;
            int resyncEvery = Math.Max(1, 5000 / PollMs);
            bool resyncRequested = true;

            while (true)
            {
                try
                {
                    var session = GetSession();

                    // 1. команды — первым делом, чтобы клик отзывался сразу
                    if (File.Exists(CmdFile))
                    {
                        DateTime stamp = File.GetLastWriteTimeUtc(CmdFile);
                        if (stamp > lastCmdStamp)
                        {
                            lastCmdStamp = stamp;
                            string action = ReadCommand();
                            if (action == "resync")
                            {
                                // скин перезагрузился и потерял значения
                                resyncRequested = true;
                            }
                            else if (action.Length > 0)
                            {
                                RunAction(action, session);
                                propsAge = int.MaxValue;   // метаданные перечитать сразу
                            }
                        }
                    }

                    string payload;
                    string vTitle = "—", vArtist = "", vAlbum = "", vApp = "", vCover = "";
                    string vPos = "0:00", vDur = "0:00";
                    int vState = 0, vProgress = 0;

                    if (session == null)
                    {
                        payload = "—\n\n\n0\n0:00\n0:00\n0\n\n";
                        lastTrackKey = "";
                        props = null;
                    }
                    else
                    {
                        // 2. метаданные — раз в ~1 с, это единственный дорогой вызов
                        if (propsAge >= propsEvery)
                        {
                            propsAge = 0;
                            try { props = session.TryGetMediaPropertiesAsync().AsTask().Result; }
                            catch { props = null; }

                            if (props != null)
                            {
                                string key = props.Title + "|" + props.Artist;
                                if (key != lastTrackKey)
                                {
                                    lastTrackKey = key;
                                    coverSlot = 1 - coverSlot;
                                    coverPath = SaveCover(props.Thumbnail, coverSlot);
                                }
                            }
                        }
                        else propsAge++;

                        // 3. статус и позиция — дёшево, каждый тик
                        var playback = session.GetPlaybackInfo();
                        var timeline = session.GetTimelineProperties();

                        int state = 2;
                        if (playback != null && playback.PlaybackStatus ==
                            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) state = 1;

                        double posSec = (timeline.Position - timeline.StartTime).TotalSeconds;
                        double durSec = (timeline.EndTime - timeline.StartTime).TotalSeconds;

                        // Плееры (Яндекс Музыка в их числе) шлют позицию не потоком,
                        // а снимком по событиям. Пока играет — доводим её сами от
                        // отметки LastUpdatedTime, иначе прогресс-бар стоял бы колом.
                        if (state == 1 && timeline.LastUpdatedTime.Year > 1601)
                        {
                            double elapsed = (DateTimeOffset.Now - timeline.LastUpdatedTime).TotalSeconds;
                            if (elapsed > 0 && elapsed < 3600) posSec += elapsed;
                        }

                        if (durSec < 0) durSec = 0;
                        if (posSec < 0) posSec = 0;
                        if (durSec > 0 && posSec > durSec) posSec = durSec;

                        int progress = durSec > 0 ? (int)Math.Round(100 * posSec / durSec) : 0;

                        string title = props != null ? OneLine(props.Title) : "";
                        string artist = props != null ? OneLine(props.Artist) : "";
                        string album = props != null ? OneLine(props.AlbumTitle) : "";
                        if (title.Length == 0) title = "—";

                        var sb = new StringBuilder();
                        sb.Append(title).Append('\n');
                        sb.Append(artist).Append('\n');
                        sb.Append(album).Append('\n');
                        sb.Append(state).Append('\n');
                        sb.Append(FormatTime(posSec)).Append('\n');
                        sb.Append(FormatTime(durSec)).Append('\n');
                        sb.Append(progress).Append('\n');
                        sb.Append(session.SourceAppUserModelId).Append('\n');
                        sb.Append(coverPath);
                        payload = sb.ToString();

                        vTitle = title; vArtist = artist; vAlbum = album;
                        vState = state; vProgress = progress;
                        vPos = FormatTime(posSec); vDur = FormatTime(durSec);
                        vApp = session.SourceAppUserModelId; vCover = coverPath;
                    }

                    // 4. отдаём значения скину. Раз в 5 с — принудительно целиком:
                    // после !Refresh скин откатывает переменные к дефолтам.
                    bool force = false;
                    if (++resyncTick >= resyncEvery || resyncRequested)
                    {
                        resyncTick = 0;
                        resyncRequested = false;
                        force = true;
                    }

                    SetVar("Title", vTitle, force);
                    SetVar("Artist", vArtist, force);
                    SetVar("Album", vAlbum, force);
                    SetVar("State", vState.ToString(CultureInfo.InvariantCulture), force);
                    SetVar("Position", vPos, force);
                    SetVar("Duration", vDur, force);
                    SetVar("Progress", vProgress.ToString(CultureInfo.InvariantCulture), force);
                    SetVar("AppId", vApp, force);
                    SetVar("Cover", vCover, force);
                    // Пустой ImageName в Rainmeter — это ошибка, поэтому метр
                    // обложки прячется отдельным флагом, а всё, что ниже,
                    // подтягивается вверх на её высоту.
                    SetVar("CoverHidden", vCover.Length > 0 ? "0" : "1", force);
                    // именно положительное число: в скине оно вычитается —
                    // формула вида (72+-72) Rainmeter не переваривает
                    SetVar("CoverShift", vCover.Length > 0 ? "0" : "72", force);
                    SetVar("StateIcon", vState == 1 ? "Pause.png" : "Play.png", force);
                    SetVar("Header", vState == 1 ? "NOW PLAYING"
                                   : vState == 2 ? "PAUSED" : "NOTHING PLAYING", force);

                    // 5. файл — для отладки и на случай, если понадобится второй потребитель
                    if (payload != lastPayload)
                    {
                        lastPayload = payload;
                        File.WriteAllText(TmpFile, payload, utf8);
                        File.Copy(TmpFile, DataFile, true);
                    }

                    // 5. Мост поднимает сам скин, поэтому и живёт он ровно
                    // столько же: без Rainmeter больше минуты — выходим, чтобы
                    // не висеть в процессах впустую.
                    if (++aliveTick >= aliveEvery)
                    {
                        aliveTick = 0;
                        if (Process.GetProcessesByName("Rainmeter").Length == 0)
                        {
                            _rainmeterWnd = IntPtr.Zero;
                            if (++rainmeterMissing >= 6)
                            {
                                Log("Rainmeter не запущен больше минуты — выходим");
                                break;
                            }
                        }
                        else rainmeterMissing = 0;
                    }
                }
                catch (Exception ex)
                {
                    Log("цикл: " + ex.Message);
                    _manager = null;            // переподключиться к SMTC на следующем витке
                    Thread.Sleep(2000);
                }

                Thread.Sleep(PollMs);
            }
        }
        return 0;
    }
}
