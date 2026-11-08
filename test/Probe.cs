using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Probe
{
    // Диагностика Pending()/Accept на loopback
    static class Program
    {
        static void Main()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 10820);
            l.Start();
            new Thread(delegate()
            {
                while (true)
                {
                    Console.WriteLine("probe: Pending=" + l.Pending());
                    Thread.Sleep(300);
                }
            }) { IsBackground = true }.Start();

            Thread.Sleep(500);
            new Thread(delegate()
            {
                for (int i = 0; i < 3; i++)
                {
                    TcpClient c = new TcpClient();
                    try
                    {
                        c.Connect(IPAddress.Loopback, 10820);
                        Console.WriteLine("probe: клиент #" + i + " CONNECTED");
                        byte[] send = new byte[40];
                        c.GetStream().Write(send, 0, send.Length);
                        Thread.Sleep(800);
                        string st = c.Client.Connected ? "still-connected" : "broken";
                        Console.WriteLine("probe: клиент #" + i + " после записи: " + st);
                        c.Close();
                    }
                    catch (Exception e) { Console.WriteLine("probe: клиент #" + i + " ОШИБКА: " + e.Message); }
                    Thread.Sleep(500);
                }
            }) { IsBackground = true }.Start();

            Thread.Sleep(4000);
        }
    }
}
