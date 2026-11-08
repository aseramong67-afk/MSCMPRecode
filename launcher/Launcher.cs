// =============================================================
// MP Recode Remastered Launcher
// 1) проверяет версию мода на GitHub (raw dist/version.txt)
// 2) докачивает свежий DLL в Mods\ (гасит игру, заменяет атомарно)
// 3) запускает mysummercar.exe и жмёт Play! в окне конфигуратора
// Без Unity/MSCLoader — чистый .NET 4 (csc).
// =============================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace MPRecodeLauncher
{
    internal static class Program
    {
        // Хост игры по умолчанию: рядом с лаунчером (launcher кладётся в корень игры)
        private static string gameDir;
        private static string repoBase; // https://raw.githubusercontent.com/<owner>/<repo>/<branch>

        private static string IniPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.ini"); }
        }

        private static string ModsDir { get { return Path.Combine(gameDir, "Mods"); } }
        private static string DllPath { get { return Path.Combine(ModsDir, "MSCMPRecode.dll"); } }
        private static string LocalVerPath { get { return Path.Combine(ModsDir, "MPRecode.version"); } }

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            // .NET по умолчанию может тащить TLS 1.0 — raw.githubusercontent требует 1.2+
            try
            {
                ServicePointManager.SecurityProtocol =
                    (SecurityProtocolType)3072 /*Tls12*/ | (SecurityProtocolType)768 /*Tls11*/;
            }
            catch (Exception) { }
            Console.WriteLine("==============================================");
            Console.WriteLine("  MP RECODE REMASTERED - LAUNCHER");
            Console.WriteLine("==============================================");

            LoadIni();
            // Аргументы перекрывают ini: --game <dir>  --repo <url>  --noupdate
            bool noUpdate = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("--game", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    gameDir = args[++i];
                if (args[i].Equals("--repo", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    repoBase = args[++i].TrimEnd('/');
                if (args[i].Equals("--noupdate", StringComparison.OrdinalIgnoreCase))
                    noUpdate = true;
            }
            gameDir = Path.GetFullPath(gameDir);

            if (!File.Exists(Path.Combine(gameDir, "mysummercar.exe")))
            {
                Console.WriteLine("[ОШИБКА] Не нашёл mysummercar.exe в: " + gameDir);
                Console.WriteLine("Пропиши путь в launcher.ini: game=<путь>");
                Exit(1);
                return 1;
            }
            Console.WriteLine("Игра: " + gameDir);

            if (noUpdate)
            {
                Console.WriteLine("Обновление пропущено (--noupdate).");
            }
            else
            {
                UpdateMod();
            }

            StartGame();
            return 0;
        }

        // ==== Обновление мода с GitHub ====
        private static void UpdateMod()
        {
            Console.WriteLine();
            Console.WriteLine("[1/3] Проверка обновлений...");
            Console.WriteLine("      хост: " + repoBase);

            string remoteVersion;
            try
            {
                remoteVersion = DownloadText(repoBase + "/dist/version.txt").Trim();
            }
            catch (Exception e)
            {
                realInner(e);
                Console.WriteLine("      GitHub недоступен или репозиторий не задан (" + shortMsg(e) + ")");
                Console.WriteLine("      Запускаю игру с текущей версией мода.");
                return;
            }
            if (remoteVersion.Length == 0)
            {
                Console.WriteLine("      На сервере нет version.txt — пропускаю обновление.");
                return;
            }

            string localVersion = ReadLocalVersion();
            Console.WriteLine("      локальная: " + localVersion + " | серверная: " + remoteVersion);

            int cmp = CompareVersions(remoteVersion, localVersion);
            if (cmp <= 0)
            {
                Console.WriteLine("      Обновление не требуется.");
                return;
            }

            Console.WriteLine("[2/3] Качаю мод " + remoteVersion + "...");
            try
            {
                byte[] dll = DownloadBytes(repoBase + "/dist/MSCMPRecode.dll");

                // Игра держит dll открытым — гасим
                KillGame(imgName: "mysummercar");
                Directory.CreateDirectory(ModsDir);

                string tmp = DllPath + ".new";
                File.WriteAllBytes(tmp, dll);
                if (File.Exists(DllPath)) File.Delete(DllPath);
                File.Move(tmp, DllPath);
                File.WriteAllText(LocalVerPath, remoteVersion, Encoding.ASCII);

                Console.WriteLine("      Установлено: " + DllPath);
            }
            catch (Exception e)
            {
                realInner(e);
                Console.WriteLine("      Ошибка скачивания (" + shortMsg(e) + ") — играю со старой версией.");
            }
        }

        private static string ReadLocalVersion()
        {
            try
            {
                if (File.Exists(LocalVerPath))
                    return File.ReadAllText(LocalVerPath).Trim();
            }
            catch (Exception) { }
            return "(нет)";
        }

        // "0.3.10" против "0.3.9": числовая сверка сегментов, фолбэк — строковая
        private static int CompareVersions(string a, string b)
        {
            try
            {
                Version va = new Version(a);
                Version vb = new Version(b);
                return va.CompareTo(vb);
            }
            catch (Exception) { }
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        // ==== Запуск игры + Play! ====
        private static void StartGame()
        {
            Console.WriteLine();
            Console.WriteLine("[3/3] Запуск игры...");

            string exe = Path.Combine(gameDir, "mysummercar.exe");
            bool already = Process.GetProcessesByName("mysummercar").Length > 0;
            if (already)
            {
                Console.WriteLine("      Игра уже запущена.");
                return;
            }

            Process p = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = gameDir
            });

            // Окно конфигуратора: жмём Play! программно (SendMessage BM_CLICK)
            for (int i = 0; i < 40; i++)
            {
                System.Threading.Thread.Sleep(300);
                IntPtr hwnd = FindPlayButton();
                if (hwnd != IntPtr.Zero)
                {
                    SendMessage(hwnd, 0x00F5 /*BM_CLICK*/, IntPtr.Zero, IntPtr.Zero);
                    Console.WriteLine("      Play! нажат.");
                    return;
                }
                // Окно уже могло уйти — играя грузится сразу (skipIntro)
                if (p != null && p.HasExited) { Console.WriteLine("      Процесс вышел (?)."); return; }
            }
            Console.WriteLine("      Окно конфигуратора не найдено (вероятно загрузка сразу в игру).");
        }

        // ==== ini: game=<dir> repo=<url> ====
        private static void LoadIni()
        {
            string self = AppDomain.CurrentDomain.BaseDirectory;

            // По умолчанию: лаунчер лежит в корне игры
            gameDir = self;
            repoBase = "https://raw.githubusercontent.com/OWNER/MSCMPRecode/main";

            try
            {
                if (File.Exists(IniPath))
                {
                    string[] lines = File.ReadAllLines(IniPath, Encoding.UTF8);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i].Trim();
                        if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("[")) continue;
                        int sep = line.IndexOf('=');
                        if (sep <= 0) continue;
                        string k = line.Substring(0, sep).Trim().ToLowerInvariant();
                        string v = line.Substring(sep + 1).Trim();
                        if (k == "game" && v.Length > 0) gameDir = v;
                        if (k == "repo" && v.Length > 0) repoBase = v.TrimEnd('/');
                    }
                }
            }
            catch (Exception) { }
        }

        // ==== Сеть ====
        private static string DownloadText(string url)
        {
            return Encoding.UTF8.GetString(DownloadBytes(url));
        }

        private static byte[] DownloadBytes(string url)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = 15000;
            req.ReadWriteTimeout = 30000;
            req.UserAgent = "MPRecode-Launcher/0.3";
            req.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] buf = new byte[16384];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                return ms.ToArray();
            }
        }

        private static void KillGame(string imgName)
        {
            try
            {
                Process[] ps = Process.GetProcessesByName(imgName);
                for (int i = 0; i < ps.Length; i++)
                {
                    try { ps[i].Kill(); ps[i].WaitForExit(5000); } catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        private static string shortMsg(Exception e)
        {
            string m = e.Message;
            if (m == null) return "";
            if (m.Length > 90) m = m.Substring(0, 90);
            return m;
        }

        private static void realInner(Exception e)
        {
            try
            {
                Exception w = e; while (w.InnerException != null) w = w.InnerException;
                if (w != e) Console.WriteLine("      причина: " + w.Message);
            }
            catch (Exception) { }
        }

        private static void Exit(int code)
        {
            Console.WriteLine("Нажми Enter для выхода...");
            Console.ReadLine();
        }

        // ==== WinAPI: кнопка Play! у процесса конфигуратора ====
        private delegate bool EnumWindowsProc(IntPtr h, IntPtr lp);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hwnd, EnumWindowsProc cb, IntPtr lp);
        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

        private static IntPtr FindPlayButton()
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr h, IntPtr lp)
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid == CurrentGamePid())
                {
                    EnumChildWindows(h, delegate(IntPtr ch, IntPtr lp2)
                    {
                        StringBuilder t = new StringBuilder(64);
                        GetWindowText(ch, t, 64);
                        if (t.ToString() == "Play!") { found = ch; return false; }
                        return true;
                    }, IntPtr.Zero);
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static uint currentGamePid = 0;

        private static uint CurrentGamePid()
        {
            if (currentGamePid != 0) return currentGamePid;
            try
            {
                // находим PID главного окна конфигуратора: топ-окно с классом/тайлом
                knownConfigPid = 0;
                EnumWindows(delegate(IntPtr h, IntPtr lp)
                {
                    StringBuilder t = new StringBuilder(64);
                    GetWindowText(h, t, 64);
                    if (t.ToString().StartsWith("My Summer Car Configuration"))
                    {
                        uint pid;
                        GetWindowThreadProcessId(h, out pid);
                        knownConfigPid = pid;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);
                currentGamePid = knownConfigPid;
            }
            catch (Exception) { }
            return currentGamePid;
        }

        private static uint knownConfigPid;
    }
}
