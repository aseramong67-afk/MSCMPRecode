using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

namespace Probe4
{
    // Кто виноват: MpHostServer или среда? В одном процессе:
    //  - мод-хост на 10825 (MpHostServer из DLL)
    //  - vanilla TcpListener на 10826
    // В оба стучится vanilla-клиент. Кто принял — тот жив.
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

            Assembly asm = Assembly.LoadFrom(@"E:\My Summer Car - MP Copy\Mods\MSCMPRecode\MSCMPRecode.dll");
            Type logT = asm.GetType("MSCMPRecode.ModLog");
            Type hostT = asm.GetType("MSCMPRecode.MpHostServer");
            logT.GetField("Sink").SetValue(null, new Action<string>(delegate(string m) { Console.WriteLine("  [log] " + m); }));

            // ---- vanilla хост ----
            bool vanillaAccepted = false;
            TcpListener vanilla = new TcpListener(IPAddress.Any, 10826);
            vanilla.Start();
            vanilla.BeginAcceptTcpClient(delegate(IAsyncResult ar)
            {
                try
                {
                    TcpClient t = vanilla.EndAcceptTcpClient(ar);
                    Console.WriteLine("probe4: VANILLA принял соединение!");
                    vanillaAccepted = true;
                }
                catch (Exception e) { Console.WriteLine("probe4: vanilla accept err " + e.Message); }
            }, vanilla);

            // ---- мод хост ----
            object mhost = Activator.CreateInstance(hostT);
            hostT.GetMethod("Start").Invoke(mhost, new object[] { 10825 });

            Thread.Sleep(1500);

            ConnectAndPoke("vanilla-порт 10826", 10826);
            ConnectAndPoke("мод-порт 10825", 10825);

            Thread.Sleep(1500);
            Console.WriteLine("probe4: vanillaAccepted=" + vanillaAccepted +
                              ", мод ClientCount=" + hostT.GetProperty("ClientCount").GetValue(mhost, null));
        }

        static void ConnectAndPoke(string label, int port)
        {
            try
            {
                TcpClient c = new TcpClient();
                c.Connect(IPAddress.Loopback, port);
                byte[] hello = Encoding.UTF8.GetBytes("PROBE-PING");
                c.GetStream().Write(hello, 0, hello.Length);
                Thread.Sleep(700);
                bool alive = c.Connected;
                c.Close();
                Console.WriteLine("probe4: " + label + " connected=" + alive);
            }
            catch (Exception e)
            {
                Console.WriteLine("probe4: " + label + " сбой: " + e.Message);
            }
        }
    }
}
