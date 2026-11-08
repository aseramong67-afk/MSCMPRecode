using System;
using System.Globalization;
using UnityEngine;
using HutongGames.PlayMaker;

namespace MSCMPRecode
{
    // ==== Меню-UI мода ====
    // Схема BeerMP NetManager.StartMenu/InitMenuButton:
    // - родительский FSM панели Interface/Buttons отключается;
    // - клик-стейты кнопок ButtonContinue/ButtonNewgame ЗАМЕНЯЮТСЯ ЦЕЛИКОМ
    //   своим хуком (оригинальные действия меню не стартуют);
    // - мир грузим сами: Application.LoadLevel("GAME") — так же, как BeerMP;
    // - NEW GAME -> старт мира как хост (код сессии в буфере по созданию сессии),
    //   JOIN -> диалог ввода кода -> LoadLevel + mp-подключение после загрузки.
    // Плюс IMGUI-панель мода справа вверху.
    public class MpMenuUI : MonoBehaviour
    {
        private bool menuDone;

        // Состояние панели
        private bool uiInit;
        private string nickInput;
        private bool autoHost = true;    // кнопка "NEW GAME" -> авто-хост + код в буфере

        // Окно ввода кода сессии (кнопка JOIN)
        private bool joinDialogOpen;
        private string joinCodeInput = "";
        private Rect joinRect = new Rect(0, 0, 360, 130);
        private bool joinFocused;

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

                // Схема BeerMP NetManager.StartMenu:
                // 1) родительский FSM панели отключаем
                PlayMakerFSM panelPm = root.GetComponent<PlayMakerFSM>();
                if (panelPm != null) panelPm.enabled = false;

                // 2) клик-стейты обеих кнопок заменяем ЦЕЛИКОМ своим хуком
                //    (original actions меню не вызываются — мир грузим сами LoadLevel("GAME"))
                InitMenuButton(root.transform.Find("ButtonContinue"), "NEW GAME", delegate
                {
                    // Хост: старт мира, сессия создаётся после входа, код копируется в буфер
                    if (autoHost) MpManager.QueuePendingConnect(1);
                    Application.LoadLevel("GAME");
                });

                InitMenuButton(root.transform.Find("ButtonNewgame"), "JOIN", delegate
                {
                    joinCodeInput = MpConfig.JoinCode;
                    joinDialogOpen = true;
                    joinFocused = false;
                    // Сам вход в мир происходит в диалоге по «ВОЙТИ» (LoadLevel + pendingConnect)
                });

                if (!uiInit)
                {
                    uiInit = true;
                    nickInput = MpConfig.PlayerName;
                }

                ModLog.Info("Меню: кнопки NEW GAME/JOIN по схеме BeerMP (LoadLevel(\"GAME\")).");
                return true;
            }
            catch (Exception e)
            {
                ModLog.Warn("Меню-кнопки: " + e.Message);
                return true; // не повторяем попытку каждый кадр — только по возврату в меню
            }
        }

        // Точная копия BeerMP InitMenuButton: заменяет действия и переходы клик-стейта
        private static void InitMenuButton(Transform parent, string name, Action click)
        {
            if (parent == null) { ModLog.Warn("Меню: нет кнопки " + name); return; }

            PlayMakerFSM pm = parent.GetComponent<PlayMakerFSM>();
            if (pm == null || pm.FsmStates == null || pm.FsmStates.Length == 0 || pm.FsmEvents == null)
            {
                ModLog.Warn("Меню: нет FSM у кнопки " + name);
                return;
            }

            FsmState action = null;
            for (int i = 0; i < pm.FsmStates.Length; i++)
                if (pm.FsmStates[i].Name == "Action") { action = pm.FsmStates[i]; break; }
            if (action == null || action.Transitions == null) { ModLog.Warn("Меню: нет стейта Action у " + name); return; }

            string clickStateName = null;
            for (int i = 0; i < action.Transitions.Length; i++)
                if (action.Transitions[i].EventName == "DOWN") { clickStateName = action.Transitions[i].ToState; break; }
            if (string.IsNullOrEmpty(clickStateName)) { ModLog.Warn("Меню: нет перехода DOWN у " + name); return; }

            FsmState clickState = null;
            for (int i = 0; i < pm.FsmStates.Length; i++)
                if (pm.FsmStates[i].Name == clickStateName) { clickState = pm.FsmStates[i]; break; }
            if (clickState == null) { ModLog.Warn("Меню: нет клик-стейта " + clickStateName); return; }

            // Переходы клик-стейта: только FINISHED -> начальный стейт
            FsmEvent finished = null;
            for (int i = 0; i < pm.FsmEvents.Length; i++)
                if (pm.FsmEvents[i].Name == "FINISHED") { finished = pm.FsmEvents[i]; break; }
            if (finished == null) { ModLog.Warn("Меню: нет события FINISHED у " + name); return; }

            clickState.Transitions = new FsmTransition[]
            {
                new FsmTransition
                {
                    FsmEvent = finished,
                    ToState = pm.FsmStates[0].Name
                }
            };
            clickState.Actions = new FsmStateAction[]
            {
                new PmHook(click),
                new HutongGames.PlayMaker.Actions.Wait { time = 0.2f }
            };

            // Подписи: TextMesh у первого ребёнка (+ суб/тень)
            if (parent.childCount > 0)
            {
                TextMesh tm = parent.GetChild(0).GetComponent<TextMesh>();
                if (tm != null)
                {
                    tm.text = name;
                    if (tm.transform.childCount > 0)
                    {
                        TextMesh tm2 = tm.transform.GetChild(0).GetComponent<TextMesh>();
                        if (tm2 != null) tm2.text = name;
                    }
                }
            }

            ModLog.Info("Меню: кнопка '" + name + "' перепристроена (клик-стейт " + clickStateName + ").");
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

            autoHost = GUI.Toggle(new Rect(x + 10, y + 56, W - 20, 18), autoHost,
                "NEW GAME = создать сессию после входа");

            if (GUI.Button(new Rect(x + 10, y + 80, W - 20, 24), "СОХРАНИТЬ НАСТРОЙКИ"))
            {
                ApplySettings();
            }

            GUI.Label(new Rect(x + 10, y + 110, W - 20, 18),
                "Кнопки: NEW GAME -> хост, JOIN -> по коду");

            GUI.Label(new Rect(x + 10, y + 132, W - 20, 92),
                "Хост: NEW GAME -> код придёт в чат\nи сам скопируется в буфер.\n" +
                "Клиент: JOIN -> впиши код -> вход.\n" +
                "Консоль ~: mp host / mp join <код> / mp code");
            if (joinDialogOpen)
            {
                // Esc — отмена
                if (Event.current.type == EventType.keyDown && Event.current.keyCode == KeyCode.Escape)
                    joinDialogOpen = false;

                joinRect = new Rect((Screen.width - joinRect.width) / 2f,
                                    (Screen.height - joinRect.height) / 2f, joinRect.width, joinRect.height);
                GUI.Box(joinRect, "");
                GUI.Label(new Rect(joinRect.x + 12, joinRect.y + 8, joinRect.width - 24, 20),
                    "КОД СЕССИИ ХОСТА (NEW GAME у друга):");

                GUI.SetNextControlName("mpjoincode");
                joinCodeInput = GUI.TextField(new Rect(joinRect.x + 12, joinRect.y + 34, joinRect.width - 24, 22), joinCodeInput ?? "");
                if (!joinFocused)
                {
                    joinFocused = true;
                    GUI.FocusControl("mpjoincode");
                }

                bool tryJoin = GUI.Button(new Rect(joinRect.x + 12, joinRect.y + 66, 160, 26), "ВОЙТИ");
                if (Event.current.type == EventType.keyDown && Event.current.keyCode == KeyCode.Return)
                    tryJoin = true;
                if (tryJoin)
                {
                    string code = (joinCodeInput ?? "").Trim();
                    if (code.Length > 0)
                    {
                        MpConfig.JoinCode = code;
                        MpConfig.Save();
                        MpManager.QueuePendingConnect(2);
                        joinDialogOpen = false;
                        // Вход в мир (свой сейв) — после загрузки TryApplyPendingConnect подключится по коду
                        Application.LoadLevel("GAME");
                    }
                }

                if (GUI.Button(new Rect(joinRect.x + 184, joinRect.y + 66, joinRect.width - 196, 26), "ОТМЕНА"))
                {
                    joinDialogOpen = false;
                }

                GUI.Label(new Rect(joinRect.x + 12, joinRect.y + 98, joinRect.width - 24, 18),
                    "Код появится у хоста после mp host / NEW GAME (Esc — отмена)");
            }
        }

        private void ApplySettings()
        {
            try
            {
                if (!string.IsNullOrEmpty(nickInput)) MpConfig.PlayerName = nickInput.Trim();
                MpConfig.Save();
                ModLog.Info("Настройки МП сохранены из меню (ник: " + MpConfig.PlayerName + ")");
            }
            catch (Exception e)
            {
                ModLog.Warn("Настройки из меню: " + e.Message);
            }
        }
    }
}
