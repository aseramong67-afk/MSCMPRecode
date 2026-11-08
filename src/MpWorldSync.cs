using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using HutongGames.PlayMaker;

namespace MSCMPRecode
{
    // ==== Мир-синхронизация («синхронизируем всё») ====
    // Схема (по мотивам BeerMP NetGameWorldManager, но на TCP и без Steam):
    //  * хост — единственный источник правды; он замечает изменения
    //    времени суток и погоды и раздаёт их клиентам;
    //  * клиент у себя ОТКЛЮЧАЕТ FSM-стейты загрузки мира ("Load"/"Load game"),
    //    применяет полученные значения и локальных часов не заводит;
    //  * произвольные FSM-события (двери, выключатели, пожар и пр.) передаются
    //    парой (хеш имени объекта, имя события) — каталог расширяет mp scanworld.
    // Все имена кодируются устойчивым FNV-1a-хешем — одинаковым на всех машинах.
    public static class MpWorldSync
    {
        // ==== Типы мировых записей (kind) ====
        public const byte WE_FSM_EVENT = 1;  // событие FSM: (objHash, evHash, text = имя события)
        public const byte WE_OBJ_POS   = 2;  // позиция объекта: (objHash, f1..f3 = x,y,z)
        public const byte WE_OBJ_ARM   = 3;  // резерв (арматура/состояние объекта) — v0.3
        public const byte WS_TIME      = 4;  // время: f1 = день, f2 = час, f3 = минуты
        public const byte WS_WEATHER   = 5;  // погода: f1..f6 = Offset/PosX/PosZ/Rotation/X/Z, f7 = CloudID, f8 = Type

        // Каталог известных событий (для понятных сообщений в чате)
        private static readonly Dictionary<uint, string> knownEvents = new Dictionary<uint, string>();

        public static void RegisterKnown(uint evHash, string label)
        {
            if (!knownEvents.ContainsKey(evHash)) knownEvents[evHash] = label;
        }

        public static string DescribeEvent(uint evHash)
        {
            string s;
            if (knownEvents.TryGetValue(evHash, out s)) return s;
            return "unknown#" + evHash.ToString("X8");
        }

        // ==== Стабильный хеш имени: FNV-1a (32 бита), одинаковый на всех компах ====
        public static uint Hash(string s)
        {
            unchecked
            {
                uint h = 2166136261u;
                if (s != null)
                    for (int i = 0; i < s.Length; i++)
                        h = (h ^ (uint)s[i]) * 16777619u;
                return h;
            }
        }

        public static short ShortId(string s)
        {
            return (short)(Hash(s) & 0x7FFF);
        }

        // ==== Локальные репорты (вызываются хостом из своего кода) ====
        public static void ReportFsmEvent(string objName, string eventName, float value)
        {
            MpManager mgr = MpManager.Instance;
            if (mgr == null) return;
            mgr.WorldReport(WE_FSM_EVENT, Hash(objName), Hash(eventName), new float[] { value }, eventName);
        }

        public static void ReportPosition(string objName, Transform t)
        {
            if (t == null) return;
            MpManager mgr = MpManager.Instance;
            if (mgr == null) return;
            mgr.WorldReportPos(objName, t);
        }

        // ==== Привязка FSM мира (SUN/Clouds) ====
        private static PlayMakerFSM sunFsm;      // "MAP/SUN/Pivot/SUN", FSM "Color"
        private static PlayMakerFSM weatherFsm;  // "MAP/CloudSystem/Clouds", FSM "Weather"

        private static FsmInt   gDay;        // глобальная переменная GlobalDay
        private static FsmInt   sunHour;     // переменная "Time" у SUN
        private static FsmFloat sunMinutes;  // переменная "Minutes" у SUN

        private static FsmFloat ofs, posX, posZ, rot, wx, wz; // погодные флоаты
        private static FsmInt   cloudId, weatherType;         // WeatherCloudID / WeatherType
        private static FsmEvent mpUpdateWeatherEv;            // событие MP_UpdateWeather

        private static double bindRetryAt;
        private static bool weatherEventReady;
        private static string lastBindLog;

        // Привязка (после загрузки сцены). Повторяем, пока не нашли оба FSM.
        private static void Bind()
        {
            if (sunFsm != null && weatherFsm != null)
            {
                TryApplyPending();
                return;
            }
            if (Time.realtimeSinceStartup < bindRetryAt) return;
            bindRetryAt = Time.realtimeSinceStartup + 5.0;

            try
            {
                if (sunFsm == null)
                {
                    GameObject sun = GameObject.Find("MAP/SUN/Pivot/SUN");
                    if (sun != null)
                    {
                        sunFsm = sun.GetPm("Color");
                        if (sunFsm != null)
                        {
                            gDay = FsmVariables.GlobalVariables.FindFsmInt("GlobalDay");
                            sunHour = sunFsm.FsmVariables.FindFsmInt("Time");
                            sunMinutes = sunFsm.FsmVariables.FindFsmFloat("Minutes");
                        }
                    }
                }
                if (weatherFsm == null)
                {
                    GameObject cl = GameObject.Find("MAP/CloudSystem/Clouds");
                    if (cl != null)
                    {
                        PlayMakerFSM w2 = cl.GetPm("Weather");
                        if (w2 != null)
                        {
                            // Перепривязка (новая сцена/сессия): сбрасываем FSM-событие погоды
                            weatherFsm = w2;
                            weatherEventReady = false;
                            mpUpdateWeatherEv = null;
                            ofs = weatherFsm.FsmVariables.FindFsmFloat("Offset");
                            posX = weatherFsm.FsmVariables.FindFsmFloat("PosX");
                            posZ = weatherFsm.FsmVariables.FindFsmFloat("PosZ");
                            rot = weatherFsm.FsmVariables.FindFsmFloat("Rotation");
                            wx = weatherFsm.FsmVariables.FindFsmFloat("X");
                            wz = weatherFsm.FsmVariables.FindFsmFloat("Z");
                            cloudId = weatherFsm.FsmVariables.FindFsmInt("WeatherCloudID");
                            weatherType = weatherFsm.FsmVariables.FindFsmInt("WeatherType");
                        }
                    }
                }
                if ((sunFsm != null || weatherFsm != null))
                {
                    string st = (sunFsm != null ? "SUN" : "") + (weatherFsm != null ? " Weather" : "");
                    if (st != lastBindLog)
                    {
                        ModLog.Info("Мир-синк привязан: " + st.Trim());
                        lastBindLog = st;
                    }
                }
                TryApplyPending();
            }
            catch (Exception e)
            {
                ModLog.Error("Мир-синк bind: " + e.Message);
            }
        }

        // Возмещение значений, приходивших раньше, чем FSM мира был найден (rev: S6)
        private static float[] pendingTime;
        private static float[] pendingWeather;
        private static bool pendingApplying;

        private static void TryApplyPending()
        {
            if (pendingApplying) return;
            pendingApplying = true;
            try
            {
                if (pendingTime != null && sunFsm != null)
                {
                    float[] pt = pendingTime;
                    pendingTime = null;
                    ApplyTime(pt);
                }
                if (pendingWeather != null && weatherFsm != null)
                {
                    float[] pw = pendingWeather;
                    pendingWeather = null;
                    ApplyWeather(pw);
                }
            }
            catch (Exception e)
            {
                ModLog.Error("Мир-синк pending: " + e.Message);
            }
            finally
            {
                pendingApplying = false;
            }
        }

        // ==== Готовность игрока (вызывается, когда найден Fsy) ====
        public static void PlayerReady(int mode)
        {
            Bind();
            if (mode == 2) ClientDisableLocalWorldControl();
        }

        // Клиент: вернуть FSM-загрузку мира (после выхода из сессии)
        public static void RestoreClient()
        {
            ClientEnableLocalWorldControl();
        }

        // Клиент: убираем собственную загрузку времени/погоды — слушаем хоста
        private static void ClientDisableLocalWorldControl()
        {
            try
            {
                if (sunFsm != null) ToggleStateActions(sunFsm, "Load", false);
                if (weatherFsm != null) ToggleStateActions(weatherFsm, "Load game", false);
                EnsureWeatherEvent();
                ModLog.Info("Мир-синк: локальное время/погода ОТКЛЮЧЕНЫ (клиент).");
            }
            catch (Exception e)
            {
                ModLog.Error("Мир-синк client-off: " + e.Message);
            }
        }

        private static void ClientEnableLocalWorldControl()
        {
            try
            {
                if (sunFsm != null) ToggleStateActions(sunFsm, "Load", true);
                if (weatherFsm != null) ToggleStateActions(weatherFsm, "Load game", true);
                ModLog.Info("Мир-синк: локальное время/погода восстановлены.");
            }
            catch (Exception e)
            {
                ModLog.Error("Мир-синк client-on: " + e.Message);
            }
        }

        private static void ToggleStateActions(PlayMakerFSM pm, string stateName, bool enabled)
        {
            if (pm == null || pm.Fsm == null || pm.FsmStates == null) return;
            FsmState st = pm.GetPmState(stateName);
            if (st == null || st.Actions == null) return;
            for (int i = 0; i < st.Actions.Length; i++)
                if (st.Actions[i] != null) st.Actions[i].Enabled = enabled;
        }

        private static void EnsureWeatherEvent()
        {
            if (weatherEventReady || weatherFsm == null) return;
            mpUpdateWeatherEv = weatherFsm.AddPmEvent("MP_UpdateWeather");
            weatherFsm.AddGlobalTransitionPm(mpUpdateWeatherEv, "Set cloud");
            weatherEventReady = true;
        }

        // ==== Хост-поллинг: замечаем изменения и раздаём ====
        private static double nextTimePoll;
        private static double nextWeatherPoll;

        private static int lastDay = int.MinValue, lastHour = int.MinValue;
        private static float lastMinutes = float.MinValue;
        private static float lastRot = float.MinValue, lastPosX = float.MinValue,
                             lastPosZ = float.MinValue, lastOfs = float.MinValue,
                             lastWx = float.MinValue, lastWz = float.MinValue;
        private static int lastCloud = int.MinValue, lastWType = int.MinValue;

        // Вызывается из MpManager.Tick на хосте
        public static void HostPoll()
        {
            Bind();
            RefreshSnapshotCache();
            double now = Time.realtimeSinceStartup;

            if (sunFsm != null && now >= nextTimePoll)
            {
                nextTimePoll = now + 2.0;
                int d = gDay != null ? gDay.Value : 0;
                int h = sunHour != null ? sunHour.Value : 0;
                float m = sunMinutes != null ? sunMinutes.Value : 0f;
                if (d != lastDay || h != lastHour || Mathf.Abs(m - lastMinutes) > 0.01f)
                {
                    lastDay = d; lastHour = h; lastMinutes = m;
                    MpManager mgr = MpManager.Instance;
                    if (mgr != null) mgr.WorldReport(WS_TIME, 0, 0,
                        new float[] { d, h, m }, "");
                }
            }

            if (weatherFsm != null && now >= nextWeatherPoll)
            {
                nextWeatherPoll = now + 4.0;
                float ro = rot != null ? rot.Value : 0f;
                float px = posX != null ? posX.Value : 0f;
                float pz = posZ != null ? posZ.Value : 0f;
                float of = ofs != null ? ofs.Value : 0f;
                float xx = wx != null ? wx.Value : 0f;
                float zz = wz != null ? wz.Value : 0f;
                int cid = cloudId != null ? cloudId.Value : 0;
                int wty = weatherType != null ? weatherType.Value : 0;
                if (Mathf.Abs(ro - lastRot) > 0.01f || Mathf.Abs(px - lastPosX) > 0.01f ||
                    Mathf.Abs(pz - lastPosZ) > 0.01f || Mathf.Abs(of - lastOfs) > 0.01f ||
                    Mathf.Abs(xx - lastWx) > 0.01f || Mathf.Abs(zz - lastWz) > 0.01f ||
                    cid != lastCloud || wty != lastWType)
                {
                    lastRot = ro; lastPosX = px; lastPosZ = pz; lastOfs = of;
                    lastWx = xx; lastWz = zz; lastCloud = cid; lastWType = wty;
                    MpManager mgr = MpManager.Instance;
                    if (mgr != null) mgr.WorldReport(WS_WEATHER, 0, 0,
                        new float[] { of, px, pz, ro, xx, zz, cid, wty }, "");
                }
            }
        }

        // ==== Снапшот при подключении нового клиента ====
        // Кадры собираются на главном потоке (HostPoll) — из фонового потока
        // вызывать Unity API (GameObject.Find и пр.) нельзя.
        private static byte[] cachedTimeFrame;
        private static byte[] cachedWeatherFrame;

        public static byte[] SnapshotTimeFrame()
        {
            return cachedTimeFrame;
        }

        public static byte[] SnapshotWeatherFrame()
        {
            return cachedWeatherFrame;
        }

        // Обновление кэша снапшота (вызывается с главного потока)
        private static void RefreshSnapshotCache()
        {
            if (sunFsm != null)
            {
                int d = gDay != null ? gDay.Value : 0;
                int h = sunHour != null ? sunHour.Value : 0;
                float m = sunMinutes != null ? sunMinutes.Value : 0f;
                cachedTimeFrame = WorldFrame(WS_TIME, 0, 0, new float[] { d, h, m }, "");
            }
            if (weatherFsm != null)
            {
                cachedWeatherFrame = WorldFrame(WS_WEATHER, 0, 0, new float[]
                {
                    ofs != null ? ofs.Value : 0f,
                    posX != null ? posX.Value : 0f,
                    posZ != null ? posZ.Value : 0f,
                    rot != null ? rot.Value : 0f,
                    wx != null ? wx.Value : 0f,
                    wz != null ? wz.Value : 0f,
                    cloudId != null ? cloudId.Value : 0,
                    weatherType != null ? weatherType.Value : 0
                }, "");
            }
        }

        // Сборка кадра Packet.WorldEvent: [kind:1][objHash:4][evHash:4][f1..f8:32][string]
        public static byte[] WorldFrame(byte kind, uint objHash, uint evHash, float[] f, string text)
        {
            PacketWriter w = new PacketWriter();
            w.PutByte((byte)Packet.WorldEvent);
            w.PutByte(kind);
            w.PutInt((int)objHash);
            w.PutInt((int)evHash);
            for (int i = 0; i < 8; i++)
                w.PutFloat(f != null && i < f.Length ? f[i] : 0f);
            // Строку режем — лимит кадра 4096 байт (rev: S1)
            if (text != null && text.Length > 300) text = text.Substring(0, 300);
            w.PutString(text == null ? "" : text);
            return w.ToArray();
        }

        // ==== Применение мировых записей у клиента ====
        public static void Apply(byte kind, uint objHash, uint evHash, float[] f, string text)
        {
            switch (kind)
            {
                case WE_FSM_EVENT:
                    ApplyFsmEvent(objHash, text);
                    break;

                case WE_OBJ_POS:
                    ApplyObjectPosition(objHash, f);
                    break;

                case WS_TIME:
                    ApplyTime(f);
                    break;

                case WS_WEATHER:
                    ApplyWeather(f);
                    break;
            }
        }

        // Пробегаем все FSM объекта и шлём событие, если оно там существует
        private static void ApplyFsmEvent(uint objHash, string evName)
        {
            if (string.IsNullOrEmpty(evName)) return;
            GameObject go = ResolveObject(objHash);
            if (go == null) return;

            PlayMakerFSM[] list = go.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < list.Length; i++)
            {
                PlayMakerFSM pm = list[i];
                if (pm == null || pm.Fsm == null || pm.FsmEvents == null) continue;
                for (int j = 0; j < pm.FsmEvents.Length; j++)
                {
                    FsmEvent fe = pm.FsmEvents[j];
                    if (fe != null && fe.Name == evName)
                    {
                        pm.SendEvent(evName);
                        break;
                    }
                }
            }
        }

        private static void ApplyObjectPosition(uint objHash, float[] f)
        {
            if (f == null || f.Length < 3) return;
            GameObject go = ResolveObject(objHash);
            if (go == null) return;
            go.transform.position = new Vector3(f[0], f[1], f[2]);
        }

        // Время: как BeerMP — day/time/minutes + WAKEUP солнцу
        private static void ApplyTime(float[] f)
        {
            if (f == null || f.Length < 3) return;
            Bind();
            if (sunFsm == null)
            {
                pendingTime = f; // мир ещё не готов: применим при перепривязке (rev: S6)
                return;
            }
            pendingTime = null;
            try
            {
                if (gDay != null) gDay.Value = (int)f[0];
                if (sunHour != null) sunHour.Value = (int)f[1];
                if (sunMinutes != null) sunMinutes.Value = f[2];
                sunFsm.SendEvent("WAKEUP");
            }
            catch (Exception e)
            {
                ModLog.Error("Мир-синк время: " + e.Message);
            }
        }

        // Погода: как BeerMP — событие, потом переменные и поворот облачной системы
        private static void ApplyWeather(float[] f)
        {
            if (f == null || f.Length < 8) return;
            Bind();
            if (weatherFsm == null)
            {
                pendingWeather = f; // мир ещё не готов: применим при перепривязке (rev: S6)
                return;
            }
            pendingWeather = null;
            try
            {
                EnsureWeatherEvent();
                weatherFsm.Fsm.Event(mpUpdateWeatherEv);
                if (cloudId != null) cloudId.Value = (int)f[6];
                if (weatherType != null) weatherType.Value = (int)f[7];
                if (ofs != null) ofs.Value = f[0];
                if (posX != null) posX.Value = f[1];
                if (posZ != null) posZ.Value = f[2];
                if (rot != null) rot.Value = f[3];
                weatherFsm.transform.eulerAngles = Vector3.up * f[3];
                if (wx != null) wx.Value = f[4];
                if (wz != null) wz.Value = f[5];
            }
            catch (Exception e)
            {
                ModLog.Error("Мир-синк погода: " + e.Message);
            }
        }

        // ==== Поиск GameObject по хешу имени (кэш попаданий, пауза промахов) ====
        private static readonly Dictionary<uint, GameObject> objCache = new Dictionary<uint, GameObject>();
        private static readonly Dictionary<uint, double> missAt = new Dictionary<uint, double>();
        private static double nextCacheFlush;
        private const float missRetrySec = 10f; // вторая попытка промахнувшегося хеша

        private static GameObject ResolveObject(uint hash)
        {
            GameObject g;
            if (objCache.TryGetValue(hash, out g) && g != null) return g;

            double now = Time.realtimeSinceStartup;
            double m;
            if (missAt.TryGetValue(hash, out m) && now - m < missRetrySec) return null;

            if (now - nextCacheFlush > 5.0)
            {
                nextCacheFlush = now;
                objCache.Clear();
            }

            GameObject[] all = GameObject.FindObjectsOfType(typeof(GameObject)) as GameObject[];
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && Hash(all[i].name) == hash)
                {
                    objCache[hash] = all[i];
                    missAt.Remove(hash);
                    return all[i];
                }
            }
            missAt[hash] = now; // запоминаем промах (rev: S5) — не сканируем опять каждое событие
            return null;
        }
    }
}
