#region References
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
#endregion

namespace Server.Network
{
    public struct PendingPacket
    {
        public NetState State;
        public byte[] Buffer;
        public int Length;
        public PacketHandler Handler;
    }

    public class MessagePump
    {
        private Listener[] m_Listeners;
        private ConcurrentQueue<PendingPacket> m_ToProcess = new ConcurrentQueue<PendingPacket>();
        private ConcurrentQueue<NetState> m_Queue;
        private Queue<NetState> m_WorkingQueue;
        private Thread m_Original;

        public MessagePump(Thread original)
        {
            m_Original = original;
            System.Net.IPEndPoint[] ipep = Listener.EndPoints;

            m_Listeners = new Listener[ipep.Length];

            bool success = false;
            m_Queue = new ConcurrentQueue<NetState>();
            m_WorkingQueue = new Queue<NetState>();

            do
            {
                for (int i = 0; i < ipep.Length; ++i)
                {
                    Listener l = new Listener(ipep[i]);
                    if (!success && l != null)
                    {
                        success = true;
                    }
                    m_Listeners[i] = l;
                }

                if (!success)
                {
                    Console.WriteLine("Retrying...");
                    Thread.Sleep(10000);
                }
            }
            while (!success);
        }

        public Listener[] Listeners { get => m_Listeners; set => m_Listeners = value; }

        public void AddListener(Listener l)
        {
            Listener[] old = m_Listeners;

            m_Listeners = new Listener[old.Length + 1];

            for (int i = 0; i < old.Length; ++i)
            {
                m_Listeners[i] = old[i];
            }

            m_Listeners[old.Length] = l;
        }

        private void CheckListener()
        {
            for (int j = 0; j < m_Listeners.Length; ++j)
            {
                System.Net.Sockets.Socket[] accepted = m_Listeners[j].Slice();

                for (int i = 0; i < accepted.Length; ++i)
                {
                    NetState ns = new NetState(accepted[i], this);
                    ns.Start();
                    string str = ns.ToString();

                    if (ns.Running && str != NetState.SitoWeb)
                    {
                        Console.WriteLine("Client: {0}: Connected. [{1} Online]", str, NetState.Instances.Count);
                    }
                }
            }
        }

        public void OnReceive(NetState ns)
        {
            // Questo viene chiamato dal thread di rete (Receive_Process)
            // Dobbiamo estrarre i pacchetti completi qui per liberare il ByteQueue

            ByteQueue buffer = ns.Buffer;
            lock (buffer)
            {
                while (ns.Running && buffer.Length > 0)
                {
                    // ... logica Seed e identificazione PacketID/Handler ...
                    if (!ns.Seeded)
                    {
                        if (!TryHandleSeed(ns, buffer)) break;
                    }

                    byte packetID = buffer.GetPacketID();
                    PacketHandler handler = ns.GetHandler(packetID);

                    if (handler == null) 
                    {
                        break; 
                    }

                    int packetLength = handler.Length > 0 ? handler.Length : buffer.GetPacketLength();
                    if (buffer.Length < packetLength) break;

                    // AFFITTIAMO QUI
                    byte[] rented = ArrayPool<byte>.Shared.Rent(packetLength);
                    buffer.Dequeue(rented.AsSpan(0, packetLength));

                    Interlocked.Increment(ref ns.PacketsInQueue);
                    // PASSAGGIO DI PROPRIETÀ: lo mettiamo in coda
                    m_ToProcess.Enqueue(new PendingPacket
                    {
                        State = ns,
                        Buffer = rented,
                        Length = packetLength,
                        Handler = handler
                    });
                }
                Core.Set();
            }
        }

        public void Slice()
        {
            // Questo gira sul MAIN THREAD (Thread Original)
            while (m_ToProcess.TryDequeue(out PendingPacket pkt))
            {
                try
                {
                    if (pkt.State != null && pkt.State.Running && pkt.Handler != null)
                    {
                        ReadOnlySpan<byte> data = pkt.Buffer.AsSpan(0, pkt.Length);
                        PacketReader r = new PacketReader(ref data, pkt.Length, pkt.Handler.Length != 0);

                        // Qui sei al sicuro: il World è bloccato su questo thread
                        pkt.Handler.OnReceive(pkt.State, ref r);
                        Interlocked.Decrement(ref pkt.State.PacketsInQueue);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(pkt.Buffer);
                    int remaining = Interlocked.Decrement(ref pkt.State.PacketsInQueue);
                    if (remaining <= 5) // Soglia di "fame" di dati
                    {
                        pkt.State.CheckThrottling();
                    }
                }
            }
        }

        private static AutoResetEvent _Signal = new AutoResetEvent(true);
        private void Thread_Slice()
        {
            while (!Core.Closing)
            {
                _Signal.WaitOne(); // Svegliato da OnReceive o Listener

                // 1. Accetta nuove connessioni
                CheckListener();

                // 2. Processa i NetState che hanno ricevuto dati
                while (m_Queue.TryDequeue(out NetState ns))
                {
                    if (ns != null && ns.Running)
                    {
                        // Estrae i pacchetti dal ByteQueue e li mette in m_ToProcess
                        // Questo svuota il buffer di ricezione il prima possibile
                        OnReceive(ns);
                    }
                }
            }
        }

        private const int BufferSize = 4096;
        private BufferPool m_Buffers = new BufferPool("Processor", 4, BufferSize);

        private bool TryHandleSeed(NetState ns, ByteQueue buffer)
        {
            if (buffer.Length <= 0) return false; // Non abbiamo nemmeno 1 byte

            byte id = buffer.GetPacketID();

            if (id == 0xEF || id == 0x0C)
            {
                // IMPORTANTE: Non rimuovere l'ID qui. 
                // Lasciamo che il loop HandleReceive lo tratti come un pacchetto normale 
                // tramite il suo handler (es. LoginServerSeed).
                ns.Seeded = true;
                return true;
            }
            else if (buffer.Length >= 4)
            {
                // Seed tradizionale a 4 byte (es: client 5.x o precedenti)
                Span<byte> span = stackalloc byte[4];
                buffer.Dequeue(span);

                // BinaryPrimitives è perfetto qui
                if (BinaryPrimitives.TryReadInt32BigEndian(span, out int seed))
                {

                    if (seed == 0)
                    {
                        Console.WriteLine("Login: {0}: Invalid client detected", ns);
                        ns.Dispose();
                        return false;
                    }

                    ns.m_Seed = seed;
                    ns.Seeded = true;
                    return true;
                }
            }

            return false;
        }

        private bool CheckEncrypted(NetState ns, byte packetID)
        {
            if (!ns.SentFirstPacket && packetID != 0xF0 && packetID != 0xF1 && packetID != 0xCF && packetID != 0x80 && packetID != 0x81 && packetID != 0x91 && packetID != 0x92 && packetID != 0xA4 && packetID != 0xEF && packetID != 0x0C)
            {
                Console.WriteLine($"Client: {ns}: Encrypted client detected, disconnecting (packet id received was {packetID})");
                PacketHandlers.AccountLogin_ReplyRej(ns, ALRReason.BadComm);
                //ns.Dispose();
                return true;
            }
            return false;
        }

        private void TraceUnknownPacket(NetState ns, ByteQueue buffer) 
        {
            int traceLen = Math.Min(buffer.Length, 256);
            byte[] packetBuffer = ArrayPool<byte>.Shared.Rent(traceLen);
            try
            {
                Span<byte> traceSpan = packetBuffer.AsSpan(0, traceLen);
                int actualRead = buffer.Dequeue(traceSpan);
                ReadOnlySpan<byte> ros = traceSpan.Slice(0, actualRead);
                new PacketReader(ref ros, actualRead, false).Trace(ns);
            }
            finally { ArrayPool<byte>.Shared.Return(packetBuffer); }
        }

        public void HandleReceive(NetState ns)
        {
            ByteQueue buffer = ns.Buffer;
            if (buffer == null) return;

            while (ns.Running)
            {
                PacketHandler handler = null;
                int packetLength = 0;
                byte[] rented = null;

                lock (buffer) // Lock minimo solo per ispezionare l'header
                {
                    if (buffer.Length <= 0) break;

                    if (!ns.Seeded)
                    {
                        if (!TryHandleSeed(ns, buffer)) break;
                        if (buffer.Length <= 0) break;
                    }

                    byte packetID = buffer.GetPacketID();
                    handler = ns.GetHandler(packetID);

                    if (handler == null) { /* Dispose... */ break; }

                    packetLength = handler.Length > 0 ? handler.Length : buffer.GetPacketLength();

                    if (packetLength <= 0 || buffer.Length < packetLength)
                        break; // Dati insufficienti, attendiamo la prossima receive

                    // Estrazione atomica
                    rented = ArrayPool<byte>.Shared.Rent(packetLength);
                    buffer.Dequeue(rented.AsSpan(0, packetLength));
                }

                // LOGICA FUORI DAL LOCK
                try
                {
                    ReadOnlySpan<byte> data = rented.AsSpan(0, packetLength);
                    // La tua PacketReader riceve lo span e imposta Position = 1 o 3
                    PacketReader r = new PacketReader(ref data, packetLength, handler.Length != 0);

                    handler.OnReceive(ns, ref r);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }
    }
}
