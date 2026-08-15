// Общее состояние плеера, настройки и журнал прослушанного.
// Данные пишет цикл SMTC из фонового потока, читают трей и окно из UI-потока.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

/// <summary>Журнал в тот же файл, что и у моста — разбираться удобнее в одном месте.</summary>
public static class Diag
{
    public static void Log(string message)
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RainmeterSMTC", "bridge.log");
            File.AppendAllText(path,
                string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}{2}", DateTime.Now, message, Environment.NewLine),
                new UTF8Encoding(false));
        }
        catch { }
    }
}

// ---------------------------------------------------------------- настройки

public static class Settings
{
    static readonly Dictionary<string, string> Values =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static string _path;

    public static string Dir
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Shard Player");
        }
    }

    // UTF-8, а не UTF-16: правило про UTF-16LE относится только к файлам,
    // которые читает сам Rainmeter.
    public static void Load()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            _path = Path.Combine(Dir, "config.ini");
            if (!File.Exists(_path)) return;
            foreach (string line in File.ReadAllLines(_path, Encoding.UTF8))
            {
                string t = line.Trim();
                if (t.Length == 0 || t[0] == ';' || t[0] == '[') continue;
                int eq = t.IndexOf('=');
                if (eq <= 0) continue;
                Values[t.Substring(0, eq).Trim()] = t.Substring(eq + 1).Trim();
            }
        }
        catch { }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var sb = new StringBuilder();
            sb.AppendLine("[Shard Player]");
            foreach (var kv in Values) sb.AppendLine(kv.Key + "=" + kv.Value);
            File.WriteAllText(_path ?? Path.Combine(Dir, "config.ini"), sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }

    public static string Get(string key, string def)
    {
        string v;
        return Values.TryGetValue(key, out v) && v.Length > 0 ? v : def;
    }

    public static bool GetBool(string key, bool def)
    {
        string v = Get(key, def ? "1" : "0");
        return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public static int GetInt(string key, int def)
    {
        int v;
        return int.TryParse(Get(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
    }

    public static void Set(string key, string value) { Values[key] = value ?? ""; Save(); }
    public static void Set(string key, bool value) { Set(key, value ? "1" : "0"); }
    public static void Set(string key, int value) { Set(key, value.ToString(CultureInfo.InvariantCulture)); }
}

// ---------------------------------------------------------------- состояние

public static class PlayerState
{
    static readonly object Gate = new object();
    static readonly Queue<string> Pending = new Queue<string>();

    public static string Title = "", Artist = "", Album = "", AppId = "", CoverPath = "";
    public static int State;                 // 0 нет сессии, 1 играет, 2 пауза
    public static double PosSec, DurSec;
    public static bool CanSeek;
    public static string TrackKey = "";      // меняется при смене трека
    public static string[] Sessions = new string[0];

    /// <summary>Состояние обновилось. Приходит из фонового потока.</summary>
    public static event Action Changed;

    public static void Publish(string title, string artist, string album, string appId,
                               string cover, int state, double pos, double dur, bool canSeek,
                               string[] sessions)
    {
        bool trackChanged;
        lock (Gate)
        {
            string key = title + "|" + artist;
            trackChanged = key != TrackKey;
            Title = title; Artist = artist; Album = album; AppId = appId;
            CoverPath = cover; State = state; PosSec = pos; DurSec = dur;
            CanSeek = canSeek; TrackKey = key; Sessions = sessions ?? new string[0];
        }
        if (trackChanged) History.Note(title, artist, appId, cover);
        History.Tick(state == 1);
        var h = Changed;
        if (h != null) { try { h(); } catch { } }
    }

    /// <summary>Команда из интерфейса. Исполняется циклом SMTC на его витке.</summary>
    public static void Send(string action)
    {
        lock (Gate) Pending.Enqueue(action);
    }

    public static string Take()
    {
        lock (Gate) return Pending.Count > 0 ? Pending.Dequeue() : null;
    }

    public static double Progress
    {
        get { return DurSec > 0 ? Math.Max(0, Math.Min(1, PosSec / DurSec)) : 0; }
    }

    public static string Tooltip
    {
        get
        {
            if (State == 0) return "Shard Player — ничего не играет";
            string s = Artist.Length > 0 ? Artist + " — " + Title : Title;
            // Windows обрезает подсказку трея на 127 символах
            return s.Length > 120 ? s.Substring(0, 119) + "…" : s;
        }
    }
}

// ---------------------------------------------------------------- история

public static class History
{
    static string _pendingTitle, _pendingArtist, _pendingApp, _pendingCover;
    static int _playedTicks;
    static bool _written;
    static readonly object Gate = new object();

    static string File_ { get { return Path.Combine(Settings.Dir, "history.tsv"); } }
    static string CoverDir { get { return Path.Combine(Settings.Dir, "history"); } }

    /// <summary>Новый трек. В журнал он попадёт, только если продержится 30 секунд.</summary>
    public static void Note(string title, string artist, string appId, string cover)
    {
        lock (Gate)
        {
            _pendingTitle = title; _pendingArtist = artist;
            _pendingApp = appId; _pendingCover = cover;
            _playedTicks = 0; _written = false;
        }
    }

    /// <summary>Тик цикла. Считаем только время реального воспроизведения.</summary>
    public static void Tick(bool playing)
    {
        if (!playing) return;
        lock (Gate)
        {
            if (_written || string.IsNullOrEmpty(_pendingTitle) || _pendingTitle == "—") return;
            // цикл ходит раз в 250 мс, 30 секунд это 120 тиков
            if (++_playedTicks < 120) return;
            _written = true;
            Write(_pendingTitle, _pendingArtist, _pendingApp, _pendingCover);
        }
    }

    /// <summary>
    /// Тот же трек уже записан только что? Долгая пауза и перезапуск сбрасывают
    /// «текущий трек», и без этой проверки одно прослушивание попадало в журнал
    /// дважды.
    /// </summary>
    static bool JustLogged(string title, string artist)
    {
        try
        {
            if (!File.Exists(File_)) return false;
            var lines = File.ReadAllLines(File_, Encoding.UTF8);
            for (int i = lines.Length - 1; i >= 0 && i >= lines.Length - 3; i--)
            {
                var p = lines[i].Split('\t');
                if (p.Length < 3) continue;
                if (p[1] != Clean(artist) || p[2] != Clean(title)) continue;

                DateTime when;
                if (!DateTime.TryParse(p[0], CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out when)) return true;
                return (DateTime.Now - when).TotalMinutes < 20;
            }
        }
        catch { }
        return false;
    }

    static void Write(string title, string artist, string app, string cover)
    {
        try
        {
            if (JustLogged(title, artist)) return;

            Directory.CreateDirectory(Settings.Dir);
            Directory.CreateDirectory(CoverDir);

            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            string thumb = "";
            if (!string.IsNullOrEmpty(cover) && File.Exists(cover))
            {
                thumb = Path.Combine(CoverDir, Math.Abs((title + artist).GetHashCode()).ToString(
                    CultureInfo.InvariantCulture) + ".png");
                if (!File.Exists(thumb)) Thumbnail.Save(cover, thumb, 60);
            }

            string line = string.Join("\t", new string[] {
                stamp, Clean(artist), Clean(title), Clean(app), thumb });
            File.AppendAllText(File_, line + Environment.NewLine, new UTF8Encoding(false));
            Trim();
        }
        catch { }
    }

    static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    static void Trim()
    {
        try
        {
            int keep = Math.Max(20, Settings.GetInt("HistoryDepth", 200));
            var lines = File.ReadAllLines(File_, Encoding.UTF8);
            if (lines.Length <= keep + 50) return;

            var tail = new string[keep];
            Array.Copy(lines, lines.Length - keep, tail, 0, keep);
            File.WriteAllLines(File_, tail, new UTF8Encoding(false));

            // обложки, на которые больше никто не ссылается
            var alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string l in tail)
            {
                var parts = l.Split('\t');
                if (parts.Length >= 5 && parts[4].Length > 0) alive.Add(parts[4]);
            }
            foreach (string f in Directory.GetFiles(CoverDir, "*.png"))
                if (!alive.Contains(f)) { try { File.Delete(f); } catch { } }
        }
        catch { }
    }

    public struct Entry
    {
        public string When, Artist, Title, App, Cover;
    }

    public static List<Entry> Read(int limit)
    {
        var list = new List<Entry>();
        try
        {
            if (!File.Exists(File_)) return list;
            var lines = File.ReadAllLines(File_, Encoding.UTF8);
            for (int i = lines.Length - 1; i >= 0 && list.Count < limit; i--)
            {
                var p = lines[i].Split('\t');
                if (p.Length < 4) continue;
                list.Add(new Entry
                {
                    When = p[0],
                    Artist = p[1],
                    Title = p[2],
                    App = p[3],
                    Cover = p.Length >= 5 ? p[4] : ""
                });
            }
        }
        catch { }
        return list;
    }
}
