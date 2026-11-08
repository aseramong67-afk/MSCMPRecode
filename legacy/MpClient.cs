using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;

namespace MSCMPRecode
{
    // ==== Клиент: ПОСТОЯННОЕ (persistent) подключение через туннель cloudpub, метод 2 ====
    // Любой обрыв -> автоматическое переподключение и повторный Hello.
    public class MpTunnelClient : IDisposable
    {
        private TcpClient tcp;
        private NetworkStream stream;
        private Thread recvThread;
        private Thread keepThread;
        private readonly object sendLock = new object();
        private volatile bool connectRequested;
        private volatile bool connected;
        private double nextReconnectAt;
        private string host;
        private int port;
        public short MyPid = -1;
        public string HostName = "хост";

        public bool Connected { get { return connected; } }
        public bool ConnectRequested { get { return connectRequested; } }
        public string Endpoint { get { return host + ":" + port; } }

        public void Connect(string tunnelHost, int tunnelPort)
        {
            host = tunnelHost;
            port = tunnelPort;
            connectRequested = true;

            if (recvThread == null || !recvThread.IsAlive)
            {
                recvThread = new Thread(RunPersistent);
                recvThread.IsBackground = true;
                recvThread.Start();
            }
            if (keepThread == null || !keepThread.IsAlive)
            {
                keepThread = new Thread(KeepAliveLoop);
                keepThread.IsBackground = true;
                keepThread.Start();
            }
        }

        public void Disconnect()
        {
            connectRequested = false;
            CloseSocket();
        }

        private void CloseSocket()
        {
            connected = false;
            try { if (stream != null) stream.Close(); stream = null; } catch { }
            try { if (tcp != null) tcp.Close(); tcp = null; } catch { }
        }

        private void RunPersistent()
        {
            while (connectRequested)
            {
                if (!connected)
                {
                    TryConnectOnce();
                    if (connected)
                    {
                        RecvLoop();
                    }
                    else
                    {
                        nextReconnectAt = Now() + 3000;
                        while (connectRequested && !connected && Now() < nextReconnectAt)
                            Thread.Sleep(200);
                    }
                }
                Thread.Sleep(50);
            }
        }

        private bool TryConnectOnce()
        {
            try
            {
                ModLog.Info("Клиент: подключаюсь к туннелю " + host + ":" + port + " ...");
                tcp = new TcpClient();
                IAsyncResult ar = tcp.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(5000, false))
                {
                    try { tcp.Close(); } catch { }
                    ModLog.Warn("Клиент: таймаут подключения.");
                    return false;
                }
                tcp.EndConnect(ar);
                tcp.NoDelay = true;
                stream = tcp.GetStream();
                connected = true;
                SendHello();
                ModLog.Info("Клиент: соединение установлено.");
                return true;
            }
            catch (Exception e)
            {
                CloseSocket();
                ModLog.Warn("Клиент: не удалось подключиться: " + e.Message);
                return false;
            }
        }

        private void SendHello()
        {
            PacketWriter w = new PacketWriter();
            w.PutByte((byte)Packet.Hello);
            w.PutString(MpConfig.PlayerName);
            w.PutByte((byte)0x01); // ревизия клиента
            Send(w.ToArray());

            PacketWriter s = new PacketWriter();
            s.PutByte((byte)Packet.SteamInfo);
            s.PutString("spacewar/steam");
            Send(s.ToArray());
        }

        // Постоянный канал: пинг каждые 15 сек не даёт туннелю cloudpub отвалиться
        private void KeepAliveLoop()
        {
            double lastPing = 0;
            while (connectRequested)
            {
                if (connected)
                {
                    if (Now() - lastPing > 15000)
                    {
                        lastPing = Now();
                        PacketWriter w = new PacketWriter();
                        w.PutByte((byte)Packet.Ping);
                        Send(w.ToArray());
                    }
                }
                Thread.Sleep(500);
            }
        }

        private void RecvLoop()
        {
            try
            {
                while (connectRequested && connected)
                {
                    byte[] body = NetUtil.ReadFrame(stream);
                    if (body == null) break;
                    if (body.Length == 0) continue;

                    Packet type = (Packet)body[0];
                    byte[] rest = new byte[body.Length - 1];
                    Array.Copy(body, 1, rest, 0, rest.Length);
                    HandlePacket(type, rest);
                }
            }
            catch (Exception) { }

            if (connectRequested)
            {
                CloseSocket();
                MpManager.PushEventLocalSystem("Туннель прервался — переподключение через 3 сек...");
                ModLog.Warn("Клиент: соединение потеряно, переподключаюсь.");
            }
        }

        private void HandlePacket(Packet type, byte[] rest)
        {
            switch (type)
            {
                case Packet.Welcome:
                    {
                        PacketReader r = new PacketReader(rest);
                        MyPid = r.GetShort();
                        HostName = r.GetString();
                        ModLog.Info("Клиент: хост " + HostName + " выдал pid " + MyPid);
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
                        st.LastUpdate = TimeSafe();
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
                case Packet.Action:
                    {
                        PacketReader r = new PacketReader(rest);
                        MpManager.PushEventAction(r.GetShort(), rest);
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
                default:
                    break;
            }
        }

        // ==== Исходящие ====
        public void SendState(short pid, float x, float y, float z, float rotY, byte flags)
        {
            if (!connected) return;
            PacketWriter w = new PacketWriter();
            w.PutByte((byte)Packet.PlayerState);
            w.PutShort(pid);
            w.PutFloat(x);
            w.PutFloat(y);
            w.PutFloat(z);
            w.PutFloat(rotY);
            w.PutByte(flags);
            Send(w.ToArray());
        }

        public void SendChat(string text)
        {
            if (!connected) return;
            PacketWriter w = new PacketWriter();
            w.PutByte((byte)Packet.Chat);
            w.PutShort(MyPid);
            w.PutString(text);
            Send(w.ToArray());
        }

        public void SendAction(byte action, short objectId, float value)
        {
            if (!connected) return;
            PacketWriter w = new PacketWriter();
            w.PutByte((byte)Packet.Action);
            w.PutShort(MyPid);
            w.PutByte(action);
            w.PutShort(objectId);
            w.PutFloat(value);
            Send(w.ToArray());
        }

        private void Send(byte[] body)
        {
            // Кадр целиком под одним локом — параллельные потоки (state/chat/ping) не перемешаются
            lock (sendLock)
            {
                NetworkStream s = stream;
                if (s == null) return;
                try { NetUtil.SendFrame(s, (ushort)body.Length, body); }
                catch (Exception) { CloseSocket(); }
            }
        }

        public void Dispose()
        {
            Disconnect();
        }

        private static double TimeSafe()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
        }

        private static double Now()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
        }
    }
}
