using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace MSCMPRecode
{
    // ==== Хост: постоянный TCP-сервер, туннель cloudpub пробрасывает сюда клиенты ====
    public class MpHostServer : IDisposable
    {
        private class ClientConn
        {
            public short Pid;
            public string Name;
            public TcpClient Tcp;
            public NetworkStream Stream;
            public object SendLock = new object();
        }

        private TcpListener listener;
        private readonly List<ClientConn> clients = new List<ClientConn>();
        private readonly object sync = new object();
        private short nextPid = 1;
        private bool running;
        private int port;

        public bool Running { get { return running; } }
        public int ClientCount
        {
            get
            {
                lock (sync) { return clients.Count; }
            }
        }

        public void Start(int listenPort)
        {
            if (running) return;
            port = listenPort;
            nextPid = 1;

            try
            {
                listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                running = true;
                // Асинхронный accept — не зависит от Pending()/поллинга
                listener.BeginAcceptTcpClient(new AsyncCallback(OnAccepted), listener);
                ModLog.Info("Хост: BeginAccept зарегистрирован. Слушаю порт " + port);
                ModLog.Info("Хост: сервер слушает 0.0.0.0:" + port + " (клиенты заходят через " + MpConfig.TunnelHost + ":" + MpConfig.TunnelPort + ")");
            }
            catch (Exception e)
            {
                running = false;
                ModLog.Error("Хост: не удалось открыть порт " + port + ": " + e.Message);
            }
        }

        private void OnAccepted(IAsyncResult ar)
        {
            ModLog.Info("Хост: коллбэк OnAccepted сработал.");
            if (!running)
            {
                ModLog.Warn("Хост: OnAccepted при running=false.");
                return;
            }

            TcpClient tcp = null;
            try
            {
                tcp = ((TcpListener)ar.AsyncState).EndAcceptTcpClient(ar);
                ModLog.Info("Хост: EndAcceptTcpClient ok.");
            }
            catch (ObjectDisposedException e)
            {
                if (running) ModLog.Warn("Хост: accept ODE: " + e.Message);
                return;
            }
            catch (Exception e)
            {
                if (running) ModLog.Warn("Хост: сбой accept: " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                if (running)
                {
                    try
                    {
                        listener.BeginAcceptTcpClient(new AsyncCallback(OnAccepted), listener);
                        ModLog.Info("Хост: перерегистрация BeginAccept ok.");
                    }
                    catch (Exception) { running = false; }
                }
            }

            if (tcp == null) return;

            lock (sync)
            {
                if (clients.Count >= MpConfig.MaxPlayers)
                {
                    try { tcp.Close(); } catch { }
                    return;
                }
            }

            short pid = nextPid;
            if (nextPid < short.MaxValue) nextPid++; else nextPid = 1;

            ClientConn conn = new ClientConn();
            conn.Pid = pid;
            conn.Tcp = tcp;
            conn.Name = "Игрок#" + pid;
            try { conn.Stream = tcp.GetStream(); conn.Stream.ReadTimeout = -1; }
            catch (Exception e2) { try { tcp.Close(); } catch { } ModLog.Warn("Хост: GetStream не удался: " + e2.Message); return; }

            lock (sync) { clients.Add(conn); }
            ModLog.Info("Хост: подключение #" + pid);

            Thread t = new Thread(ClientLoop);
            t.IsBackground = true;
            t.Start(conn);
        }

        public void Stop()
        {
            if (!running) return;
            running = false;

            try { if (listener != null) listener.Stop(); } catch { }
            short[] pids;
            lock (sync)
            {
                pids = new short[clients.Count];
                for (int i = 0; i < clients.Count; i++)
                {
                    pids[i] = clients[i].Pid;
                    try { clients[i].Stream.Close(); } catch { }
                    try { clients[i].Tcp.Close(); } catch { }
                }
                clients.Clear();
            }
            for (int i = 0; i < pids.Length; i++)
            {
                PushLeft(pids[i]);
                broadcastLeft(pids[i]);
            }
            ModLog.Info("Хост: сервер остановлен.");
        }

        private void AcceptLoop()
        {
            ModLog.Info("Хост: accept-цикл запущен.");
            long probeTicks = 0;
            while (running)
            {
                probeTicks++;
                TcpClient tcp = null;
                try
                {
                    if (!running) break;
                    bool pending = listener.Pending();
                    if (!pending)
                    {
                        if ((probeTicks % 20) == 0)
                            ModLog.Info("Хост: accept ждёт... pending=" + pending + " порт=" + listener.LocalEndpoint);
                        Thread.Sleep(100);
                        continue;
                    }
                    ModLog.Info("Хост: pending=true, принимаю...");
                    tcp = listener.AcceptTcpClient();
                }
                catch (Exception e) { ModLog.Error("Хост: accept-цикл прервался: " + e.GetType().Name + ": " + e.Message); break; }

                if (!running || tcp == null) break;

                lock (sync)
                {
                    if (clients.Count >= MpConfig.MaxPlayers)
                    {
                        try { tcp.Close(); } catch { }
                        tcp = null;
                    }
                }
                if (tcp == null) continue;

                short pid = nextPid;
                if (nextPid < short.MaxValue) nextPid++; else nextPid = 1;

                ClientConn conn = new ClientConn();
                conn.Pid = pid;
                conn.Tcp = tcp;
                conn.Name = "Игрок#" + pid;
                try { conn.Stream = tcp.GetStream(); }
                catch { try { tcp.Close(); } catch { } continue; }

                lock (sync) { clients.Add(conn); }
                ModLog.Info("Хост: подключение #" + pid);

                Thread t = new Thread(ClientLoop);
                t.IsBackground = true;
                t.Start(conn);
            }
        }

        private void ClientLoop(object obj)
        {
            ClientConn conn = (ClientConn)obj;

            byte[] body = null;
            try
            {
                while (running)
                {
                    body = NetUtil.ReadFrame(conn.Stream);
                    if (body == null) break;
                    if (body.Length == 0) continue;

                    Packet type = (Packet)body[0];
                    byte[] rest = new byte[body.Length - 1];
                    Array.Copy(body, 1, rest, 0, rest.Length);

                    switch (type)
                    {
                        case Packet.Hello:
                            {
                                PacketReader r = new PacketReader(rest);
                                string name = r.GetString();
                                conn.Name = string.IsNullOrEmpty(name) ? conn.Name : name;
                                ModLog.Info("Хост: #" + conn.Pid + " представился как " + conn.Name);
                                SendWelcome(conn);
                                SendPlayerList(conn);
                                BroadcastPlayerList();
                                // Мир-снапшот: время/погода как у хоста
                                MpManager.OnClientReady(conn.Pid);
                                break;
                            }
                        case Packet.PlayerState:
                            {
                                // [pid:2][x:4][y:4][z:4][rotY:4][flags:1] = 19 байт
                                if (rest.Length < 19) break;
                                // Не доверяем пиду клиента — подменяем на свой честный
                                NetPlayerState st = NetUtil.ParsePlayerState(rest);
                                st.Pid = conn.Pid;
                                st.LastUpdate = EnvMs();

                                PacketWriter w = new PacketWriter();
                                w.PutByte((byte)Packet.PlayerState);
                                NetUtil.WithState(w, st);
                                byte[] frame = w.ToArray();
                                Broadcast(frame, (ushort)frame.Length, conn);

                                // Хост сам видит движение подключившихся игроков
                                MpManager.PushEventState(st);
                                break;
                            }
                        case Packet.Action:
                            {
                                // [pid:2][action:1][objectId:2][value:4] = 9 байт
                                if (rest.Length < 9) break;
                                PacketReader r = new PacketReader(rest);
                                PacketWriter w = new PacketWriter();
                                w.PutByte((byte)Packet.Action);
                                w.PutShort(conn.Pid);
                                w.PutByte(r.GetByte());
                                w.PutShort(r.GetShort());
                                w.PutFloat(r.GetFloat());
                                byte[] frame = w.ToArray();
                                Broadcast(frame, (ushort)frame.Length, conn);
                                MpManager.PushEventAction(conn.Pid, rest);
                                break;
                            }
                        case Packet.Chat:
                            {
                                PacketReader r = new PacketReader(rest);
                                string text = r.GetString();
                                PacketWriter w = new PacketWriter();
                                w.PutByte((byte)Packet.Chat);
                                w.PutShort(conn.Pid);
                                w.PutString(text);
                                byte[] frame = w.ToArray();
                                Broadcast(frame, (ushort)frame.Length, conn);
                                MpManager.PushEventChat(conn.Pid, text);
                                break;
                            }
                        case Packet.SteamInfo:
                            {
                                PacketReader r = new PacketReader(rest);
                                ModLog.Info("Хост: #" + conn.Pid + " Steam-режим: " + r.GetString());
                                break;
                            }
                        case Packet.Ping:
                            Send(conn, MakePong());
                            break;
                    }
                }
            }
            catch (Exception) { }
            finally
            {
                Drop(conn, "отключился");
            }
        }

        private byte[] MakePong()
        {
            PacketWriter w = new PacketWriter();
            w.PutByte((byte)Packet.Pong);
            w.PutInt(0);
            return w.ToArray();
        }

        // ==== Вещание всем клиентам (для менеджера мира) ====
        public void BroadcastWorld(byte[] body)
        {
            Broadcast(body, (ushort)body.Length, null);
        }

        // ==== Адресная отправка (снапшот мира новому клиенту) ====
        public void SendTo(short pid, byte[] body)
        {
            ClientConn target = null;
            lock (sync)
            {
                for (int i = 0; i < clients.Count; i++)
                    if (clients[i].Pid == pid) { target = clients[i]; break; }
            }
            if (target != null) Send(target, body);
        }

        // ==== Хост отправляет собственное состояние (pid 0) всем клиентам ====
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
            byte[] body = w.ToArray();
            Broadcast(body, (ushort)body.Length, null);
        }

        // ==== Сообщение чата от хоста (pid 0) ====
        public void HostChat(string text)
        {
            PacketWriter w = new PacketWriter();
            w.PutByte((byte)Packet.Chat);
            w.PutShort(0);
            w.PutString(text);
            byte[] body = w.ToArray();
            Broadcast(body, (ushort)body.Length, null);
        }

        // ==== Действие хоста от себя (pid 0): сел/курит/пьёт/руль ====
        public void HostAction(byte action, short objectId, float value)
        {
            PacketWriter w = new PacketWriter();
            w.PutByte((byte)Packet.Action);
            w.PutShort(0);
            w.PutByte(action);
            w.PutShort(objectId);
            w.PutFloat(value);
            byte[] body = w.ToArray();
            Broadcast(body, (ushort)body.Length, null);
        }

        // ==== Отправка (потокобезопасная побайтово-цельная) ====
        private void Send(ClientConn conn, byte[] body)
        {
            try
            {
                lock (conn.SendLock)
                {
                    NetUtil.SendFrame(conn.Stream, (ushort)body.Length, body);
                }
            }
            catch (Exception) { Drop(conn, "потеря связи"); }
        }

        private void SendWelcome(ClientConn conn)
        {
            PacketWriter w = new PacketWriter();
            w.PutByte((byte)Packet.Welcome);
            w.PutShort(conn.Pid);
            w.PutString(MpConfig.PlayerName);
            Send(conn, w.ToArray());
        }

        private void SendPlayerList(ClientConn to)
        {
            PacketWriter w = new PacketWriter();
            w.PutByte((byte)Packet.PlayerList);
            lock (sync)
            {
                w.PutShort((short)clients.Count);
                for (int i = 0; i < clients.Count; i++)
                {
                    w.PutShort(clients[i].Pid);
                    w.PutString(clients[i].Name);
                }
            }
            Send(to, w.ToArray());
        }

        private void BroadcastPlayerList()
        {
            PacketWriter w = new PacketWriter();
            w.PutByte((byte)Packet.PlayerList);
            lock (sync)
            {
                w.PutShort((short)clients.Count);
                for (int i = 0; i < clients.Count; i++)
                {
                    w.PutShort(clients[i].Pid);
                    w.PutString(clients[i].Name);
                }
            }
            Broadcast(w.ToArray(), (ushort)w.Length, null);
        }

        private void Relay(ClientConn from, byte[] body)
        {
            Broadcast(body, (ushort)body.Length, from);
        }

        private void Broadcast(byte[] body, ushort len, ClientConn except)
        {
            List<ClientConn> snapshot;
            List<ClientConn> dead = new List<ClientConn>();
            lock (sync)
            {
                if (clients.Count == 0) return;
                snapshot = new List<ClientConn>(clients);
            }
            for (int i = 0; i < snapshot.Count; i++)
            {
                ClientConn c = snapshot[i];
                if (except != null && ReferenceEquals(c, except)) continue;
                try
                {
                    lock (c.SendLock) { NetUtil.SendFrame(c.Stream, len, body); }
                }
                catch (Exception) { dead.Add(c); }
            }
            // Схлопнутые сокеты выкидываем после цикла — без каскадов
            for (int i = 0; i < dead.Count; i++)
                Drop(dead[i], "сбой рассылки");
        }

        private void Drop(ClientConn conn, string reason)
        {
            bool removed = false;
            lock (sync)
            {
                if (clients.Contains(conn)) { clients.Remove(conn); removed = true; }
            }
            try { conn.Stream.Close(); } catch { }
            try { conn.Tcp.Close(); } catch { }

            if (!removed) return; // уже выпущен

            // Явное PlayerLeft всем, у кого остался его аватар (rev: К3)
            broadcastLeft(conn.Pid);
            ModLog.Info("Хост: #" + conn.Pid + " (" + conn.Name + ") " + reason);
            BroadcastPlayerList();
        }

        private void broadcastLeft(short pid)
        {
            PacketWriter w = new PacketWriter();
            w.PutByte((byte)Packet.PlayerLeft);
            w.PutShort(pid);
            byte[] body = w.ToArray();
            Broadcast(body, (ushort)body.Length, null);
        }

        private static void PushLeft(short pid)
        {
            MpManager.PushEventLeft(pid);
        }

        private static double EnvMs()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
