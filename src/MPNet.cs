using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace MSCMPRecode
{
    // ==== Типы пакетов ====
    public enum Packet : byte
    {
        Hello = 1,        // клиент -> хост: (string name, byte clientVer)
        Welcome = 2,      // хост -> клиент: (short pid, string hostName)
        PlayerState = 3,  // оба направления: (short pid, vec3 pos, float rotY, byte flags)
        Chat = 4,         // оба направления: (short pid, string text)
        PlayerLeft = 5,   // хост -> клиенты: (short pid)
        PlayerList = 6,   // хост -> клиент: (short count, [pid, name]*)
        Ping = 7,         // клиент -> хост
        Pong = 8,         // хост -> клиент: (int serverTimeMs)
        SteamInfo = 9,    // клиент -> хост: (string steamNote) - пометка spacewar-режима
        SystemNotice = 10, // локально: системное уведомление клиенту (не по сети)
        Action = 11,      // оба направления: (short pid, byte action, short objectId, float value)
        WorldEvent = 12,  // хост -> все: (kind, objHash, evHash, f1..f8, string text)
        WorldBatch = 13,  // резерв: батч мировых обновлений (v0.3)
        HostLost = 14     // локально: хост закрыл сессию (клиент теряет связь)
    }

    // ==== Типы действий (синхронизация интерактивности) ====
    public static class Actions
    {
        public const byte SitIn = 1;       // сел в машину (objectId = хэш объекта машины)
        public const byte SitOut = 2;      // вышел из машины
        public const byte SmokeOn = 3;     // начал курить
        public const byte SmokeOff = 4;    // закончил
        public const byte DrinkOn = 5;     // пьёт воду
        public const byte DrinkOff = 6;    // перестал
        public const byte Wheel = 7;       // угол руля (value = угол в градусах)
        public const byte WaveHi = 8;      // помахал рукой
    }

    // Коды события состояния игрока
    public static class PlayerFlags
    {
        public const byte InCar = 1;
        public const byte InMenu = 2;
    }

    public struct NetPlayerState
    {
        public short Pid;
        public float X, Y, Z;
        public float RotY;
        public byte Flags;
        public double LastUpdate; // локальное время получения
    }

    // ==== Утилиты бинарной упаковки ====
    public static class NetUtil
    {
        public static void WriteShort(NetworkStream s, short value)
        {
            byte[] b = new byte[2];
            b[0] = (byte)(value & 0xFF);
            b[1] = (byte)((value >> 8) & 0xFF);
            s.Write(b, 0, 2);
        }

        public static int ReadShortExact(NetworkStream s)
        {
            byte[] b = ReadExact(s, 2);
            return (short)(b[0] | (b[1] << 8));
        }

        // Формат: ushort длина тела, затем тело (вызывает payload(ByteWriter))
        public static void SendFrame(NetworkStream s, ushort len, byte[] body)
        {
            // Собираем кадр в один буфер и делаем ОДНУ запись — исключает перемешивание
            // заголовков/тел при записи из разных потоков (плюс внешняя блокировка у владельца).
            byte[] frame = new byte[2 + len];
            frame[0] = (byte)(len & 0xFF);
            frame[1] = (byte)((len >> 8) & 0xFF);
            Array.Copy(body, 0, frame, 2, len);
            s.Write(frame, 0, frame.Length);
            s.Flush();
        }

        // Читает кадр; возвращает тело или null, если соединение разорвано
        public static byte[] ReadFrame(NetworkStream s)
        {
            int b0 = s.ReadByte();
            int b1 = s.ReadByte();
            if (b0 < 0 || b1 < 0) return null;

            int len = (b0 | (b1 << 8)) & 0xFFFF;
            if (len == 0) return new byte[0];
            if (len > 4096) return null; // защита от мусора

            byte[] body = ReadExact(s, len);
            return body;
        }

        // Разбор PlayerState из тела пакета (type byte уже отрезан)
        public static NetPlayerState ParsePlayerState(byte[] rest)
        {
            // формат: [pid:2][x:4][y:4][z:4][rotY:4][flags:1]
            NetPlayerState st = new NetPlayerState();
            int i = 0;
            st.Pid = (short)(rest[i] | (rest[i + 1] << 8)); i += 2;
            st.X = BitConverter.ToSingle(rest, i); i += 4;
            st.Y = BitConverter.ToSingle(rest, i); i += 4;
            st.Z = BitConverter.ToSingle(rest, i); i += 4;
            st.RotY = BitConverter.ToSingle(rest, i); i += 4;
            st.Flags = rest[i];
            return st;
        }

        // Пересборка PlayerState с подменённым pid
        public static byte[] RebuildPlayerState(byte[] rest, short pid)
        {
            NetPlayerState st = ParsePlayerState(rest);
            st.Pid = pid;
            PacketWriter w = new PacketWriter();
            w.PutByte((byte)Packet.PlayerState);
            return WithState(w, st).ToArray();
        }

        // Записывает состояние в уже начатый пакет
        public static PacketWriter WithState(PacketWriter w, NetPlayerState st)
        {
            w.PutShort(st.Pid);
            w.PutFloat(st.X);
            w.PutFloat(st.Y);
            w.PutFloat(st.Z);
            w.PutFloat(st.RotY);
            w.PutByte(st.Flags);
            return w;
        }

        public static byte[] ReadExact(NetworkStream s, int count)
        {
            byte[] buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = s.Read(buf, offset, count - offset);
                if (read <= 0) return null;
                offset += read;
            }
            return buf;
        }
    }

    // ==== Запись в бинарное тело пакета ====
    public class PacketWriter
    {
        private MemoryStream ms = new MemoryStream(128);
        private BinaryWriter bw;

        public PacketWriter()
        {
            bw = new BinaryWriter(ms, Encoding.UTF8);
        }

        public PacketWriter PutByte(byte v) { bw.Write(v); return this; }
        public PacketWriter PutShort(short v) { bw.Write(v); return this; }
        public PacketWriter PutInt(int v) { bw.Write(v); return this; }
        public PacketWriter PutFloat(float v) { bw.Write(v); return this; }
        public PacketWriter PutString(string v)
        {
            if (v == null) v = "";
            byte[] data = Encoding.UTF8.GetBytes(v);
            if (data.Length > 60000) data = new byte[60000]; // урезаем слишком длинное
            bw.Write((ushort)data.Length);
            bw.Write(data);
            return this;
        }

        public int Length { get { return (int)ms.Length; } }
        public byte[] ToArray() { return ms.ToArray(); }
    }

    // ==== Чтение бинарного тела пакета ====
    public class PacketReader
    {
        private MemoryStream ms;
        private BinaryReader br;

        public PacketReader(byte[] data)
        {
            ms = new MemoryStream(data, false);
            br = new BinaryReader(ms, Encoding.UTF8);
        }

        public bool HasMore { get { return ms.Position < ms.Length; } }

        public byte GetByte() { return br.ReadByte(); }
        public short GetShort() { return br.ReadInt16(); }
        public int GetInt() { return br.ReadInt32(); }
        public float GetFloat() { return br.ReadSingle(); }

        public string GetString()
        {
            ushort len = br.ReadUInt16();
            if (len <= 0) return "";
            byte[] buf = br.ReadBytes(len);
            return Encoding.UTF8.GetString(buf);
        }
    }

    // ==== Сообщение для обработки в основном потоке Unity ====
    public struct NetEvent
    {
        public Packet Type;
        public NetPlayerState State;
        public short Pid;
        public string Text;
        public byte Action;
        public short ObjectId;
        public uint ObjHash;   // мировое событие: хеш объекта
        public uint EvHash;    // мировое событие: хеш FSM-события
        public float[] F;      // мировое событие: f1..f8
        public float Value;
        public List<PlayerListEntry> List;
    }

    public struct PlayerListEntry
    {
        public short Pid;
        public string Name;
    }
}
