using System;
using System.Collections.Generic;
using MSCLoader;

namespace MSCMPRecode
{
    // Единое логирование через консоль MSCLoader.
    // С2 (ревью): вызовы из фоновых потоков не должны осаждаться в ModConsole напрямую —
    // включаем режим очереди, разбор в главном потоке через Drain() из MpManager.Tick.
    public static class ModLog
    {
        private const string Prefix = "[MPRecode] ";

        // Подключаемый приёмник логов (для headless-тестов используется вместо MSCLoader)
        public static Action<string> Sink;
        private static readonly Queue<string> pending = new Queue<string>();
        private static readonly object pendingLock = new object();
        private static bool queueMode;

        public static void EnableQueueMode()
        {
            queueMode = true;
        }

        private static void Emit(string msg)
        {
            if (queueMode)
            {
                lock (pendingLock) { pending.Enqueue(msg); }
                return;
            }

            if (Sink != null)
            {
                try { Sink(msg); return; }
                catch { }
            }
            ModConsole.Print(msg);
        }

        public static void Drain()
        {
            try
            {
                while (true)
                {
                    string msg;
                    lock (pendingLock)
                    {
                        if (pending.Count == 0) return;
                        msg = pending.Dequeue();
                    }

                    if (msg.StartsWith("!W ")) { ModConsole.Warning(msg.Substring(3)); continue; }
                    if (msg.StartsWith("!E ")) { ModConsole.Error(msg.Substring(3)); continue; }
                    ModConsole.Print(msg);
                }
            }
            catch { }
        }

        public static void Info(string msg)
        {
            Emit(Prefix + msg);
        }

        public static void Warn(string msg)
        {
            Emit("!W " + Prefix + msg);
        }

        public static void Error(string msg)
        {
            Emit("!E " + Prefix + msg);
        }
    }
}
