using System;
using System;
using System.Reflection;
using System.Text;
using System.Threading;

namespace Probe2
{
    static class Program
    {
        static void Main()
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            string managedDir = @"E:\My Summer Car - MP Copy\mysummercar_Data\Managed";
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object s2, ResolveEventArgs e2)
            {
                string n = (e2.Name.Split(',')[0]).Trim() + ".dll";
                string p = System.IO.Path.Combine(managedDir, n);
                if (System.IO.File.Exists(p)) return Assembly.LoadFrom(p);
                return null;
            };

            Type logT = null, hostT = null;
            try
            {
                Assembly asm = Assembly.LoadFrom(@"E:\My Summer Car - MP Copy\Mods\MSCMPRecode\MSCMPRecode.dll");
                logT = asm.GetType("MSCMPRecode.ModLog");
                hostT = asm.GetType("MSCMPRecode.MpHostServer");
            }
            catch (Exception e) { Console.WriteLine("probe2: " + e.Message); return; }

            logT.GetField("Sink").SetValue(null, new Action<string>(delegate(string m) { Console.WriteLine("  [log] " + m); }));

            object host = Activator.CreateInstance(hostT);
            hostT.GetMethod("Start").Invoke(host, new object[] { 10821 });
            Thread.Sleep(300);

            for (int i = 0; i < 2; i++)
            {
                System.Net.Sockets.TcpClient c = new System.Net.Sockets.TcpClient();
                try
                {
                    c.Connect("127.0.0.1", 10821);
                    Console.WriteLine("probe2: клиент LINKED");
                    // Hello: тип 1, длина строки, строка
                    System.IO.MemoryStream ms = new System.IO.MemoryStream();
                    System.IO.BinaryWriter w = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8);
                    w.Write((byte)1);
                    byte[] name = Encoding.UTF8.GetBytes("Пробный-бот");
                    w.Write((ushort)name.Length);
                    w.Write(name);
                    byte[] body = ms.ToArray();
                    ms = new System.IO.MemoryStream();
                    w = new System.IO.BinaryWriter(ms);
                    w.Write((ushort)body.Length);
                    w.Write(body);
                    byte[] frame = ms.ToArray();
                    c.GetStream().Write(frame, 0, frame.Length);
                    Console.WriteLine("probe2: Hello отправлен (" + frame.Length + " байт)");
                    Thread.Sleep(1000);
                    // Читаем Welcome
                    try
                    {
                        System.Net.Sockets.NetworkStream s = c.GetStream();
                        int h0 = s.ReadByte(), h1 = s.ReadByte();
                        if (h0 >= 0 && h1 >= 0)
                        {
                            int len = h0 | (h1 << 8);
                            byte[] buf = new byte[len];
                            int off = 0;
                            while (off < len) off += s.Read(buf, off, len - off);
                            Console.WriteLine("probe2: ответ хоста " + len + " байт, тип=" + buf[0]);
                        }
                        else Console.WriteLine("probe2: хост закрыл слот");
                    }
                    catch (Exception e2) { Console.WriteLine("probe2: чтение сломано: " + e2.Message); }
                    c.Close();
                }
                catch (Exception e) { Console.WriteLine("probe2: подключение не удалось: " + e.Message); }
                Thread.Sleep(500);
            }
            Thread.Sleep(1000);
        }
    }
}
