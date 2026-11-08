using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Probe3
{
    // Различие: слушатель на Any vs Loopback, и принят ли клиент в обоих случаях
    static class Program
    {
        static void TryCase(string label, IPEndPoint bindEp)
        {
            TcpListener l = new TcpListener(bindEp);
            l.Start();
            bool accepted = false;
            object lockA = new object();

            Thread accepter = new Thread(delegate()
            {
                while (true)
                {
                    try
                    {
                        l.Pending();
                        l.AcceptTcpClient();
                        lock (lockA) { accepted = true; }
                        return;
                    }
                    catch { return; }
                }
            }) { IsBackground = true };
            accepter.Start();
            Thread.Sleep(200);

            string connectTo = "127.0.0.1";
            bool ok = false;
            try
            {
                TcpClient c = new TcpClient();
                c.Connect(connectTo, bindEp.Port);
                ok = true;
                Thread.Sleep(400);
                c.Close();
            }
            catch (Exception e) { Console.WriteLine("[" + label + "] connect ERROR: " + e.Message); }

            Thread.Sleep(200);
            lock (lockA)
            {
                Console.WriteLine("[" + label + "] connect=" + (ok ? "ok" : "FAIL") + ", accepted=" + accepted);
            }
            l.Stop();
            Thread.Sleep(100);
        }

        static void Main()
        {
            TryCase("Loopback-слушатель", new IPEndPoint(IPAddress.Loopback, 10823));
            TryCase("Any-слушатель", new IPEndPoint(IPAddress.Any, 10824));
        }
    }
}
