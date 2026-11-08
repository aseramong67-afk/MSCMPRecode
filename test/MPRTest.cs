// =============================================================
// MP Recode Remastered — автономный агентский тест-раннер
// Проверяет протокол через РЕАЛЬНЫЙ TCP (loopback): фрейминг, pids,
// broadcast, чат, drop игрока, переподключение (постоянный режим).
// Вне Unity/MSCLoader — минимальный дублирующий слой сети.
// Запуск: MPRTest.exe [количествоБотов]
// =============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace MPRTest
{
    static class Program
    {
        static int passCount, failCount;

        static void Pass(string s) { passCount++; Console.WriteLine("  [PASS] " + s); }
        static void Fail(string s) { failCount++; Console.WriteLine("  [FAIL] " + s); }

        // ==== фрейминг (копия логики мода) ====
        static void SendFrame(NetworkStream s, byte[] body)
        {
            ushort len = (ushort)body.Length;
            byte head0 = (byte)(len & 0xFF);
            byte head1 = (byte)((len >> 8) & 0xFF);
            byte[] head = new byte[2] { head0, head1 };
            s.Write(head, 0, 2);
            s.Write(body, 0, len);
            s.Flush();
        }

        static byte[] ReadFrame(NetworkStream s)
        {
            int b0 = s.ReadByte(); int b1 = s.ReadByte();
            if (b0 < 0 || b1 < 0) return null;
            int len = (b0 | (b1 << 8)) & 0xFFFF;
            if (len == 0) return new byte[0];
            byte[] buf = new byte[len];
            int off = 0;
            while (off < len)
            {
                int r = s.Read(buf, off, len - off);
                if (r <= 0) return null;
                off += r;
            }
            return buf;
        }

        static byte[] Pack(byte type, Action<BinaryWriter> build)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                BinaryWriter w = new BinaryWriter(ms);
                w.Write(type);
                build(w);
                return ms.ToArray();
            }
        }

        static void WStr(BinaryWriter w, string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            w.Write((ushort)b.Length);
            w.Write(b);
        }

        static string RStr(BinaryReader r)
        {
            ushort n = r.ReadUInt16();
            return Encoding.UTF8.GetString(r.ReadBytes(n));
        }

        const byte P_Hello = 1, P_Welcome = 2, P_State = 3, P_Chat = 4, P_Left = 5, P_List = 6;
        const byte P_Action = 11, P_WorldEvent = 12;

        // ==== мини-хост (семантика MpHostServer) ====
        class HostClientConn
        {
            public short Pid;
            public string Name = "?";
            public TcpClient Tcp;
            public NetworkStream S;
        }

        static TcpListener listener;
        static List<HostClientConn> conns = new List<HostClientConn>();
        static object syncRoot = new object();
        static short nextPid = 1;

        static void HostLoop()
        {
            listener = new TcpListener(IPAddress.Loopback, 10819);
            listener.Start();
            while (true)
            {
                TcpClient tcp;
                try { tcp = listener.AcceptTcpClient(); }
                catch { return; }
                tcp.NoDelay = true;
                HostClientConn c = new HostClientConn();
                c.Tcp = tcp; c.S = tcp.GetStream();
                lock (syncRoot) { c.Pid = nextPid++; conns.Add(c); }
                new Thread(delegate() { HandleClient(c); }).Start();
            }
        }

        static void HandleClient(HostClientConn c)
        {
            try
            {
                while (true)
                {
                    byte[] body = ReadFrame(c.S);
                    if (body == null) break;
                    byte type = body[0];
                    MemoryStream ms2 = new MemoryStream(body);
                    ms2.Position = 1;
                    BinaryReader r = new BinaryReader(ms2);

                    switch (type)
                    {
                        case P_Hello:
                            c.Name = RStr(r);
                            lock (syncRoot)
                            {
                                byte[] welc = Pack(P_Welcome, delegate(BinaryWriter w) { w.Write(c.Pid); WStr(w, "Хост"); });
                                SendFrame(c.S, welc);
                                SendList(c.S);
                                BroadcastList();
                                // Мир-снапшот: время и погода (как OnClientReady в моде)
                                SendFrame(c.S, WFrame((byte)4, 2f, 17f, 45f));
                                SendFrame(c.S, WFrame((byte)5, 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f));
                            }
                            break;
                        case P_State:
                            {
                                ushort len = (ushort)body.Length;
                                lock (syncRoot)
                                    foreach (HostClientConn o in conns)
                                        if (o != c)
                                        {
                                            byte[] fwd = new byte[body.Length];
                                            Array.Copy(body, fwd, fwd.Length);
                                            SendFrame(o.S, fwd);
                                        }
                            }
                            break;
                        case P_Action:
                            {
                                // Подменяем pid отправителя на честный (как в моде) и рассылаем
                                byte[] fwd = new byte[body.Length];
                                Array.Copy(body, fwd, fwd.Length);
                                fwd[1] = (byte)(c.Pid & 0xFF);
                                fwd[2] = (byte)((c.Pid >> 8) & 0xFF);
                                lock (syncRoot)
                                    foreach (HostClientConn o in conns)
                                        if (o != c) SendFrame(o.S, fwd);
                            }
                            break;
                        case P_Chat:
                            {
                                string text = RStr(r);
                                lock (syncRoot)
                                    foreach (HostClientConn o in conns)
                                        if (o != c)
                                            SendFrame(o.S, Pack(P_Chat, delegate(BinaryWriter w) { w.Write(c.Pid); WStr(w, text); }));
                            }
                            break;
                    }
                }
            }
            catch { }
            finally
            {
                lock (syncRoot) { conns.Remove(c); }
                try { c.Tcp.Close(); } catch { }
                lock (syncRoot)
                {
                    byte[] left = Pack(P_Left, delegate(BinaryWriter w) { w.Write(c.Pid); });
                    foreach (HostClientConn o in conns) SendFrame(o.S, left);
                    BroadcastList();
                }
            }
        }

        // Кадр WorldEvent: [type:1][kind:1][objHash:4][evHash:4][f1..f8:32][string]
        static byte[] WFrame(byte kind, params float[] fs)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                BinaryWriter w = new BinaryWriter(ms);
                w.Write(P_WorldEvent);
                w.Write(kind);
                w.Write(0); // objHash
                w.Write(0); // evHash
                for (int i = 0; i < 8; i++)
                    w.Write(i < fs.Length ? fs[i] : 0f);
                w.Write((ushort)0); // пустая строка
                return ms.ToArray();
            }
        }

        static void SendList(NetworkStream s)
        {
            lock (syncRoot)
            {
                byte[] list = Pack(P_List, delegate(BinaryWriter w)
                {
                    w.Write((short)conns.Count);
                    foreach (HostClientConn c in conns) { w.Write(c.Pid); WStr(w, c.Name); }
                });
                SendFrame(s, list);
            }
        }

        static void BroadcastList()
        {
            lock (syncRoot)
            {
                byte[] list = Pack(P_List, delegate(BinaryWriter w)
                {
                    w.Write((short)conns.Count);
                    foreach (HostClientConn c in conns) { w.Write(c.Pid); WStr(w, c.Name); }
                });
                foreach (HostClientConn o in conns) SendFrame(o.S, list);
            }
        }

        class ActHit
        {
            public short Pid;
            public byte Action;
            public short Obj;
            public float Val;
        }

        // ==== бот-агент ====
        class Bot
        {
            public string Name;
            public TcpClient Tcp;
            public NetworkStream S;
            public short Pid = -1;
            public string HostName;
            public List<string> Chats = new List<string>();
            public List<short> Lefts = new List<short>();
            public List<int> ListSizes = new List<int>();
            public List<ActHit> Actions = new List<ActHit>();
            public List<byte> WorldKinds = new List<byte>();
            public Dictionary<short, int> StatesFrom = new Dictionary<short, int>();
            public bool Dead;

            public static Bot Connect(string name)
            {
                Bot b = new Bot();
                b.Name = name;
                b.Tcp = new TcpClient();
                b.Tcp.Connect(IPAddress.Loopback, 10819);
                b.Tcp.NoDelay = true;
                b.S = b.Tcp.GetStream();
                SendFrame(b.S, Pack(P_Hello, delegate(BinaryWriter w) { WStr(w, name); }));
                new Thread(b.Recv).Start();
                return b;
            }

            public void SendState(float x, float y, float z, float rotY)
            {
                SendFrame(S, Pack(P_State, delegate(BinaryWriter w)
                {
                    w.Write(Pid); w.Write(x); w.Write(y); w.Write(z); w.Write(rotY);
                }));
            }

            public void SendChat(string t)
            {
                SendFrame(S, Pack(P_Chat, delegate(BinaryWriter w) { WStr(w, t); }));
            }

            public void SendAction(byte action, short objectId, float value)
            {
                SendFrame(S, Pack(P_Action, delegate(BinaryWriter w)
                {
                    w.Write(Pid); w.Write(action); w.Write(objectId); w.Write(value);
                }));
            }

            void Recv()
            {
                try
                {
                    while (true)
                    {
                        byte[] body = ReadFrame(S);
                        if (body == null) break;
                        byte type = body[0];
                        MemoryStream ms2 = new MemoryStream(body);
                        ms2.Position = 1;
                        BinaryReader r = new BinaryReader(ms2);
                        switch (type)
                        {
                            case P_Welcome:
                                Pid = r.ReadInt16();
                                HostName = RStr(r);
                                break;
                            case P_State:
                                {
                                    short pid = r.ReadInt16();
                                    float x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle(), ry = r.ReadSingle();
                                    if (ry < -0.01f || ry > 6.3f) { } // валидность поворота
                                    int c;
                                    if (!StatesFrom.TryGetValue(pid, out c)) StatesFrom[pid] = 1;
                                    else StatesFrom[pid] = c + 1;
                                    break;
                                }
                            case P_Chat:
                                {
                                    short pid = r.ReadInt16();
                                    string t = RStr(r);
                                    Chats.Add("[" + pid + "] " + t);
                                    break;
                                }
                            case P_Left:
                                Lefts.Add(r.ReadInt16());
                                break;
                            case P_Action:
                                {
                                    ActHit a = new ActHit();
                                    a.Pid = r.ReadInt16();
                                    a.Action = r.ReadByte();
                                    a.Obj = r.ReadInt16();
                                    a.Val = r.ReadSingle();
                                    Actions.Add(a);
                                    break;
                                }
                            case P_WorldEvent:
                                {
                                    byte kind = r.ReadByte();
                                    WorldKinds.Add(kind);
                                    break;
                                }
                            case P_List:
                                ListSizes.Add(r.ReadInt16());
                                break;
                        }
                    }
                }
                catch { Dead = true; }
            }
        }

        // ==== сценарии ====
        static void Main(string[] args)
        {
            Console.WriteLine("=== MP Recode Remastered: агентские тесты на ботах ===");
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            int nBots = 3;
            if (args.Length > 0) int.TryParse(args[0], out nBots);
            if (nBots < 2) nBots = 2;
            if (nBots > 6) nBots = 6;

            new Thread(HostLoop) { IsBackground = true }.Start();
            Thread.Sleep(300);

            T1_BotsConnectGetPids(nBots);
            T2_StateRelay(nBots);
            T3_ChatRelay();
            T6_ActionRelay();
            T4_Disconnect(goesAwayIndex: nBots - 1, expectListAfter: nBots - 1);
            T5_Rejoin(nBots - 1);
            T7_WorldSnapshot();

            Console.WriteLine();
            Console.WriteLine("Итог: PASS=" + passCount + " FAIL=" + failCount);
            Environment.Exit(failCount == 0 ? 0 : 1);
        }

        static void WaitFor(Func<bool> cond, int timeoutMs, string what)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                if (cond()) return;
                Thread.Sleep(100);
                waited += 100;
            }
        }

        static void T1_BotsConnectGetPids(int n)
        {
            Console.WriteLine("[T1] " + n + " ботов подключаются и получают уникальные pid");
            bots.Clear();
            for (int i = 0; i < n; i++)
            {
                Bot b = Bot.Connect("Бот-" + (i + 1));
                bots.Add(b);
            }
            WaitFor(delegate()
            {
                foreach (Bot b in bots) if (b.Pid < 0) return false;
                return true;
            }, 5000, "welcome");

            bool all = true;
            foreach (Bot b in bots) if (b.Pid < 0) { all = false; Fail(b.Name + " не получил Welcome"); break; }
            if (all)
            {
                HashSet<short> set = new HashSet<short>();
                foreach (Bot b in bots) set.Add(b.Pid);
                if (set.Count == bots.Count) Pass("Welcome: все pid уникальны (" + set.Count + " из " + bots.Count + ")");
                else Fail("pid не уникальны: " + set.Count + " из " + bots.Count);
            }
        }

        static List<Bot> bots = new List<Bot>();

        static void T2_StateRelay(int n)
        {
            Console.WriteLine("[T2] Асинхронный relay состояния: каждый бот шлёт состояние, все получают чужие");
            foreach (Bot b in bots) b.StatesFrom.Clear();

            for (int tick = 0; tick < 30; tick++)
            {
                float t = tick * 0.1f;
                foreach (Bot b in bots) b.SendState(MathF2.Cos(t) * (b.Pid % 5 + 2), 0f, MathF2.Sin(t) * (b.Pid % 5 + 2), t);
                Thread.Sleep(33); // ~30 Гц
            }

            bool ok = true;
            foreach (Bot b in bots)
            {
                foreach (Bot other in bots)
                {
                    if (other == b) continue;
                    int c;
                    if (!b.StatesFrom.TryGetValue(other.Pid, out c) || c < 10)
                    {
                        ok = false;
                        Fail(b.Name + " получил только " + (c) + " состояний от " + other.Name);
                    }
                }
            }
            if (ok) Pass("Все боты получили >= 10 состояний от каждого (" + (30) + " отправлено)");
        }

        static void T3_ChatRelay()
        {
            Console.WriteLine("[T3] Чат-релей: сообщение бота доходит остальным, pid отправителя корректен");
            Bot sender = bots[0];
            foreach (Bot b in bots) b.Chats.Clear();
            sender.SendChat("тестовое сообщение");
            WaitFor(delegate()
            {
                foreach (Bot b in bots)
                {
                    if (b == sender) continue;
                    if (b.Chats.Count == 0) return false;
                }
                return true;
            }, 3000, "chat");

            bool ok = true;
            foreach (Bot b in bots)
            {
                if (b == sender) continue;
                if (b.Chats.Count == 0 || !b.Chats[0].StartsWith("[" + sender.Pid + "]"))
                {
                    ok = false;
                    Fail(b.Name + " получил чат: " + (b.Chats.Count > 0 ? b.Chats[0] : "(пусто)"));
                }
            }
            if (ok) Pass("Чат доставлен всем, pid отправителя " + sender.Pid + " подставлен хостом");
        }

        static void T4_Disconnect(int goesAwayIndex, int expectListAfter)
        {
            Console.WriteLine("[T4] Обрыв клиента: PlayerLeft + обновление playerlist у оставшихся");
            Bot leaving = bots[goesAwayIndex];
            foreach (Bot b in bots)
            {
                if (b != leaving) b.Lefts.Clear();
                b.ListSizes.Clear();
            }
            try { leaving.S.Close(); } catch { }
            try { leaving.Tcp.Close(); } catch { }

            WaitFor(delegate()
            {
                foreach (Bot b in bots)
                {
                    if (b == leaving) continue;
                    if (b.Lefts.Count == 0 || b.ListSizes.Count == 0 || b.ListSizes[b.ListSizes.Count - 1] != expectListAfter)
                        return false;
                }
                return true;
            }, 3000, "left+list");

            bool ok = true;
            foreach (Bot b in bots)
            {
                if (b == leaving) continue;
                if (b.Lefts.Count == 0) { ok = false; Fail(b.Name + ": не получил PlayerLeft"); }
                int last = b.ListSizes.Count > 0 ? b.ListSizes[b.ListSizes.Count - 1] : -1;
                if (last != expectListAfter)
                    Fail(b.Name + ": последний размер списка " + last + ", ожидался " + expectListAfter);
            }
            if (ok) Pass("PlayerLeft разослан, playerlist теперь " + expectListAfter);

            bots.RemoveAt(goesAwayIndex);
        }

        static void T6_ActionRelay()
        {
            Console.WriteLine("[T6] Релей действий: сидение в машине и руль с верным pid/objectId");
            Bot sender = bots[0];
            foreach (Bot b in bots) b.Actions.Clear();

            sender.SendAction(1, (short)0x1234, 0f);      // SitIn, objectId = хеш машины
            Thread.Sleep(120);
            sender.SendAction(7, 0, 123.4f);              // Wheel

            WaitFor(delegate()
            {
                foreach (Bot b in bots)
                {
                    if (b == sender) continue;
                    if (b.Actions.Count < 2) return false;
                }
                return true;
            }, 4000, "actions");

            bool ok = true;
            foreach (Bot b in bots)
            {
                if (b == sender) continue;
                if (b.Actions.Count < 2)
                {
                    ok = false;
                    Fail(b.Name + " получил " + b.Actions.Count + " действий");
                    continue;
                }
                ActHit sit = b.Actions[0], wheel = b.Actions[1];
                if (sit.Pid != sender.Pid || sit.Action != 1 || sit.Obj != (short)0x1234)
                    Fail(b.Name + ": SitIn pid=" + sit.Pid + " action=" + sit.Action + " obj=" + sit.Obj);
                if (wheel.Action != 7 || Math.Abs(wheel.Val - 123.4f) > 0.01f)
                    Fail(b.Name + ": Wheel action=" + wheel.Action + " val=" + wheel.Val);
            }
            if (ok) Pass("Действия SitIn/Wheel разосланы с верным pid=" + sender.Pid + " и objectId=0x1234");
        }

        static void T7_WorldSnapshot()
        {
            Console.WriteLine("[T7] Мир-снапшот: новый клиент сразу получает время (kind=4) и погоду (kind=5)");
            Bot wb = Bot.Connect("Бот-мир");
            WaitFor(delegate() { return wb.WorldKinds.Count >= 2; }, 4000, "world snapshot");

            bool hasTime = wb.WorldKinds.Contains((byte)4);
            bool hasWeather = wb.WorldKinds.Contains((byte)5);
            if (hasTime && hasWeather) Pass("Снапшот мира получен: kind 4 (время) и 5 (погода)");
            else Fail("Снапшот мира: kinds=" + (wb.WorldKinds.Count > 0 ? "частично" : "пусто")
                      + (hasTime ? " [время ок]" : " [нет времени]") + (hasWeather ? " [погода ок]" : " [нет погоды]"));
            if (wb.Pid >= 0) Pass("Клиент мира подключился с pid=" + wb.Pid);
            else Fail("Клиент мира не получил Welcome");
        }

        static void T5_Rejoin(int idx)
        {
            Console.WriteLine("[T5] Постоянный режим: бот восстанавливается и снова получает Welcome");
            Bot back = Bot.Connect("Бот-возврат");
            WaitFor(delegate() { return back.Pid >= 0; }, 5000, "rejoin welcome");

            if (back.Pid >= 0)
            {
                bool unique = true;
                foreach (Bot b in bots) if (b.Pid == back.Pid) unique = false;
                if (unique) Pass("Повторный Welcome: pid " + back.Pid + " уникален");
                else Fail("Повторный pid " + back.Pid + " совпал с существующим");
            }
            else Fail("Бот-возврат не получил Welcome");
        }
    }

    static class MathF2
    {
        public static float Cos(float x) { return (float)Math.Cos(x); }
        public static float Sin(float x) { return (float)Math.Sin(x); }
    }
}
