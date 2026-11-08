using System;
using System.Threading;
using UnityEngine;

namespace MSCMPRecode
{
    // ==== Сетевой бот-агент ====
    // Подключается к хосту как настоящий клиент через 127.0.0.1 (loopback),
    // прошли весь цикл: TCP -> Hello -> Welcome(pid) -> PlayerState broadcast -> Chat.
    // Двигается по кругу вокруг позиции хоста, шлёт чат раз в ~7 сек.
    public class MpTestBot : IDisposable
    {
        private readonly MpTunnelClient client;
        private readonly string botName;
        private Thread worker;
        private volatile bool alive;
        private Vector3 center;
        private int statesSent;

        private MpTestBot(MpTunnelClient c, string name, Vector3 basePos)
        {
            client = c;
            botName = name;
            center = basePos;
        }

        public static MpTestBot Spawn(string host, int port, string name)
        {
            MpTunnelClient c = new MpTunnelClient();
            c.Connect(host, port);

            MpTestBot bot = new MpTestBot(c, name, GetWorldAnchor());
            bot.alive = true;
            bot.worker = new Thread(bot.Run);
            bot.worker.IsBackground = true;
            bot.worker.Start();
            ModLog.Info("Бот-агент " + name + " запущен.");
            return bot;
        }

        private static Vector3 GetWorldAnchor()
        {
            // Якорь вокруг позиции локального игрока, чтобы мониторить видимость
            try
            {
                GameObject p = GameObject.Find("Fsy");
                if (p != null) return p.transform.position;
            }
            catch { }
            return Vector3.zero;
        }

        private void Run()
        {
            // Дождаться Welcome(pid)
            int guard = 0;
            while (client.MyPid < 0 && guard++ < 60)
                Thread.Sleep(250);

            double lastChat = 0;
            double lastAction = 0;
            double next = Now();
            bool smokeOn = false;
            bool drinkOn = false;

            while (alive && client.ConnectRequested)
            {
                if (!client.Connected)
                {
                    Thread.Sleep(500);
                    continue;
                }

                if (Now() >= next)
                {
                    next = Now() + (1000.0 / 20.0); // 20 Гц как в реальном тикрейте
                    double t = Now() / 1000.0;
                    float x = center.x + Mathf.Cos((float)t) * 6f;
                    float z = center.z + Mathf.Sin((float)t) * 6f;
                    float y = center.y;
                    float rotY = ((float)t * Mathf.Rad2Deg) - 90f;

                    client.SendState(client.MyPid, x, y, z, rotY, 0);
                    client.SendAction(Actions.Wheel, 0, Mathf.Sin((float)t) * 120f); // встрёп руля
                    statesSent++;

                    if (Now() - lastAction > 9000 && client.MyPid >= 0)
                    {
                        lastAction = Now();
                        smokeOn = !smokeOn;
                        drinkOn = !smokeOn;
                        client.SendAction(smokeOn ? Actions.SmokeOn : Actions.SmokeOff, 0, 0f);
                        client.SendAction(drinkOn ? Actions.DrinkOn : Actions.DrinkOff, 0, 0f);
                        // слева "в машину"/"выход" — тест обоих состояний
                        client.SendAction((statesSent / 100) % 2 == 0 ? Actions.SitIn : Actions.SitOut, 7, 0f);
                    }

                    if (Now() - lastChat > 7000 && client.MyPid >= 0)
                    {
                        lastChat = Now();
                        client.SendChat(botName + ": пакетов " + statesSent + ", pid=" + client.MyPid);
                    }
                }
                Thread.Sleep(10);
            }
        }

        public void Shutdown()
        {
            alive = false;
            client.Disconnect();
            ModLog.Info("Бот-агент " + botName + " остановлен (пакетов отправлено: " + statesSent + ").");
        }

        public void Dispose()
        {
            Shutdown();
        }

        private static double Now()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
        }
    }
}
