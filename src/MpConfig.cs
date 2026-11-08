using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace MSCMPRecode
{
    // Конфиг мода: Mods/Config/MSCMPRecode/config.txt
    // Транспорт: Steam P2P (лобби), вход по коду сессии (у хоста код, клиент его вводит)
    public static class MpConfig
    {
        public const int TickRateDefault = 20;
        public const int MaxPlayersDefault = 8;

        // Значения по умолчанию сразу в полях — работает и без Load() (headless-тесты)
        public static int TickRate = TickRateDefault;
        public static int MaxPlayers = MaxPlayersDefault;
        public static string PlayerName = "Игрок";

        // Код сессии хоста (кнопка NewGame -> JOIN в меню)
        public static string JoinCode = "";

        public static string Folder
        {
            get
            {
                string mods = Path.Combine(UnityEngine.Application.dataPath, "..\\Mods\\Config\\MSCMPRecode");
                return mods;
            }
        }

        private static string FilePath
        {
            get { return Path.Combine(Folder, "config.txt"); }
        }

        public static void Load()
        {
            TickRate = TickRateDefault;
            MaxPlayers = MaxPlayersDefault;
            PlayerName = "Игрок";
            JoinCode = "";

            try
            {
                if (!Directory.Exists(Folder))
                    Directory.CreateDirectory(Folder);

                if (!File.Exists(FilePath))
                {
                    Save();
                    return;
                }

                string[] lines = File.ReadAllLines(FilePath, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line == null) continue;
                    line = line.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;

                    int sep = line.IndexOf('=');
                    if (sep <= 0) continue;

                    string key = line.Substring(0, sep).Trim().ToLowerInvariant();
                    string val = line.Substring(sep + 1).Trim();

                    switch (key)
                    {
                        case "tickrate": int.TryParse(val, out TickRate); break;
                        case "maxplayers": int.TryParse(val, out MaxPlayers); break;
                        case "playername": PlayerName = val.Length > 0 ? val : PlayerName; break;
                        case "joincode": JoinCode = val; break;
                        // старые ключи (tunnelhost/tunnelport/listenport/joinaddress/joinport)
                        // намеренно игнорируются — транспорт переведён на Steam
                    }
                }
            }
            catch (Exception e)
            {
                ModLog.Warn("Не удалось прочитать config.txt: " + e.Message);
            }
        }

        public static void Save()
        {
            try
            {
                if (!Directory.Exists(Folder))
                    Directory.CreateDirectory(Folder);

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# MP Recode Remastered v0.3 (Steam P2P, вход по коду сессии)");
                sb.AppendLine("# у хоста после mp host появится КОД (копируется в буфер автоматически)");
                sb.AppendLine("# клиент вписывает этот код в поле меню или конфига и входит в сессию");
                sb.AppendLine("tickrate=" + TickRate.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("maxplayers=" + MaxPlayers.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("playername=" + PlayerName);
                sb.AppendLine("joincode=" + JoinCode);

                File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception e)
            {
                ModLog.Warn("Не удалось сохранить config.txt: " + e.Message);
            }
        }
    }
}
