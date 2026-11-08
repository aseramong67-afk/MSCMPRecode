using System;
using System.Linq;
using System.Reflection;
using MSCLoader;

namespace MSCMPRecode
{
    // ==== Консольная команда "mp" ====
    public class MpCommand : ConsoleCommand
    {
        public sealed override string Name { get { return "mp"; } }
        public sealed override string Help { get { return "Сеть MP Recode (Steam): mp host | mp join <код> | mp code | mp leave | mp status | mp name <ник> | mp maxplayers N | mp bots N | mp clear | mp scanworld [радиус] | mp dumpcar"; } }
        public sealed override bool ShowInHelp { get { return true; } }

        /// <summary>Регистрация в консоли MSCLoader (совместимо с разными версиями)</summary>
        public bool Add(object owner)
        {
            try
            {
                Type t = typeof(ConsoleCommand);
                MethodInfo mi = t.GetMethod("Add", new Type[] { typeof(ConsoleCommand) });
                if (mi != null) { mi.Invoke(null, new object[] { this }); return true; }
            }
            catch (Exception e) { ModLog.Warn("Регистрация команды не удалась: " + e.Message); }
            return false;
        }

        public sealed override void Run(string[] args)
        {
            MpManager mgr = MpManager.Instance;
            if (mgr == null || args == null || args.Length == 0)
                return;

            string cmd = args[0].ToLowerInvariant();

            switch (cmd)
            {
                case "host":
                    // Создаём Steam-сессию; код придёт в чат и в буфер после ответа Steam
                    mgr.HostGame();
                    break;

                case "join":
                    {
                        if (args.Length >= 2)
                        {
                            string code = string.Join("", args.Skip(1).ToArray()).Trim();
                            MpConfig.JoinCode = code;
                            MpConfig.Save();
                            mgr.JoinGame(code);
                        }
                        else if (!string.IsNullOrEmpty(MpConfig.JoinCode))
                        {
                            mgr.JoinGame(MpConfig.JoinCode);
                        }
                        else
                        {
                            ModLog.Info("mp join <КОД> — впиши код сессии хоста (у хоста: mp code).");
                        }
                        break;
                    }

                case "code":
                    {
                        string code = MpSteam.LobbyCodeStr;
                        if (Mode1(mgr) && !string.IsNullOrEmpty(code))
                        {
                            ModLog.Info("Код сессии: " + code + " (скопирован в буфер обмена).");
                            MpManager.PushEventLocalSystem("Код сессии: " + code);
                            MpSteam.CopyToClipboard(code);
                        }
                        else
                        {
                            ModLog.Info("Ты не хост. Стань хостом: mp host");
                        }
                        break;
                    }

                case "leave":
                    mgr.Leave();
                    ModLog.Info("Ты вышел из сети.");
                    break;

                case "status":
                    ModLog.Info("Статус: " + mgr.StatusLine());
                    break;

                case "name":
                    if (args.Length >= 2)
                    {
                        MpConfig.PlayerName = string.Join(" ", args.Skip(1).ToArray()).Trim();
                        if (MpConfig.PlayerName.Length > 24) MpConfig.PlayerName = MpConfig.PlayerName.Substring(0, 24);
                        MpConfig.Save();
                        ModLog.Info("Ник теперь: " + MpConfig.PlayerName);
                    }
                    break;

                case "maxplayers":
                    {
                        int n;
                        if (args.Length >= 2 && int.TryParse(args[1], out n) && n >= 2 && n <= 16)
                        {
                            MpConfig.MaxPlayers = n;
                            MpConfig.Save();
                            ModLog.Info("Максимум игроков: " + n + " (применится при создании сессии)");
                        }
                        else
                        {
                            ModLog.Info("Использование: mp maxplayers <2..16> (сейчас: " + MpConfig.MaxPlayers + ")");
                        }
                        break;
                    }

                case "bots":
                    {
                        int n = 1;
                        if (args.Length >= 2) int.TryParse(args[1], out n);
                        if (n < 1) n = 1;
                        if (n > 8) n = 8;
                        mgr.SpawnDummyBots(n);
                        break;
                    }

                case "clear":
                    mgr.ClearDummyBots();
                    break;

                case "scanworld":
                    {
                        float radius = 40f;
                        if (args.Length >= 2) float.TryParse(args[1].Replace(",", "."), out radius);
                        mgr.ScanWorld(radius);
                        break;
                    }

                case "dumpcar":
                    mgr.DumpCar();
                    break;

                default:
                    ModLog.Info(Help);
                    break;
            }
        }

        private static bool Mode1(MpManager mgr) { return mgr != null && mgr.Mode == 1; }
    }
}
