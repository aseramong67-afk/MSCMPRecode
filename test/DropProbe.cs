// Зонд: сколько времени хост замечает обрыв клиента (реальные классы DLL)
using System;
using System.Reflection;
using System.Threading;

namespace DropProbe
{
    static class Program
    {
        static void Main()
        {
            string managedDir = @"E:\My Summer Car - MP Copy\mysummercar_Data\Managed";
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object s2, ResolveEventArgs e2)
            {
                string n = (e2.Name.Split(',')[0]).Trim() + ".dll";
                string p = System.IO.Path.Combine(managedDir, n);
                return System.IO.File.Exists(p) ? Assembly.LoadFrom(p) : null;
            };

            Assembly asm = Assembly.LoadFrom(@"E:\My Summer Car - MP Copy\Mods\MSCMPRecode\MSCMPRecode.dll");
            Type logType = asm.GetType("MSCMPRecode.ModLog");
            logType.GetField("Sink").SetValue(null, new Action<string>(delegate(string m)
            {
                Console.WriteLine("  [" + DateTime.UtcNow.TimeOfDay.TotalMilliseconds.ToString("F0") + "ms] " + m);
            }));

            Type hostType = asm.GetType("MSCMPRecode.MpHostServer");
            Type clientType = asm.GetType("MSCMPRecode.MpTunnelClient");

            double t0 = DateTime.UtcNow.TimeOfDay.TotalMilliseconds;

            Console.WriteLine("-- start host @ " + (DateTime.UtcNow.TimeOfDay.TotalMilliseconds - t0).ToString("F0"));
            object host = Activator.CreateInstance(hostType);
            hostType.GetMethod("Start").Invoke(host, new object[] { 10829 });

            object cli = Activator.CreateInstance(clientType);
            Console.WriteLine("-- connect @ " + (DateTime.UtcNow.TimeOfDay.TotalMilliseconds - t0).ToString("F0"));
            clientType.GetMethod("Connect").Invoke(cli, new object[] { "127.0.0.1", 10829 });
            Thread.Sleep(1000);

            FieldInfo pidF = clientType.GetField("MyPid");
            short pid = (short)pidF.GetValue(cli);
            Console.WriteLine("-- pid=" + pid);

            double dt = DateTime.UtcNow.TimeOfDay.TotalMilliseconds;
            Console.WriteLine("-- disconnect @ +0ms");
            clientType.GetMethod("Disconnect").Invoke(cli, null);

            for (int i = 0; i < 60; i++)
            {
                int n = (int)hostType.GetProperty("ClientCount").GetValue(host, null);
                if (n == 0)
                {
                    Console.WriteLine("-- HOST NOTICED after " + (DateTime.UtcNow.TimeOfDay.TotalMilliseconds - dt).ToString("F0") + "ms");
                    return;
                }
                Thread.Sleep(100);
            }
            Console.WriteLine("-- HOST NEVER NOTICED (30 сек+)");

            if (logType != null) { };
        }
    }
}
