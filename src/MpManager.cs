using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UnityEngine;

namespace MSCMPRecode
{
    // ==== Главный менеджер мультиплеера (живёт в игре от загрузки до выхода) ====
    public class MpManager : MonoBehaviour
    {
        private static MpManager instance;
        private static readonly object evLock = new object();
        private static readonly Queue<NetEvent> events = new Queue<NetEvent>();

        private MpSteamHost host { get { return MpSteam.Host; } }
        private MpSteamClient client { get { return MpSteam.Client; } }

        // Режим: 0 = офлайн, 1 = хост (Steam-лобби), 2 = клиент по коду сессии
        public int Mode;

        private double nextSendAt;
        private Transform localPlayer;
        private float findPlayerCooldown;
        private int findFrameSkip;

        // Аватары удалённых игроков
        private readonly Dictionary<short, MPAvatar> avatars = new Dictionary<short, MPAvatar>();
        private readonly Dictionary<short, string> names = new Dictionary<short, string>();

        // Локальные действия игроков (синхронизация интерактивности)
        private Transform prevParent;                 // для детекта посадки в машину
        private bool localInCar;                      // сидим в машине?
        private bool localSmoking;
        private bool localDrinking;
        private float wheelChecksum = float.MinValue; // последний разосланный угол руля
        private bool carSteerFound;                   // найден ли руль сатсы
        private Transform carSteer;                   // трансформ руля
        private double nextSweepAt;                   // таймер демонтажа затерявшихся аватаров
        private readonly List<short> sweepBuffer = new List<short>();

        // Косметические боты (видимы при любом режиме)
        private readonly List<GameObject> dummyBots = new List<GameObject>();

        // Чат
        private readonly List<ChatLine> chatLog = new List<ChatLine>();
        private bool chatOpen;
        private string chatInput = "";

        public static MpManager Instance { get { return instance; } }

        private void Awake()
        {
            instance = this;
            DontDestroyOnLoad(gameObject);
            ModLog.EnableQueueMode();
            ModLog.Drain();
            ModLog.Info("Менеджер MP готов.");
        }

        // ==== Очередь сетевых событий (потокобезопасно) ====
        public static void PushEventState(NetPlayerState st)
        {
            NetEvent e = new NetEvent();
            e.Type = Packet.PlayerState;
            e.State = st;
            Push(e);
        }

        public static void PushEventChat(short pid, string text)
        {
            NetEvent e = new NetEvent();
            e.Type = Packet.Chat;
            e.Pid = pid;
            e.Text = text;
            Push(e);
        }

        public static void PushEventLeft(short pid)
        {
            NetEvent e = new NetEvent();
            e.Type = Packet.PlayerLeft;
            e.Pid = pid;
            Push(e);
        }

        public static void PushEventAction(short pid, byte[] body)
        {
            // body: [pid:2][action:1][objectId:2][value:4]
            try
            {
                NetEvent e = new NetEvent();
                e.Type = Packet.Action;
                e.Pid = pid;
                e.Action = body[2];
                e.ObjectId = (short)(body[3] | (body[4] << 8));
                e.Value = BitConverter.ToSingle(body, 5);
                Push(e);
            }
            catch (Exception) { }
        }

        public static void PushEventList(List<PlayerListEntry> list)
        {
            NetEvent e = new NetEvent();
            e.Type = Packet.PlayerList;
            e.List = list;
            Push(e);
        }

        public static void PushEventLocalSystem(string text)
        {
            NetEvent e = new NetEvent();
            e.Type = Packet.SystemNotice;
            e.Text = text;
            Push(e);
        }

        public static void PushEventWorld(byte kind, uint objHash, uint evHash, float[] f, string text)
        {
            NetEvent e = new NetEvent();
            e.Type = Packet.WorldEvent;
            e.Action = kind;
            e.ObjHash = objHash;
            e.EvHash = evHash;
            e.F = f;
            e.Text = text;
            Push(e);
        }

        // Хост закрыл сессию / пропал (вызывается из MpSteam на LobbyChatUpdate)
        public static void PushEventHostLost()
        {
            NetEvent e = new NetEvent();
            e.Type = Packet.HostLost;
            Push(e);
        }

        private static void Push(NetEvent e)
        {
            lock (evLock) { events.Enqueue(e); }
        }

        // ==== Управление сессией ====
        public string StatusLine()
        {
            switch (Mode)
            {
                case 1:
                    {
                        MpSteamHost h = host;
                        int players = h != null ? 1 + h.MemberCount : 1;
                        return "ХОСТ (Steam) | игроков: " + players + " | код: " + MpSteam.LobbyCodeStr;
                    }
                case 2:
                    {
                        MpSteamClient c = client;
                        bool ok = c != null && c.MyPid >= 0;
                        return "КЛИЕНТ (Steam" + (ok ? ", подключён" : ", жду хоста...") + ") | pid " + (c != null ? c.MyPid : -1) +
                               " | хост: " + (c != null ? c.HostName : "?");
                    }
                default:
                    return "ОФФЛАЙН (mp host / mp join <код>)";
            }
        }

        // ==== Применение отложенного режима из меню (после реального входа в мир) ====
        private void TryApplyPendingConnect()
        {
            if (pendingConnect == 0) return;
            if (localPlayer == null) return;

            int mode = pendingConnect;
            pendingConnect = 0;

            if (mode == 1 && Mode == 0)
            {
                HostGame();
            }
            else if (mode == 2 && Mode == 0)
            {
                if (string.IsNullOrEmpty(MpConfig.JoinCode))
                    AddChat(-1, "МП: код сессии не задан. Впиши код хоста в панели меню или mp join <код>.");
                else
                    JoinGame(MpConfig.JoinCode);
            }
        }

        public void HostGame()
        {
            if (Mode == 2) Leave();
            Mode = 1;
            names[0] = MpConfig.PlayerName + " (хост)";
            MpSteam.EnsureInit();
            MpSteam.HostCreate(MpConfig.MaxPlayers);
            AddChat(-1, "Создаю Steam-сессию... код появится в чате.");
            ModLog.Info("Хост. Клиенты заходят по коду сессии.");
        }

        public void JoinGame(string sessionCode)
        {
            if (Mode == 1) Leave();
            Mode = 2;
            MpSteam.EnsureInit();
            MpSteam.ClientJoin(sessionCode);
            AddChat(-1, "Подключаюсь по коду " + sessionCode + " ...");
        }

        public void Leave()
        {
            if (Mode == 2)
            {
                MpWorldSync.RestoreClient();
            }
            MpSteam.LeaveLobby();
            Mode = 0;
            ClearAllAvatars();
            names.Clear();
        }

        // Отложенный МП-режим из меню (0 = нет, 1 = хост, 2 = join): применяем после входа в мир
        private int pendingConnect;

        public static void QueuePendingConnect(int mode)
        {
            MpManager m = Instance;
            if (m == null) return;
            m.pendingConnect = mode;
            ModLog.Info("Авто-МП: после входа в мир применю режим " + (mode == 1 ? "ХОСТ" : "JOIN"));
        }

        // ==== Главный такт (вызывается из мода каждый кадр) ====
        public void Tick()
        {
            ModLog.Drain();     // С2: лог из фоновых потоков печатаем в главном

            // Steam: коллбэки лобби/P2P + приём пакетов (все в главном потоке)
            MpSteam.EnsureInit();
            MpSteam.Pump();

            ProcessEvents();
            SweepDeadHostMembers();
            SweepStaleAvatars();

            // Ищем локального игрока FPS-объекта (Fsy), пока не найдём
            if (localPlayer == null)
            {
                findFrameSkip++;
                if (findFrameSkip >= 30)
                {
                    findFrameSkip = 0;
                    GameObject p = GameObject.Find("Fsy");
                    if (p != null)
                    {
                        localPlayer = p.transform;
                        // Сцена загружена — привязываем FSM мира (время/погода)
                        MpWorldSync.PlayerReady(Mode);
                        TryApplyPendingConnect();
                    }
                }
            }

            // Хост следит за временем и погодой и раздаёт изменения
            if (Mode == 1) MpWorldSync.HostPoll();

            LocalActionScan();

            // Рассылка состояния с частотой tickrate
            double period = 1.0 / Math.Max(1, MpConfig.TickRate);
            bool active = (Mode == 1) || (Mode == 2 && client != null && client.MyPid >= 0);
            if (active && Time.realtimeSinceStartup >= nextSendAt)
            {
                nextSendAt = Time.realtimeSinceStartup + (float)period;

                Vector3 pos = Vector3.zero;
                float rot = 0f;
                if (localPlayer != null)
                {
                    pos = localPlayer.position;
                    rot = localPlayer.eulerAngles.y;
                }
                else if (Camera.main != null)
                {
                    pos = Camera.main.transform.position;
                    rot = Camera.main.transform.eulerAngles.y;
                }
                if (localPlayer != null || Camera.main != null)
                {
                    byte flags = localInCar ? (byte)PlayerFlags.InCar : (byte)0;
                    if (Mode == 1)
                    {
                        if (host != null && host.MemberCount > 0)
                            host.BroadcastSelfState(pos.x, pos.y, pos.z, rot, flags);
                    }
                    else
                        client.SendState(client.MyPid, pos.x, pos.y, pos.z, rot, flags);
                }
            }
        }

        // ==== Седение/вставание/курение/питьё/руль — детект и рассылка ====
        private void LocalActionScan()
        {
            if (localPlayer == null || !(Mode == 1 || (Mode == 2 && client != null && client.MyPid >= 0))) return;

            // 1) Посадка в машину / выход: смотрим на смену родителя у Fsy
            Transform par = localPlayer.parent;
            if (!ReferenceEquals(par, prevParent))
            {
                if (par != null)
                {
                    localInCar = true;
                    // Хеш имени КОРНЯ машины — стабильный между игроками
                    Transform root = par;
                    while (root.parent != null) root = root.parent;
                    short carId = MpWorldSync.ShortId(root.gameObject.name);
                    SendActionToHost(Actions.SitIn, carId, 0f);
                    carSteer = FindSteer(par);
                    carSteerFound = carSteer != null;
                }
                else
                {
                    localInCar = false;
                    carSteerFound = false;
                    SendActionToHost(Actions.SitOut, 0, 0f);
                }
                prevParent = par;
            }

            // 2) Курение (T) и питьё воды (G) — по клавишам
            if (Input.GetKeyDown(KeyCode.T))
            {
                localSmoking = !localSmoking;
                SendActionToHost(localSmoking ? Actions.SmokeOn : Actions.SmokeOff, 0, 0f);
                AddChat(-1, localSmoking ? "Куришь (видно остальным)" : "Сигарета потушена");
            }
            if (Input.GetKeyDown(KeyCode.G))
            {
                localDrinking = !localDrinking;
                SendActionToHost(localDrinking ? Actions.DrinkOn : Actions.DrinkOff, 0, 0f);
                AddChat(-1, localDrinking ? "Пьёшь воду (видно остальным)" : "Не пьёшь");
            }

            // 3) Руль: угол руля сатсы, рассылаем при изменении > 3°
            if (carSteerFound && carSteer != null)
            {
                float ang = carSteer.eulerAngles.z;
                if (wheelChecksum == float.MinValue || Mathf.Abs(Mathf.DeltaAngle(wheelChecksum, ang)) > 3f)
                {
                    wheelChecksum = ang;
                    SendActionToHost(Actions.Wheel, 0, ang);
                }
            }
        }

        private void SendActionToHost(byte action, short objectId, float value)
        {
            if (Mode == 1)
            {
                if (host != null && host.MemberCount > 0) host.HostAction(action, objectId, value);
            }
            else if (Mode == 2 && client != null && client.MyPid >= 0)
                client.SendAction(action, objectId, value);
        }

        // Ищем руль в арматуре машины
        private static Transform FindSteer(Transform carRoot)
        {
            if (carRoot == null) return null;
            Transform[] all = carRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                string n = all[i].name.ToLowerInvariant();
                if (n.Contains("steering"))
                    return all[i];
            }
            return null;
        }

        // Демонтаж затерявшихся аватаров, если PlayerLeft потерялся
        private void SweepStaleAvatars()
        {
            if (Time.realtimeSinceStartup < nextSweepAt) return;
            nextSweepAt = Time.realtimeSinceStartup + 1f;

            sweepBuffer.Clear();
            foreach (KeyValuePair<short, MPAvatar> kv in avatars)
            {
                if (kv.Key >= 900) continue; // боты живут своей жизнью
                if (Time.realtimeSinceStartup - kv.Value.LastSeen > 8f)
                    sweepBuffer.Add(kv.Key);
            }
            for (int i = 0; i < sweepBuffer.Count; i++)
            {
                ModLog.Info("Аватар #" + sweepBuffer[i] + " удалён по таймауту (пакетов нет > 8 сек).");
                RemoveAvatar(sweepBuffer[i]);
            }
        }
        // Раз в 2с: проверка живости P2P-сессий у Steam-хоста
        private void SweepDeadHostMembers()
        {
            if (Mode != 1 || host == null) return;
            if (Time.realtimeSinceStartup < nextDeadSweepAt) return;
            nextDeadSweepAt = Time.realtimeSinceStartup + 2f;
            host.SweepDead();
        }
        private float nextDeadSweepAt;

        private void ProcessEvents()
        {
            while (true)
            {
                NetEvent e;
                lock (evLock)
                {
                    if (events.Count == 0) return;
                    e = events.Dequeue();
                }

                switch (e.Type)
                {
                    case Packet.PlayerState:
                        HandleRemoteState(e.State);
                        break;
                    case Packet.Action:
                        HandleRemoteAction(e.Pid, e.Action, e.ObjectId, e.Value);
                        break;
                    case Packet.WorldEvent:
                        HandleWorld(e.Action, e.ObjHash, e.EvHash, e.F, e.Text);
                        break;
                    case Packet.Chat:
                        AddChat(e.Pid, e.Text);
                        break;
                    case Packet.PlayerLeft:
                        RemoveAvatar(e.Pid);
                        if (names.ContainsKey(e.Pid)) names.Remove(e.Pid);
                        break;
                    case Packet.PlayerList:
                        if (e.List != null)
                        {
                            names.Clear();
                            for (int i = 0; i < e.List.Count; i++)
                                names[e.List[i].Pid] = e.List[i].Name;
                            ReconcileAvatars(e.List);
                        }
                        break;
                    case Packet.SystemNotice:
                        AddChat(-1, e.Text);
                        break;
                    case Packet.HostLost:
                        HandleHostLost();
                        break;
                }
            }
        }

        // Хост пропал/закрыл сессию — чистим мир на клиенте
        private void HandleHostLost()
        {
            AddChat(-1, "Хост закрыл сессию. Отключаюсь.");
            ModLog.Warn("Хост пропал: чистка аватаров, переход в офлайн.");
            MpWorldSync.RestoreClient();
            ClearAllAvatars();
            names.Clear();
            Mode = 0;
        }

        private void HandleRemoteState(NetPlayerState st)
        {
            if (st.Pid < 0) return;
            if (client != null && client.MyPid >= 0 && st.Pid == client.MyPid) return; // свой эхо-пакет

            MPAvatar av;
            if (!avatars.TryGetValue(st.Pid, out av))
            {
                av = MPAvatar.Create(st.Pid);
                av.TargetRotY = st.RotY;
                av.transform.position = new Vector3(st.X, st.Y, st.Z);
                avatars[st.Pid] = av;
                string nm;
                string shown = names.TryGetValue(st.Pid, out nm) ? nm : ("Игрок#" + st.Pid);
                av.Name = shown;
                AddChat(-2, shown + " вошёл в игру");
            }

            av.TargetPos = new Vector3(st.X, st.Y, st.Z);
            av.TargetRotY = st.RotY;
            av.LerpSpeed = 10f;
            av.LastSeen = Time.realtimeSinceStartup;
        }

        private void RemoveAvatar(short pid)
        {
            MPAvatar av;
            if (avatars.TryGetValue(pid, out av))
            {
                string nm = av.Name;
                AddChat(-2, nm + " покинул игру");
                av.gameObject.SetActive(false);
                Destroy(av.gameObject);
                avatars.Remove(pid);
            }
        }

        // Сверка аватаров со справочником имён: убираем призраков (rev: К3)
        private void ReconcileAvatars(List<PlayerListEntry> list)
        {
            bool changed = false;
            List<short> toRemove = new List<short>();
            foreach (KeyValuePair<short, MPAvatar> kv in avatars)
            {
                short pid = kv.Key;
                if (pid >= 900) continue; // локальные боты не подлежат
                bool found = false;
                for (int i = 0; i < list.Count; i++)
                    if (list[i].Pid == pid) { found = true; break; }

                if (found)
                {
                    // обновляем имя, если переименовался
                    kv.Value.Name = names.ContainsKey(pid) ? names[pid] : kv.Value.Name;
                }
                else toRemove.Add(pid);
            }
            for (int i = 0; i < toRemove.Count; i++)
            {
                RemoveAvatar(toRemove[i]);
                changed = true;
            }
            if (changed) ModLog.Info("Чистка призраков: удалено " + toRemove.Count);
        }

        // ==== Применение мировых записей (клиент применяет, хост — источник правды) ====
        private void HandleWorld(byte kind, uint objHash, uint evHash, float[] f, string text)
        {
            if (Mode == 1) return; // у хоста свои значения
            MpWorldSync.Apply(kind, objHash, evHash, f, text);

            // Краткое упоминание в чате — только для «случайных» событий из каталога
            if (kind == MpWorldSync.WE_FSM_EVENT)
                AddChat(-2, "Мир: " + MpWorldSync.DescribeEvent(evHash) + (string.IsNullOrEmpty(text) ? "" : " (" + text + ")"));
        }
        // ==== Действия других игроков: сидение/курение/питьё/руль/приветствие ====
        private void HandleRemoteAction(short pid, byte action, short objectId, float value)
        {
            MPAvatar av;
            if (!avatars.TryGetValue(pid, out av)) return;

            string nm = NameOf(pid);
            switch (action)
            {
                case Actions.SitIn:
                    av.InCar = true;
                    av.AnchorCar = ResolveCar(objectId);
                    AddChat(-2, nm + " сел(а) в машину [объект " + objectId + "]");
                    break;
                case Actions.SitOut:
                    av.InCar = false;
                    av.AnchorCar = null;
                    AddChat(-2, nm + " вышел(ла) из машины");
                    break;
                case Actions.SmokeOn:
                    av.Smoking = true;
                    AddChat(-2, nm + " закурил(а)");
                    break;
                case Actions.SmokeOff:
                    av.Smoking = false;
                    AddChat(-2, nm + " потушил(а) сигарету");
                    break;
                case Actions.DrinkOn:
                    av.Drinking = true;
                    AddChat(-2, nm + " пьёт воду");
                    break;
                case Actions.DrinkOff:
                    av.Drinking = false;
                    AddChat(-2, nm + " перестал(а) пить");
                    break;
                case Actions.Wheel:
                    av.WheelAngle = value;
                    break;
                case Actions.WaveHi:
                    AddChat(-2, nm + " помахал(а) рукой");
                    break;
            }
        }

        // Поиск локальной машины по короткому id (хеш имени) — имена стабильны между игроками
        private readonly Dictionary<short, Transform> carRegistry = new Dictionary<short, Transform>();

        private Transform ResolveCar(short carId)
        {
            Transform t;
            if (carRegistry.TryGetValue(carId, out t) && t != null) return t;
            double lim = 0; // не ограничиваем по времени: вызов редкий
            GameObject[] all = GameObject.FindObjectsOfType(typeof(GameObject)) as GameObject[];
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                if (MpWorldSync.ShortId(all[i].name) == carId)
                {
                    carRegistry[carId] = all[i].transform;
                    return all[i].transform;
                }
            }
            ModLog.Warn(" не нашли машину с объектным id* " + carId + " (задано: " + lim + ")");
            return null;
        }

        private void ClearAllAvatars()
        {
            List<short> keys = new List<short>(avatars.Keys);
            for (int i = 0; i < keys.Count; i++) RemoveAvatar(keys[i]);
        }

        // Ссылки на менеджер/сеть для передачи мировых событий
        private MpSteamHost HostRef { get { return instance != null ? instance.host : null; } }

        // ==== Отправка мирового события (только хост, раздаёт всем) ====
        internal void WorldReport(byte kind, uint objHash, uint evHash, float[] f, string text)
        {
            SendWorld(kind, objHash, evHash, f, text);
        }

        internal void WorldReportPos(string objName, Transform t)
        {
            SendWorld(MpWorldSync.WE_OBJ_POS, MpWorldSync.Hash(objName), 0,
                      new float[] { t.position.x, t.position.y, t.position.z }, objName);
        }

        private void SendWorld(byte kind, uint objHash, uint evHash, float[] f, string text)
        {
            if (Mode != 1) return;               // авторитет только у хоста
            if (host == null || host.MemberCount == 0) return;   // некому передавать
            byte[] frame = MpWorldSync.WorldFrame(kind, objHash, evHash, f, text);
            host.BroadcastWorldFrame(frame);
        }

        // Вызывается хостом при Hello нового клиента: снапшот мира
        public static void OnClientReady(short pid)
        {
            MpManager m = Instance;
            if (m == null || m.Mode != 1) return;
            MpSteamHost h = m.host;
            if (h == null) return;

            try
            {
                byte[] t = MpWorldSync.SnapshotTimeFrame();
                if (t != null) h.SendToPid(pid, t);

                byte[] w = MpWorldSync.SnapshotWeatherFrame();
                if (w != null) h.SendToPid(pid, w);

                ModLog.Info("Мир-снапшот отправлен игроку #" + pid);
            }
            catch (Exception e)
            {
                ModLog.Error("Мир-снапшот: " + e.Message);
            }
        }
        public class ChatLine
        {
            public short Pid;
            public string Text;
            public string Stamp;
        }

        private void AddChat(short pid, string text)
        {
            ChatLine l = new ChatLine();
            l.Pid = pid;
            l.Text = text;
            try { l.Stamp = DateTime.Now.ToString("HH:mm:ss"); } catch { l.Stamp = ""; }
            chatLog.Add(l);
            if (chatLog.Count > 60) chatLog.RemoveAt(0);
        }

        private string NameOf(short pid)
        {
            string n;
            if (pid < 0) return pid == -2 ? "[система]" : "[система]";
            if (names.TryGetValue(pid, out n)) return n;
            return pid == 0 ? "Хост" : "Игрок#" + pid;
        }

        public void DrawGUI()
        {
            // В главном меню вся графика — у MpMenuUI (панель справа вверху)
            if (Application.loadedLevelName == "MainMenu") return;

            // Статус
            GUI.Label(new Rect(8, 4, 520, 20), "MP Recode Remastered v0.3.0 (Steam) — " + StatusLine());

            // Лог чата
            float top = 26;
            GUI.Box(new Rect(8, top, 460, 20 + 18 * Mathf.Min(8, chatLog.Count)), "");
            int n = chatLog.Count;
            for (int i = 0; i < n && i < 8; i++)
            {
                ChatLine l = chatLog[n - 1 - i];
                string prefix = (l.Pid < 0 ? "" : NameOf(l.Pid) + ": ");
                GUI.Label(new Rect(14, top + 8 + (7 - i) * 18, 448, 18), prefix + l.Text);
            }

            // Окно ввода чата по F1
            if (chatOpen)
            {
                GUI.Box(new Rect(8, Screen.height - 70, 460, 60), "Чат (Enter — отправить, Esc — закрыть)");
                GUI.SetNextControlName("mpchat");
                chatInput = GUI.TextField(new Rect(16, Screen.height - 44, 380, 22), chatInput);
                GUI.FocusControl("mpchat");
            }
        }

        public void Update()
        {
            // В меню — без чата по F1 (ввод перехватится чекбоксами панели)
            if (Application.loadedLevelName == "MainMenu")
            {
                if (Input.GetKeyDown(KeyCode.Escape) && chatOpen) chatOpen = false;
                return;
            }

            if (Input.GetKeyDown(KeyCode.F1))
            {
                chatOpen = !chatOpen;
                if (!chatOpen) chatInput = "";
            }
            if (Input.GetKeyDown(KeyCode.Escape) && chatOpen)
            {
                chatOpen = false;
                chatInput = "";
            }
            if (Input.GetKeyDown(KeyCode.Return) && chatOpen)
            {
                string t = chatInput.Trim();
                if (t.Length > 0)
                {
                    if (t.StartsWith("/"))
                        RunSlash(t.Substring(1));
                    else if (Mode == 1)
                    {
                        if (host != null && host.MemberCount > 0)
                            host.HostChat(MpConfig.PlayerName + ": " + t);
                        AddChat(0, t);
                    }
                    else if (Mode == 2 && client != null && client.MyPid >= 0)
                        client.SendChat(MpConfig.PlayerName + ": " + t);
                    else
                        AddChat(-1, "Вы офлайн. mp host или mp join <код>.");
                }
                chatInput = "";
            }
        }

        // / команды прямо в чате
        private void RunSlash(string cmd)
        {
            string[] a = cmd.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (a.Length == 0) return;
            switch (a[0].ToLowerInvariant())
            {
                case "bots": SpawnDummyBots(1); break;
                case "clearbots": ClearDummyBots(); break;
                case "pos":
                    AddChat(-1, localPlayer != null ? localPlayer.position.ToString() : "Fsy не найден");
                    break;
            }
        }

        // ==== Косметические боты (локальный smoke-тест видимости/анимации) ====
        public void SpawnDummyBots(int count)
        {
            Vector3 basePos = localPlayer != null ? localPlayer.position : (Camera.main != null ? Camera.main.transform.position : Vector3.zero);
            for (int i = 0; i < count; i++)
            {
                short fakePid = (short)(900 + dummyBots.Count);
                MPAvatar av = MPAvatar.Create(fakePid);
                av.Name = "Бот#" + fakePid;
                av.transform.position = basePos + new Vector3(dummyBots.Count * 2f, 0, -3f);
                av.TargetPos = av.transform.position;
                av.gameObject.AddComponent<MpDummyBotWalk>();
                avatars[fakePid] = av;
                dummyBots.Add(av.gameObject);
            }
            AddChat(-1, "Добавлено ботов: " + count);
        }

        public void ClearDummyBots()
        {
            // dummy-боты не участвуют в playerlist'ах — вычищаем только pid>=900
            List<short> toRemove = new List<short>();
            foreach (KeyValuePair<short, MPAvatar> kv in avatars)
                if (kv.Key >= 900) toRemove.Add(kv.Key);
            for (int i = 0; i < toRemove.Count; i++)
            {
                Destroy(avatars[toRemove[i]].gameObject);
                avatars.Remove(toRemove[i]);
            }
            dummyBots.Clear();
            AddChat(-1, "Боты удалены.");
        }

        // ==== Инструменты «синхронизации всего» ====

        // mp scanworld [радиус]: сканирует сцену вокруг игрока, выводит объекты с FSM-автоматами
        // и их состояния — на основе этого расширяется каталог мир-синка.
        public void ScanWorld(float radius)
        {
            Vector3 center = localPlayer != null ? localPlayer.position : Vector3.zero;
            GameObject[] all = GameObject.FindObjectsOfType(typeof(GameObject)) as GameObject[];
            int found = 0, fsmCount = 0;

            ModLog.Info("=== СКАН МИРА: объекты в радиусе " + radius + " (всего GameObjects " + all.Length + ") ===");
            List<string> lines = new List<string>();

            for (int i = 0; i < all.Length; i++)
            {
                GameObject g = all[i];
                if (g == null) continue;

                PlayMakerFSM[] fsms = g.GetComponents<PlayMakerFSM>();
                if (fsms == null || fsms.Length == 0) continue;

                float dist = Vector3.Distance(center, g.transform.position);
                if (dist > radius) continue;

                found++;
                for (int j = 0; j < fsms.Length; j++)
                {
                    PlayMakerFSM f = fsms[j];
                    if (f == null || f.Fsm == null) continue;
                    fsmCount++;
                    StringBuilder sb = new StringBuilder();
                    sb.Append("FSM '").Append(g.name).Append("' / '").Append(f.FsmName).Append("' @ " + (int)dist + "м");
                    if (f.FsmStates != null && f.FsmStates.Length > 0)
                    {
                        sb.Append(" стейты: ");
                        for (int k = 0; k < f.FsmStates.Length && k < 8; k++)
                            sb.Append(f.FsmStates[k].Name).Append(k < 7 && k < f.FsmStates.Length - 1 ? ", " : "");
                    }
                    lines.Add(sb.ToString());
                }
            }

            lines.Sort();
            int shown = 0;
            foreach (string l in lines)
            {
                if (shown >= 80) { ModLog.Info("... ещё " + (lines.Count - shown) + " ФСМ (сузь радиус)"); break; }
                ModLog.Info(l);
                shown++;
            }
            ModLog.Info("=== СКАН МИРА: объектов с FSM " + found + ", всего FSM-скриптов " + fsmCount + " ===");
        }

        // mp dumpcar: все подузлы текущей машины (для калибровки салона/руля/двери)
        public void DumpCar()
        {
            GameObject p = GameObject.Find("Fsy");
            if (p == null || p.transform.parent == null) { AddChat(-1, "Сядь в машину и повтори mp dumpcar."); return; }

            Transform root = p.transform.parent;
            while (root.parent != null) root = root.parent;
            AddChat(-1, "Корень машины: '" + root.gameObject.name + "' id=" + MpWorldSync.ShortId(root.gameObject.name));

            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            int shown = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (shown >= 60) { AddChat(-2, "... [ограничение вывода]"); break; }
                string path = all[i].name + " (" + all[i].childCount + ")";
                if (all[i].name.ToLowerInvariant().Contains("steer") || all[i].name.ToLowerInvariant().Contains("seat") ||
                    all[i].name.ToLowerInvariant().Contains("door") || all[i].name.ToLowerInvariant().Contains("haiptan"))
                {
                    AddChat(-2, all[i].name + " :: тэг=" + all[i].tag + " | позиция " + all[i].localPosition);
                    shown++;
                }
            }
        }
    }
}
