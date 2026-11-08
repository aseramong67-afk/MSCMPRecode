using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Steamworks;
using UnityEngine;

namespace MSCMPRecode
{
    // ==== Код сессии (схема BeerMP LobbyCodeParser): ulong лобби -> компактный код ====
    public static class LobbyCode
    {
        public static string Encode(ulong id)
        {
            StringBuilder sb = new StringBuilder();
            ulong v = id;
            while (v != 0)
            {
                sb.Append(Get6BitChar((int)(v & 0x3F)));
                v >>= 6;
            }
            if (sb.Length == 0) sb.Append(Get6BitChar(0));
            return sb.ToString();
        }

        public static ulong Decode(string code)
        {
            if (code == null) return 0;
            code = code.Trim();
            ulong v = 0;
            for (int i = 0; i < code.Length && i < 12; i++)
            {
                int b = GetByte(code[i]);
                v |= ((ulong)b) << (i * 6);
            }
            return v;
        }

        private static char Get6BitChar(int b)
        {
            if (b < 10) return (char)('0' + b);
            if (b < 36) return (char)('A' + (b - 10));
            if (b == 36) return '_';
            if (b >= 63) return '-';
            return (char)('a' + (b - 37));
        }

        private static int GetByte(char c)
        {
            if (c == '-') return 63;
            if (c == '_' || c == ' ') return 36; // пробел тоже принимаем (копи-паста)
            if (c < ':') return c - '0';
            if (c < '[') return c - 'A' + 10;
            if (c < '{') return c - 'a' + 37;
            return 0;
        }
    }

    // ==== Участник хост-сессии ====
    public class MpSteamMember
    {
        public CSteamID Id;
        public short Pid;
        public string Name = "Игрок";
        public double LastSeen;
    }

    // ==== Хост: сессия поверх Steam P2P (однопоточный pump из MpManager.Tick) ====
    public class MpSteamHost
    {
        private readonly List<MpSteamMember> members = new List<MpSteamMember>();
        private short nextPid = 1;

        public int MemberCount { get { return members.Count; } }
        public string HostName;

        public MpSteamMember FindByPid(short pid)
        {
            for (int i = 0; i < members.Count; i++)
                if (members[i].Pid == pid) return members[i];
            return null;
        }

        public MpSteamMember FindById(CSteamID id)
        {
            for (int i = 0; i < members.Count; i++)
                if (members[i].Id == id) return members[i];
            return null;
        }

        public void OnLobbyJoined()
        {
            HostName = MpConfig.PlayerName;
            ModLog.Info("Steam-хост: сессия активна, жду клиентов. Код: " +
                        LobbyCode.Encode(MpSteam.LobbyId.m_SteamID));
        }

        // Присоединение из лобби (по LobbyChatUpdate: кто-то зашёл в лобби)
        public void OnMemberEnterLobby(CSteamID id)
        {
            if (id == SteamUser.GetSteamID()) return;
            if (FindById(id) != null) return;
            MpSteamMember m = new MpSteamMember();
            m.Id = id;
            m.Pid = nextPid;
            if (nextPid < short.MaxValue) nextPid++; else nextPid = 1;
            m.LastSeen = EnvMs();
            members.Add(m);
            ModLog.Info("Steam-хост: в лобби новый участник, pid=" + m.Pid + " (" + id.m_SteamID + ")");
            BroadcastPlayerList();
            MpManager.PushEventList(GetListSnapshot()); // сам хост видит список
        }

        // Участник ушёл / отвалился
        public void OnMemberLeft(CSteamID id)
        {
            MpSteamMember m = FindById(id);
            if (m == null) return;
            members.Remove(m);

            PacketWriter w = new PacketWriter();
            w.PutByte((byte)Packet.PlayerLeft);
            w.PutShort(m.Pid);
            byte[] frame = w.ToArray();
            BroadcastAll(frame);
            MpManager.PushEventLeft(m.Pid);
            ModLog.Info("Steam-хост: участник " + m.Name + " (pid " + m.Pid + ") ушёл.");
            BroadcastPlayerList();
            MpManager.PushEventList(GetListSnapshot());
        }

        // ==== Приём пакета (main thread) ====
        public void OnReceive(CSteamID from, byte[] body)
        {
            if (body == null || body.Length < 1) return;
            MpSteamMember m = FindById(from);
            if (m == null)
            {
                // Клиент может прислать Hello, пока хост ещё не увидел его в списке лобби
                // (race LobbyChatUpdate). Hello доверяем всегда, иначе клиент зависнет.
                MpSteam.AddLobbyMemberIfPresent(from);
                m = FindById(from);
                if (m == null)
                {
                    if (body[0] != (byte)Packet.Hello) return; // не наш формат — игнор
                    m = new MpSteamMember();
                    m.Id = from;
                    m.Pid = nextPid;
                    if (nextPid < short.MaxValue) nextPid++; else nextPid = 1;
                    m.LastSeen = EnvMs();
                    members.Add(m);
                    ModLog.Info("Steam-хост: участник по P2P-Hello, pid=" + m.Pid + " (" + from.m_SteamID + ")");
                    BroadcastPlayerList();
                    MpManager.PushEventList(GetListSnapshot());
                }
            }
            m.LastSeen = EnvMs();

            Packet type = (Packet)body[0];
            byte[] rest = new byte[body.Length - 1];
            Array.Copy(body, 1, rest, 0, rest.Length);

            switch (type)
            {
                case Packet.Hello:
                    {
                        PacketReader r = new PacketReader(rest);
                        m.Name = r.GetString();
                        if (string.IsNullOrEmpty(m.Name)) m.Name = "Игрок" + m.Pid;
                        ModLog.Info("Steam-хост: " + m.Name + " представился (pid " + m.Pid + ")");

                        SendTo(m, (byte)Packet.Welcome, delegate(PacketWriter w)
                        {
                            w.PutShort(m.Pid);
                            w.PutString(HostName);
                        });

                        SendPlayerListTo(m);
                        BroadcastPlayerList();
                        MpManager.PushEventList(GetListSnapshot());

                        // Снапшот мира прямо этому клиенту
                        MpManager.OnClientReady(m.Pid);
                        break;
                    }
                case Packet.PlayerState:
                    {
                        if (rest.Length < 19) break;
                        NetPlayerState st = NetUtil.ParsePlayerState(rest);
                        st.Pid = m.Pid;
                        PacketWriter w = new PacketWriter();
                        w.PutByte((byte)Packet.PlayerState);
                        NetUtil.WithState(w, st);
                        BroadcastExcept(w.ToArray(), from);
                        MpManager.PushEventState(st);
                        m.LastSeen = EnvMs();
                        break;
                    }
                case Packet.Chat:
                    {
                        PacketReader r = new PacketReader(rest);
                        string text = r.GetString();
                        PacketWriter w = new PacketWriter();
                        w.PutByte((byte)Packet.Chat);
                        w.PutShort(m.Pid);
                        w.PutString(text);
                        BroadcastExcept(w.ToArray(), from);
                        MpManager.PushEventChat(m.Pid, text);
                        break;
                    }
                case Packet.Action:
                    {
                        if (rest.Length < 9) break;
                        PacketReader r = new PacketReader(rest);
                        PacketWriter w = new PacketWriter();
                        w.PutByte((byte)Packet.Action);
                        w.PutShort(m.Pid);
                        w.PutByte(r.GetByte());
                        w.PutShort(r.GetShort());
                        w.PutFloat(r.GetFloat());
                        BroadcastExcept(w.ToArray(), from);
                        MpManager.PushEventAction(m.Pid, rest);
                        break;
                    }
                case Packet.Ping:
                    SendTo(m, (byte)Packet.Pong, delegate(PacketWriter w) { w.PutInt(0); });
                    break;
            }
        }

        // ==== Отправки от хоста ====
        public void BroadcastSelfState(float x, float y, float z, float rotY, byte flags)
        {
            PacketWriter w = new PacketWriter();
            w.PutByte((byte)Packet.PlayerState);
            w.PutShort(0);
            w.PutFloat(x);
            w.PutFloat(y);
            w.PutFloat(z);
            w.PutFloat(rotY);
            w.PutByte(flags);
            BroadcastAll(w.ToArray());
        }

        public void HostChat(string text)
        {
            PacketWriter w = new PacketWriter();
            w.PutByte((byte)Packet.Chat);
            w.PutShort(0);
            w.PutString(text);
            BroadcastAll(w.ToArray());
        }

        public void HostAction(byte action, short objectId, float value)
        {
            PacketWriter w = new PacketWriter();
            w.PutByte((byte)Packet.Action);
            w.PutShort(0);
            w.PutByte(action);
            w.PutShort(objectId);
            w.PutFloat(value);
            BroadcastAll(w.ToArray());
        }

        public void BroadcastWorldFrame(byte[] frame)
        {
            BroadcastAll(frame);
        }

        private void SendAll(List<PlayerListEntry> e) { }

        public void SendToPid(short pid, byte[] body)
        {
            MpSteamMember m = FindByPid(pid);
            if (m != null) MpSteam.Send(m.Id, body, true);
        }

        public void SendPlayerListTo(MpSteamMember m)
        {
            PacketWriter w = new PacketWriter();
            w.PutByte((byte)Packet.PlayerList);
            FillPlayerList(w);
            MpSteam.Send(m.Id, w.ToArray(), true);
        }

        public void BroadcastPlayerList()
        {
            PacketWriter w = new PacketWriter();
            w.PutByte((byte)Packet.PlayerList);
            FillPlayerList(w);
            BroadcastAll(w.ToArray());
        }

        private void FillPlayerList(PacketWriter w)
        {
            w.PutShort((short)(members.Count + 1));
            w.PutShort(0);
            w.PutString(HostName + " (хост)");
            for (int i = 0; i < members.Count; i++)
            {
                w.PutShort(members[i].Pid);
                w.PutString(members[i].Name);
            }
        }

        public List<PlayerListEntry> GetListSnapshot()
        {
            List<PlayerListEntry> list = new List<PlayerListEntry>();
            list.Add(new PlayerListEntry { Pid = 0, Name = HostName + " (хост)" });
            for (int i = 0; i < members.Count; i++)
                list.Add(new PlayerListEntry { Pid = members[i].Pid, Name = members[i].Name });
            return list;
        }

        private void BroadcastAll(byte[] body)
        {
            for (int i = 0; i < members.Count; i++)
                MpSteam.Send(members[i].Id, body, true);
        }

        private void BroadcastExcept(byte[] body, CSteamID except)
        {
            for (int i = 0; i < members.Count; i++)
                if (members[i].Id != except)
                    MpSteam.Send(members[i].Id, body, true);
        }

        private void SendTo(MpSteamMember m, byte head, Func2 writer)
        {
            PacketWriter w = new PacketWriter();
            w.PutByte(head);
            writer(w);
            MpSteam.Send(m.Id, w.ToArray(), true);
        }

        public delegate void Func2(PacketWriter w);

        public void Shutdown()
        {
            members.Clear();
        }

        // Проверка молчащих клиентов по P2P-состоянию (MpSteam зовёт раз в 2с)
        public void SweepDead()
        {
            double now = EnvMs();
            for (int i = members.Count - 1; i >= 0; i--)
            {
                P2PSessionState_t st;
                if (!SteamNetworking.GetP2PSessionState(members[i].Id, out st)) continue;
                bool connecting = Convert.ToBoolean(st.m_bConnecting);
                bool active = Convert.ToBoolean(st.m_bConnectionActive);
                if (!connecting && !active)
                {
                    ModLog.Info("Steam-хост: P2P-сессия с pid " + members[i].Pid + " умерла.");
                    OnMemberLeft(members[i].Id);
                }
            }
        }

        private static double EnvMs()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
        }
    }

    // ==== Клиент: подключение к хосту по коду сессии ====
    public class MpSteamClient
    {
        public CSteamID HostId;
        public short MyPid = -1;
        public string HostName = "хост";
        private double lastHello;

        public void OnEnterLobby(CSteamID lobby)
        {
            HostId = SteamMatchmaking.GetLobbyOwner(lobby);
            MyPid = -1;
            HostName = "хост";
            lastHello = 0;
            ModLog.Info("Steam-клиент: в лобби, владелец " + HostId.m_SteamID + ", шлю Hello...");
            SendHello();

            PacketWriter s = new PacketWriter();
            s.PutByte((byte)Packet.SteamInfo);
            s.PutString("spacewar/steam");
            MpSteam.Send(HostId, s.ToArray(), true);
        }

        // Повтор Hello, пока хост не выдал pid (Welcome). MpSteam.Pump зовёт каждый кадр.
        public void RetryHello()
        {
            if (MyPid >= 0 || HostId.m_SteamID == 0) return;
            double now = NowMs();
            if (now - lastHello < 2000) return;
            lastHello = now;
            SendHello();
        }

        private void SendHello()
        {
            PacketWriter w = new PacketWriter();
            w.PutByte((byte)Packet.Hello);
            w.PutString(MpConfig.PlayerName);
            MpSteam.Send(HostId, w.ToArray(), true);
        }

        private static double NowMs()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
        }

        public void OnReceive(CSteamID from, byte[] body)
        {
            if (body == null || body.Length < 1) return;
            if (from != HostId) return; // только от владельца лобби

            Packet type = (Packet)body[0];
            byte[] rest = new byte[body.Length - 1];
            Array.Copy(body, 1, rest, 0, rest.Length);

            switch (type)
            {
                case Packet.Welcome:
                    {
                        PacketReader r = new PacketReader(rest);
                        MyPid = r.GetShort();
                        HostName = r.GetString();
                        ModLog.Info("Steam-клиент: хост " + HostName + " выдал pid " + MyPid);
                        break;
                    }
                case Packet.PlayerState:
                    {
                        PacketReader r = new PacketReader(rest);
                        NetPlayerState st = new NetPlayerState();
                        st.Pid = r.GetShort();
                        st.X = r.GetFloat();
                        st.Y = r.GetFloat();
                        st.Z = r.GetFloat();
                        st.RotY = r.GetFloat();
                        st.Flags = r.GetByte();
                        MpManager.PushEventState(st);
                        break;
                    }
                case Packet.Chat:
                    {
                        PacketReader r = new PacketReader(rest);
                        MpManager.PushEventChat(r.GetShort(), r.GetString());
                        break;
                    }
                case Packet.PlayerLeft:
                    {
                        PacketReader r = new PacketReader(rest);
                        MpManager.PushEventLeft(r.GetShort());
                        break;
                    }
                case Packet.Action:
                    {
                        if (rest.Length < 9) break;
                        PacketReader r = new PacketReader(rest);
                        MpManager.PushEventAction(r.GetShort(), rest);
                        break;
                    }
                case Packet.WorldEvent:
                    {
                        // [kind:1][objHash:4][evHash:4][f1..f8:32][string]
                        if (rest.Length < 41) break;
                        byte kind = rest[0];
                        uint objH = (uint)(rest[1] | (rest[2] << 8) | (rest[3] << 16) | (rest[4] << 24));
                        uint evH = (uint)(rest[5] | (rest[6] << 8) | (rest[7] << 16) | (rest[8] << 24));
                        float[] f = new float[8];
                        for (int i = 0; i < 8; i++)
                            f[i] = BitConverter.ToSingle(rest, 9 + i * 4);
                        string text = "";
                        try
                        {
                            byte[] tail = new byte[rest.Length - 41];
                            Array.Copy(rest, 41, tail, 0, tail.Length);
                            PacketReader rt = new PacketReader(tail);
                            text = rt.GetString();
                        }
                        catch { }
                        MpManager.PushEventWorld(kind, objH, evH, f, text);
                        break;
                    }
                case Packet.PlayerList:
                    {
                        PacketReader r = new PacketReader(rest);
                        short count = r.GetShort();
                        List<PlayerListEntry> list = new List<PlayerListEntry>(count);
                        for (int i = 0; i < count; i++)
                        {
                            PlayerListEntry e = new PlayerListEntry();
                            e.Pid = r.GetShort();
                            e.Name = r.GetString();
                            list.Add(e);
                        }
                        MpManager.PushEventList(list);
                        break;
                    }
            }
        }

        // ==== Исходящие ====
        public void SendState(short pid, float x, float y, float z, float rotY, byte flags)
        {
            PacketWriter w = new PacketWriter();
            w.PutByte((byte)Packet.PlayerState);
            w.PutShort(pid);
            w.PutFloat(x);
            w.PutFloat(y);
            w.PutFloat(z);
            w.PutFloat(rotY);
            w.PutByte(flags);
            MpSteam.Send(HostId, w.ToArray(), false); // состояние — ненадёжно, 20 Гц
        }

        public void SendChat(string text)
        {
            if (MyPid < 0) return;
            PacketWriter w = new PacketWriter();
            w.PutByte((byte)Packet.Chat);
            w.PutShort(MyPid);
            w.PutString(text);
            MpSteam.Send(HostId, w.ToArray(), true);
        }

        public void SendAction(byte action, short objectId, float value)
        {
            if (MyPid < 0) return;
            PacketWriter w = new PacketWriter();
            w.PutByte((byte)Packet.Action);
            w.PutShort(MyPid);
            w.PutByte(action);
            w.PutShort(objectId);
            w.PutFloat(value);
            MpSteam.Send(HostId, w.ToArray(), true);
        }

        public void Shutdown()
        {
            MyPid = -1;
        }
    }

    // ==== MpSteam: общий слой (лобби, коды, P2P pump) ====
    public static class MpSteam
    {
        private static bool callbacksReady;
        private static CSteamID lobby;
        private static string lobbyCode;
        private static bool joiningLobby;   // клиент: ждём LobbyDataUpdate -> JoinLobby (схема BeerMP)
        private static SteamAPICall_t createCall;       // CreateLobby: контроль ответа Steam
        private static double createCallStarted;
        private static double createWatchLast;
        private static List<CSteamID> connections = new List<CSteamID>();

        public static CSteamID LobbyId { get { return lobby; } }
        public static string LobbyCodeStr { get { return lobbyCode; } }
        public static MpSteamHost Host { get; private set; }
        public static MpSteamClient Client { get; private set; }
        public static bool IsHost { get { return Host != null; } }

        // ==== Запуск (один раз) ====
        public static void EnsureInit()
        {
            if (callbacksReady) return;
            try
            {
                // Steam уже инициализирован самой игрой (onlinefix/Steam)
                SteamAPI.Init();
            }
            catch (Exception) { /* уже инициализирован — ок */ }

            try
            {
                Callback<LobbyCreated_t>.Create(OnLobbyCreated);
                Callback<LobbyEnter_t>.Create(OnLobbyEnter);
                Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
                Callback<LobbyDataUpdate_t>.Create(OnLobbyDataUpdate);
                Callback<P2PSessionRequest_t>.Create(OnP2PSessionRequest);
                Callback<P2PSessionConnectFail_t>.Create(OnP2PSessionConnectFail);
                callbacksReady = true;
                ModLog.Info("Steam-слой готов. Мой SteamID: " + SteamUser.GetSteamID().m_SteamID);
            }
            catch (Exception e)
            {
                ModLog.Error("Steam-слой: " + e.Message);
            }
        }

        // ==== Хост: создать лобби ====
        public static void HostCreate(int maxPlayers)
        {
            EnsureInit();
            Host = new MpSteamHost();
            Client = null;
            createCall = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, Math.Max(2, maxPlayers));
            createCallStarted = NowMs();
            ModLog.Info("Steam: создаю лобби... (APICall " + createCall.m_SteamAPICall + ")");
        }

        // ==== Клиент: войти по коду ====
        public static void ClientJoin(string code)
        {
            EnsureInit();
            if (string.IsNullOrEmpty(code)) { ModLog.Error("Код сессии пуст."); return; }
            code = code.Trim().ToUpperInvariant().Replace(" ", "");
            ulong lobbyId = LobbyCode.Decode(code);
            if (lobbyId == 0) { ModLog.Error("Неверный код сессии: " + code); MpManager.PushEventLocalSystem("Неверный код: " + code); return; }

            Host = null;
            Client = new MpSteamClient();
            lobby = new CSteamID(lobbyId);
            if (!SteamMatchmaking.RequestLobbyData(lobby))
            {
                MpManager.PushEventLocalSystem("Не запросить данные сессии " + code);
                ModLog.Error("RequestLobbyData не удался.");
                Client = null;
                return;
            }
            joiningLobby = true;
            ModLog.Info("Steam: запрашиваю данные сессии по коду " + code + " (ID " + lobbyId + ")...");
        }

        public static void LeaveLobby()
        {
            if (lobby != CSteamID.Nil && lobby.m_SteamID != 0)
            {
                try { SteamMatchmaking.LeaveLobby(lobby); } catch (Exception) { }
            }
            lobby = CSteamID.Nil;
            lobbyCode = null;
            joiningLobby = false;
            if (Host != null) Host.Shutdown();
            if (Client != null) Client.Shutdown();
            for (int i = 0; i < connections.Count; i++)
            {
                try { SteamNetworking.CloseP2PSessionWithUser(connections[i]); } catch (Exception) { }
            }
            connections.Clear();
            Host = null;
            Client = null;
            ModLog.Info("Steam: сессия закрыта.");
        }

        // ==== Pump: читает P2P-пакеты (вызывается из MpManager.Tick) ====
        public static void Pump()
        {
            if (!callbacksReady) return;

            // Steam-коллбэки лобби/P2P — обязательно каждый кадр (как BeerMP.Update)
            try { SteamAPI.RunCallbacks(); }
            catch (Exception) { }

            // Клиент: дожимаем Hello, пока нет Welcome
            if (Client != null) Client.RetryHello();

            WatchCreateLobby();

            try
            {
                uint size;
                while (SteamNetworking.IsP2PPacketAvailable(out size))
                {
                    byte[] buf = new byte[size];
                    uint read;
                    CSteamID from;
                    if (!SteamNetworking.ReadP2PPacket(buf, size, out read, out from)) break;
                    if (from == SteamUser.GetSteamID()) continue;

                    if (IsHost && Host != null) Host.OnReceive(from, buf);
                    else if (Client != null) Client.OnReceive(from, buf);
                }
            }
            catch (Exception e)
            {
                ModLog.Error("Steam pump: " + e.Message);
            }
        }

        // Контроль CreateLobby: вызов «висит» или Steam вернул ошибку API?
        private static void WatchCreateLobby()
        {
            if (createCall.m_SteamAPICall == 0) return;
            try
            {
                bool done;
                if (SteamUtils.IsAPICallCompleted(createCall, out done) && done)
                {
                    ESteamAPICallFailure fr = SteamUtils.GetAPICallFailureReason(createCall);
                    createCall = new SteamAPICall_t(0);
                    if (fr == ESteamAPICallFailure.k_ESteamAPICallFailureNone)
                        ModLog.Info("Steam: CreateLobby подтверждён (ждём LobbyCreated)...");
                    else
                        ModLog.Error("Steam: CreateLobby ОШИБКА: " + fr);
                    return;
                }
                double waited = (NowMs() - createCallStarted) / 1000.0;
                if (NowMs() - createWatchLast >= 3000 && waited > 3)
                {
                    createWatchLast = NowMs();
                    ModLog.Warn("Steam: CreateLobby висит " + (int)waited + " с — Steam не отвечает...");
                }
            }
            catch (Exception e)
            {
                ModLog.Warn("CreateLobby watch: " + e.Message);
                createCall = new SteamAPICall_t(0);
            }
        }

        private static double NowMs()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
        }

        public static bool Send(CSteamID to, byte[] body, bool reliable)
        {
            try
            {
                EP2PSend mode = reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliable;
                if (!connections.Contains(to)) connections.Add(to);
                return SteamNetworking.SendP2PPacket(to, body, (uint)body.Length, mode);
            }
            catch (Exception e)
            {
                ModLog.Warn("Steam send: " + e.Message);
                return false;
            }
        }

        // ==== Коллбэки ====
        private static void OnLobbyCreated(LobbyCreated_t cb)
        {
            createCall = new SteamAPICall_t(0);
            if (cb.m_eResult != EResult.k_EResultOK)
            {
                ModLog.Error("Steam: лобби не создалось: " + cb.m_eResult);
                if (Host != null) Host.Shutdown();
                Host = null;
                return;
            }
            lobby = new CSteamID(cb.m_ulSteamIDLobby);
            lobbyCode = LobbyCode.Encode(cb.m_ulSteamIDLobby);
            try
            {
                SteamMatchmaking.SetLobbyData(lobby, "mprver", "0.3");
                SteamMatchmaking.SetLobbyData(lobby, "mprhost", MpConfig.PlayerName);
            }
            catch (Exception) { }
            MpManager.PushEventLocalSystem("СЕССИЯ СОЗДАНА. Код: " + lobbyCode + " (скопирован в буфер)");
            // Хост окончательно активируется в OnLobbyEnter (лобби создано -> входим в него)
            CopyToClipboard(lobbyCode);
        }

        private static void OnLobbyEnter(LobbyEnter_t cb)
        {
            createCall = new SteamAPICall_t(0);
            joiningLobby = false;
            if (cb.m_EChatRoomEnterResponse != 1)
            {
                ModLog.Error("Steam: не войти в сессию: " + (EChatRoomEnterResponse)cb.m_EChatRoomEnterResponse);
                MpManager.PushEventLocalSystem("Хост по коду не найден или недоступен (" + cb.m_EChatRoomEnterResponse + ").");
                if (Client != null) Client.Shutdown();
                Client = null;
                return;
            }
            lobby = new CSteamID(cb.m_ulSteamIDLobby);
            lobbyCode = LobbyCode.Encode(cb.m_ulSteamIDLobby);
            if (Host != null)
            {
                Host.OnLobbyJoined(); // создатель тоже получает LobbyEnter
                return;
            }
            if (Client != null)
            {
                ModLog.Info("Steam: вошёл в сессию. Код: " + lobbyCode);
                Client.OnEnterLobby(lobby);
            }
        }

        private static void OnLobbyChatUpdate(LobbyChatUpdate_t cb)
        {
            CSteamID who = new CSteamID(cb.m_ulSteamIDUserChanged);
            // 2 = кик/выход
            if ((cb.m_rgfChatMemberStateChange & 0x0002) != 0)
            {
                if (IsHost && Host != null)
                {
                    Host.OnMemberLeft(who);
                }
                else if (Client != null)
                {
                    CSteamID owner = SteamMatchmaking.GetLobbyOwner(lobby);
                    if (who == owner)
                    {
                        MpManager.PushEventLocalSystem("Хост закрыл сессию.");
                        MpManager.PushEventHostLost();
                        Client.Shutdown();
                        Client = null;
                        lobby = CSteamID.Nil;
                    }
                }
            }
            // нужен P2P от вновь вошедшего — хост запоминает его заранее
            if (IsHost && Host != null && (cb.m_rgfChatMemberStateChange & 0x0001) != 0)
                Host.OnMemberEnterLobby(who);
        }

        // Вызывается хостом при получении P2P от незнакомца
        internal static void AddLobbyMemberIfPresent(CSteamID id)
        {
            if (Host == null || lobby.m_SteamID == 0) return;
            try
            {
                int n = SteamMatchmaking.GetNumLobbyMembers(lobby);
                for (int i = 0; i < n; i++)
                    if (SteamMatchmaking.GetLobbyMemberByIndex(lobby, i) == id)
                    {
                        Host.OnMemberEnterLobby(id);
                        return;
                    }
            }
            catch (Exception) { }
        }

        private static void OnLobbyDataUpdate(LobbyDataUpdate_t cb)
        {
            // Схема BeerMP: данные лобби пришли -> можно подаваться в лобби
            if (joiningLobby)
            {
                joiningLobby = false;
                SteamMatchmaking.JoinLobby(new CSteamID(cb.m_ulSteamIDLobby));
                ModLog.Info("Steam: данные получены, вхожу в сессию...");
            }
        }

        private static void OnP2PSessionRequest(P2PSessionRequest_t cb)
        {
            try
            {
                SteamNetworking.AcceptP2PSessionWithUser(cb.m_steamIDRemote);
                if (!connections.Contains(cb.m_steamIDRemote)) connections.Add(cb.m_steamIDRemote);
            }
            catch (Exception) { }
        }

        private static void OnP2PSessionConnectFail(P2PSessionConnectFail_t cb)
        {
            ModLog.Warn("Steam: P2P-сессия с " + cb.m_steamIDRemote.m_SteamID + " пропала (" + cb.m_eP2PSessionError + ").");
            if (Host != null) Host.OnMemberLeft(cb.m_steamIDRemote);
        }

        public static bool SteamWorksAvailable()
        {
            try
            {
                CSteamID me = SteamUser.GetSteamID();
                return me.m_SteamID != 0;
            }
            catch (Exception) { return false; }
        }

        // ГУИ-утилита systemCopyBuffer непублична в этой сборке Unity — только reflection
        // (как у BeerMP.Helpers.Clipboard)
        internal static void CopyToClipboard(string text)
        {
            try
            {
                PropertyInfo cp = typeof(GUIUtility).GetProperty("systemCopyBuffer",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (cp != null) cp.SetValue(null, text, null);
            }
            catch (Exception) { }
        }
    }
}
