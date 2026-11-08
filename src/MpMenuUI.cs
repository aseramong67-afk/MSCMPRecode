using System;
using System.Globalization;
using UnityEngine;
using HutongGames.PlayMaker;

namespace MSCMPRecode
{
    // ==== Меню-UI мода ====
    // По мотивам BeerMP NetManager.StartMenu: кнопки главного меню — это объекты
    // Interface/Buttons/ButtonContinue и ButtonNewgame с личным PlayMakerFSM
    // (стейт "Action", переход "DOWN" -> клик-стейт, TextMesh у child).
    // Мы НЕ ломаем оригинальные действия загрузки — вставляем наш хук ПЕРВЫМ
    // действием клик-стейта (он просто запоминает режим МП на после входа в мир)
    // и меняем подписи кнопок. Плюс IMGUI-панель мода справа вверху.
    public class MpMenuUI : MonoBehaviour
    {
        private bool menuDone;

        // Ссылки на подписи кнопок (для переключения режима NewGame)
        private TextMesh continueText;
        private TextMesh newgameText;
        private string origContinue = "";
        private string origNewgame = "";

        // Состояние панели
        private bool uiInit;
        private string nickInput;
        private string joinCode;
        private bool newIsJoin = true;   // кнопка "NewGame" -> JOIN хосту
        private bool autoHost = true;    // кнопка "Continue" -> авто-хост

        private void Update()
        {
            bool inMenu = Application.loadedLevelName == "MainMenu";
            if (inMenu && !menuDone) menuDone = HookMenuButtons();
            if (!inMenu) menuDone = false; // после выхода из меню кнопки пересоздаются сценой
        }

        private bool HookMenuButtons()
        {
            try
            {
                GameObject root = GameObject.Find("Interface/Buttons");
                if (root == null) return false;

                HookButton(root.transform, "ButtonContinue", "CONTINUE + HOST",
                    delegate
                    {
                        if (autoHost) MpManager.QueuePendingConnect(1);
                    }, ref origContinue);

                HookButton(root.transform, "ButtonNewgame", "NEW GAME / JOIN",
                    delegate
                    {
                        if (newIsJoin) MpManager.QueuePendingConnect(2);
                    }, ref origNewgame);

                continueText = FindButtonText(root.transform, "ButtonContinue");
                newgameText = FindButtonText(root.transform, "ButtonNewgame");

                if (!uiInit)
                {
                    uiInit = true;
                    nickInput = MpConfig.PlayerName;
                    joinCode = MpConfig.JoinCode;
                }

                ModLog.Info("Меню: кнопки Continue/NewGame перехвачены МП-модом.");
                return true;
            }
            catch (Exception e)
            {
                ModLog.Warn("Меню-кнопки: " + e.Message);
                return true; // не повторяем попытку каждый кадр — только по возврату в меню
            }
        }

        private static TextMesh FindButtonText(Transform root, string buttonName)
        {
            Transform t = root.Find(buttonName);
            if (t == null || t.childCount == 0) return null;
            TextMesh tm = t.GetChild(0).GetComponent<TextMesh>();
            if (tm != null && tm.transform.childCount > 0)
            {
                TextMesh tm2 = tm.transform.GetChild(0).GetComponent<TextMesh>();
                if (tm2 != null) return tm2; // суб/тень — удобнее менять на нём
            }
            return tm;
        }

        // Вставляет наш хук ПЕРВЫМ в клик-стейт кнопки и меняет подпись
        private void HookButton(Transform root, string buttonName, string label, Action onClick, ref string origText)
        {
            Transform t = root.Find(buttonName);
            if (t == null) { ModLog.Warn("Меню: нет кнопки " + buttonName); return; }

            PlayMakerFSM pm = t.GetComponent<PlayMakerFSM>();
            if (pm == null || pm.Fsm == null || pm.FsmStates == null)
            {
                ModLog.Warn("Меню: нет FSM у кнопки " + buttonName);
                return;
            }

            // Стейт "Action" и переход "DOWN" -> клик-стейт (как у BeerMP)
            FsmState action = null;
            for (int i = 0; i < pm.FsmStates.Length; i++)
                if (pm.FsmStates[i].Name == "Action") { action = pm.FsmStates[i]; break; }
            if (action == null || action.Transitions == null) return;

            string clickState = null;
            for (int i = 0; i < action.Transitions.Length; i++)
                if (action.Transitions[i].EventName == "DOWN")
                {
                    clickState = action.Transitions[i].ToState;
                    break;
                }
            if (string.IsNullOrEmpty(clickState)) return;

            // Хук ВНАЧАЛЕ стейта: не ломаем оригинальную загрузку
            pm.InsertPmAction(clickState, new PmHook(onClick), 0);

            // Подпись: TextMesh у первого ребёнка (+ суб, как у BeerMP)
            if (t.childCount > 0)
            {
                TextMesh tm = t.GetChild(0).GetComponent<TextMesh>();
                if (tm != null)
                {
                    if (origText.Length == 0) origText = tm.text;
                    tm.text = label;
                    if (tm.transform.childCount > 0)
                    {
                        TextMesh tm2 = tm.transform.GetChild(0).GetComponent<TextMesh>();
                        if (tm2 != null) tm2.text = label;
                    }
                }
            }
        }

        // ==== IMGUI-панель мода (видна в главном меню) ====
        private void OnGUI()
        {
            if (Application.loadedLevelName != "MainMenu") return;

            const float W = 320f;
            const float H = 230f;
            float x = Screen.width - W - 12f;
            float y = 12f;

            GUI.Box(new Rect(x, y, W, H), "");
            GUI.Label(new Rect(x + 10, y + 6, W - 20, 20), "MP RECODE REMASTERED v0.3.0 (Steam)");

            GUI.Label(new Rect(x + 10, y + 30, 70, 20), "Ник:");
            nickInput = GUI.TextField(new Rect(x + 80, y + 28, W - 92, 20), nickInput ?? "");

            GUI.Label(new Rect(x + 10, y + 54, 70, 20), "Код сессии:");
            joinCode = GUI.TextField(new Rect(x + 80, y + 52, W - 92, 20), joinCode ?? "");

            autoHost = GUI.Toggle(new Rect(x + 10, y + 78, W - 20, 18), autoHost,
                "Continue = стать ХОСТОМ после входа");
            bool prevJoin = newIsJoin;
            newIsJoin = GUI.Toggle(new Rect(x + 10, y + 96, W - 20, 18), newIsJoin,
                "NewGame = ЗАЙТИ по коду (выкл = обычная новая игра)");

            // Мгновенная реакция подписи "NewGame"
            if (prevJoin != newIsJoin && newgameText != null)
                newgameText.text = newIsJoin ? "NEW GAME / JOIN" : (origNewgame.Length > 0 ? origNewgame : "NEW GAME");

            if (GUI.Button(new Rect(x + 10, y + 120, W - 20, 24), "СОХРАНИТЬ НАСТРОЙКИ"))
            {
                ApplySettings();
            }

            GUI.Label(new Rect(x + 10, y + 150, W - 20, 18),
                "Кнопки: ПРОДОЛЖИТЬ -> хост, НОВАЯ ИГРА -> join");

            GUI.Label(new Rect(x + 10, y + 172, W - 20, 50),
                "Хост: mp host -> код придёт в чат + буфер.\n" +
                "Клиент: впиши код в поле и войди через НОВАЯ ИГРА.\n" +
                "Консоль ~: mp join <код> / mp status / mp code");
        }

        private void ApplySettings()
        {
            try
            {
                if (!string.IsNullOrEmpty(nickInput)) MpConfig.PlayerName = nickInput.Trim();
                MpConfig.JoinCode = (joinCode ?? "").Trim();
                MpConfig.Save();
                ModLog.Info("Настройки МП сохранены из меню (ник: " + MpConfig.PlayerName +
                            ", код: " + MpConfig.JoinCode + ")");
            }
            catch (Exception e)
            {
                ModLog.Warn("Настройки из меню: " + e.Message);
            }
        }
    }
}
