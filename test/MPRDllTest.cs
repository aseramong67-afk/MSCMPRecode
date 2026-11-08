// =============================================================
// Self-test РЕАЛЬНЫХ классов мода из MSCMPRecode.dll (pure-logic,
// без Steam/Unity): LobbyCode, PacketWriter/Reader, NetUtil,
// MpWorldSync.Hash/ShortId. Steam-часть headless-тестом не покрыта.
// =============================================================
using System;
using System.Reflection;
using System.Text;

namespace MPRDllTest
{
    static class Program
    {
        static int pass, fails;
        static Assembly mod;

        static void Pass(string s) { pass++; Console.WriteLine("  [PASS] " + s); }
        static void Fail(string s) { fails++; Console.WriteLine("  [FAIL] " + s); }

        static void Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            Console.WriteLine("=== Pure-logic классы MSCMPRecode.dll (headless) ===");

            string managedDir = @"E:\My Summer Car - MP Copy\mysummercar_Data\Managed";
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object s2, ResolveEventArgs e2)
            {
                string n = (e2.Name.Split(',')[0]).Trim() + ".dll";
                string p = System.IO.Path.Combine(managedDir, n);
                if (System.IO.File.Exists(p))
                    return Assembly.LoadFrom(p);
                return null;
            };

            string dll = @"E:\My Summer Car - MP Copy\Mods\MSCMPRecode.dll";
            try
            {
                mod = Assembly.LoadFrom(dll);
                Pass("DLL загружен: " + mod.GetName().Name + " v" + mod.GetName().Version);
            }
            catch (Exception e)
            {
                Fail("Не могу загрузить " + dll + ": " + e.Message);
                Environment.Exit(1);
            }

            // Глушим MSCLoader-консоль (без Unity печать падает)
            Type logType = mod.GetType("MSCMPRecode.ModLog");
            if (logType != null)
            {
                FieldInfo sink = logType.GetField("Sink");
                if (sink != null)
                    sink.SetValue(null, new Action<string>(delegate(string m)
                    {
                        try { Console.WriteLine("  [log] " + m); } catch { }
                    }));
            }

            T1_LobbyCode();
            T2_PacketRoundtrip();
            T3_NetUtilState();
            T4_Hashes();
            T5_Types();

            Console.WriteLine();
            Console.WriteLine("Итог: PASS=" + pass + " FAIL=" + fails);
            Environment.Exit(fails == 0 ? 0 : 1);
        }

        // T1: код сессии: ulong -> код -> ulong (схема BeerMP LobbyCodeParser)
        static void T1_LobbyCode()
        {
            Type t = mod.GetType("MSCMPRecode.LobbyCode");
            if (t == null) { Fail("Нет класса LobbyCode"); return; }
            MethodInfo enc = t.GetMethod("Encode");
            MethodInfo dec = t.GetMethod("Decode");

            // Реальный вид SteamID лобби: unsigned 32/64-бит
            ulong[] ids = new ulong[] {
                0, 1, 63, 64, 4095, 123456789,
                0xFFFFFFFFu,
                10977524170181397728ul,
                15536329619649902211ul
            };
            bool ok = true;
            for (int i = 0; i < ids.Length; i++)
            {
                string code = (string)enc.Invoke(null, new object[] { ids[i] });
                ulong back = (ulong)dec.Invoke(null, new object[] { code });
                if (back != ids[i]) { ok = false; Fail("Round-trip " + ids[i] + " -> " + code + " -> " + back); }
            }
            if (ok) Pass("LobbyCode round-trip по " + ids.Length + " ID (макс длина кода 11 симв.)");

            string c1 = (string)enc.Invoke(null, new object[] { 0xFFFFFFFFu });
            if (c1.Length == 6) Pass("Код 32-битного ID: 6 символов (" + c1 + ")");  // ceil(32/6)
            else Fail("Код 32-битного ID: длина " + (c1 == null ? -1 : c1.Length));

            ulong d1 = (ulong)dec.Invoke(null, new object[] { "AAAAAAAAAAA" });
            // 'A' -> 10, младшие биты заполнены; просто проверяем, что не падает
            Pass("Decode устойчив к некороткому коду (" + d1.ToString() + ")");

            ulong d0 = (ulong)dec.Invoke(null, new object[] { "8E-4f_2Z !@" });
            Pass("Decode с мусором/пробелами не падает (" + d0.ToString() + ")");
        }

        // T2: PacketWriter/Reader: бинарная совместимость строк и чисел
        static void T2_PacketRoundtrip()
        {
            Type w = mod.GetType("MSCMPRecode.PacketWriter");
            Type r = mod.GetType("MSCMPRecode.PacketReader");
            if (w == null || r == null) { Fail("Нет PacketWriter/Reader"); return; }

            object pw = Activator.CreateInstance(w);
            foreach (var m in w.GetMethods())
            {
                if (m.Name == "PutByte") m.Invoke(pw, new object[] { (byte)12 });
                if (m.Name == "PutShort") m.Invoke(pw, new object[] { (short)-4321 });
                if (m.Name == "PutInt") m.Invoke(pw, new object[] { -1 });
                if (m.Name == "PutFloat") m.Invoke(pw, new object[] { 3.14159f });
                if (m.Name == "PutString") m.Invoke(pw, new object[] { "МП: привет, мир! ☺" });
            }
            byte[] data = (byte[])w.GetMethod("ToArray").Invoke(pw, null);

            object pr = Activator.CreateInstance(r, new object[] { data });
            byte b = (byte)r.GetMethod("GetByte").Invoke(pr, null);
            short s = (short)r.GetMethod("GetShort").Invoke(pr, null);
            int i = (int)r.GetMethod("GetInt").Invoke(pr, null);
            float f = (float)r.GetMethod("GetFloat").Invoke(pr, null);
            string str = (string)r.GetMethod("GetString").Invoke(pr, null);

            if (b == 12 && s == -4321 && i == -1 && f == 3.14159f && str == "МП: привет, мир! ☺")
                Pass("PacketWriter/Reader: byte/short/int/float/UTF8-string round-trip");
            else
                Fail("Поля не совпали: " + b + "," + s + "," + i + "," + f + ",'" + str + "'");

            bool more = (bool)r.GetProperty("HasMore").GetValue(pr, null);
            if (!more) Pass("HasMore=false после полного чтения");
            else Fail("HasMore=true после полного чтения");
        }

        // T3: NetUtil.ParsePlayerState + WithState [pid:2][x:4][y:4][z:4][rotY:4][flags:1]
        static void T3_NetUtilState()
        {
            Type n = mod.GetType("MSCMPRecode.NetUtil");
            if (n == null) { Fail("Нет NetUtil"); return; }
            MethodInfo ps = n.GetMethod("ParsePlayerState");
            MethodInfo with = n.GetMethod("WithState");

            Type stT = mod.GetType("MSCMPRecode.NetPlayerState");
            object st = Activator.CreateInstance(stT);
            stT.GetField("Pid").SetValue(st, (short)7);
            stT.GetField("X").SetValue(st, 123.5f);
            stT.GetField("Y").SetValue(st, -2f);
            stT.GetField("Z").SetValue(st, 0.01f);
            stT.GetField("RotY").SetValue(st, 270.5f);
            stT.GetField("Flags").SetValue(st, (byte)3);

            object pw = Activator.CreateInstance(mod.GetType("MSCMPRecode.PacketWriter"));
            with.Invoke(null, new object[] { pw, st });
            byte[] wire = (byte[])mod.GetType("MSCMPRecode.PacketWriter").GetMethod("ToArray").Invoke(pw, null);

            if (wire.Length == 19) Pass("WithState: 19 байт (2+16+1) — по спецификации");
            else Fail("WithState: неожиданная длина " + wire.Length);

            object back = ps.Invoke(null, new object[] { wire });
            short pid = (short)stT.GetField("Pid").GetValue(back);
            float x = (float)stT.GetField("X").GetValue(back);
            float rot = (float)stT.GetField("RotY").GetValue(back);
            byte fl = (byte)stT.GetField("Flags").GetValue(back);
            if (pid == 7 && x == 123.5f && rot == 270.5f && fl == 3)
                Pass("ParsePlayerState: pid/x/rotY/flags совпали");
            else
                Fail("ParsePlayerState: " + pid + "," + x + "," + rot + "," + fl);
        }

        // T4: стабильные хеши объектов мира
        static void T4_Hashes()
        {
            Type w = mod.GetType("MSCMPRecode.MpWorldSync");
            if (w == null) { Fail("Нет MpWorldSync"); return; }
            MethodInfo hash = w.GetMethod("Hash");
            MethodInfo sid = w.GetMethod("ShortId");

            uint h1 = (uint)hash.Invoke(null, new object[] { "SATSUMA(557, 545, 968)" });
            uint h2 = (uint)hash.Invoke(null, new object[] { "SATSUMA(557, 545, 968)" });
            uint h3 = (uint)hash.Invoke(null, new object[] { "GOLOSARY(562)" });
            if (h1 == h2 && h1 != h3)
                Pass("Hash стабилен и различает объекты: " + h1 + " vs " + h3);
            else
                Fail("Hash: " + h1 + "," + h2 + "," + h3);

            short s1 = (short)sid.Invoke(null, new object[] { "SATSUMA(557, 545, 968)" });
            short s2 = (short)sid.Invoke(null, new object[] { "SATSUMA(557, 545, 968)" });
            if (s1 == s2 && s1 != 0) Pass("ShortId (машинный id): " + s1);
            else Fail("ShortId: " + s1 + "," + s2);
        }

        // T5: типы протокола + новый HostLost
        static void T5_Types()
        {
            Type pk = mod.GetType("MSCMPRecode.Packet");
            if (pk == null) { Fail("Нет enum Packet"); return; }
            Array names = pk.GetEnumValues();
            bool hasHello = false, hasWorld = false, hasLost = false;
            foreach (var v in names)
            {
                string n = v.ToString();
                if (n == "Hello") hasHello = true;
                if (n == "WorldEvent") hasWorld = true;
                if (n == "HostLost") hasLost = true;
            }
            if (hasHello && hasWorld && hasLost)
                Pass("Packet: Hello/WorldEvent/HostLost на месте ( Steam-слой + HostLost )");
            else
                Fail("Packet неполон: Hello=" + hasHello + " World=" + hasWorld + " HostLost=" + hasLost);

            // Классы Steam-слоя должны существовать
            if (mod.GetType("MSCMPRecode.MpSteam") != null &&
                mod.GetType("MSCMPRecode.MpSteamHost") != null &&
                mod.GetType("MSCMPRecode.MpSteamClient") != null)
                Pass("Steam-слой: MpSteam/MpSteamHost/MpSteamClient скомпилированы в dll");
            else
                Fail("Steam-слоя нет в dll");
        }
    }
}
