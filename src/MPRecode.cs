using System;
using MSCLoader;
using UnityEngine;

namespace MSCMPRecode
{
    // Точка входа мода для MSCLoader
    public class MPRecodeMod : Mod
    {
        public override string ID { get { return "MSCMPRecode"; } }
        public override string Name { get { return "MP Recode Remastered"; } }
        public override string Version { get { return "0.3.0"; } }
        public override string Author { get { return "Umka"; } }
        public override string Description
        {
            get { return "Мультиплеер: Steam P2P (вход по коду сессии) + мир-синк + UI меню."; }
        }

        public override void ModSetup()
        {
            SetupFunction(Setup.OnMenuLoad, OnMenuLoad);
            SetupFunction(Setup.OnLoad, OnModLoad);
            SetupFunction(Setup.Update, OnModUpdate);
            SetupFunction(Setup.OnGUI, OnModGUI);
        }

        private void OnMenuLoad()
        {
            EnsureRoot();
        }

        // Корень мода: менеджер + меню-UI. Создаём и в фазе меню, и при входе в мир:
        // MSCLoader вызывает OnLoad только в игровых фазах — без OnMenuLoad в меню
        // наш UI просто не существует, кнопки не перехватываются.
        private static void EnsureRoot()
        {
            if (MpManager.Instance != null) return;
            GameObject go = new GameObject("MPRecodeRoot");
            go.AddComponent<MpManager>();
            go.AddComponent<MpMenuUI>();
            UnityEngine.Object.DontDestroyOnLoad(go);
        }

        private void OnModLoad()
        {
            MpConfig.Load();
            ModConsole.Print("[MPRecode] v0.3.0 загрузился. Транспорт: Steam P2P (вход по коду сессии) | ник: " + MpConfig.PlayerName);

            EnsureRoot();

            new MpCommand().Add(this);
        }

        private void OnModUpdate()
        {
            if (MpManager.Instance != null)
                MpManager.Instance.Tick();
        }

        private void OnModGUI()
        {
            if (MpManager.Instance != null)
                MpManager.Instance.DrawGUI();
        }
    }
}
