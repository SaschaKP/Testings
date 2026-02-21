#region References
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using Server.Items;
using System.Threading.Tasks;
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

        public MessagePump()
        {
        }

        public void Begin()
        {
            
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
            Thread_Slice();
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
                while (buffer.Length > 0)
                {
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
                    if (buffer.Length < packetLength) 
                    {
                        TraceUnknownPacket(ns, buffer);
                        break; 
                    }

                    byte[] rented = ArrayPool<byte>.Shared.Rent(packetLength);
                    buffer.Dequeue(rented.AsSpan(0, packetLength));

                    m_ToProcess.Enqueue
                    (
                        new PendingPacket
                        {
                            State = ns,
                            Buffer = rented,
                            Length = packetLength,
                            Handler = handler
                        }
                    );
                }
            }

            if(ns.IsDisposing)
            {
                ns.InternalFinalize();
            }

            Core.Set();
        }

        public void Slice()
        {
            // Questo gira sul MAIN THREAD (Thread Original)
            while (m_ToProcess.TryDequeue(out PendingPacket pkt))
            {
                bool isValid = false;
                NetState ns = null;
                try
                {
                    ns = pkt.State;
                    isValid = ns != null && ns.Running && !ns.IsDisposing;

                    if (isValid && pkt.Handler != null)
                    {
                        ReadOnlySpan<byte> data = pkt.Buffer.AsSpan(0, pkt.Length);
                        PacketReader r = new PacketReader(ref data, pkt.Length, pkt.Handler.Length != 0);

                        // Qui sei al sicuro: il World è bloccato su questo thread
                        pkt.Handler.OnReceive(pkt.State, ref r);
                        Console.WriteLine($"pkt.State: {pkt.State} - pkt.Handler.PacketId: {pkt.Handler.PacketID} - pkt.State.IsDisposing: {pkt.State.IsDisposing}");
                    }
                    //TODO: rimuovere dopo il debug!
                    else
                    {
                        Console.WriteLine($"pkt.State: {pkt.State?.ToString() ?? "null"} - pkt.State.IsDisposing: {pkt.State?.IsDisposing.ToString() ?? "false"} - pkt.Handler: {pkt.Handler?.PacketID.ToString() ?? "unknown"} - pkt.State.IsDisposing: {pkt.State?.IsDisposing.ToString() ?? "true"}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(pkt.Buffer);
                    //int remaining = Interlocked.Decrement(ref pkt.State.PacketsInQueue);

                    if(isValid)
                    {
                        ns.Receive_Start();
                    }
                    else
                    {
                        ns?.Dispose();
                    }
                }
            }
        }

        private static AutoResetEvent _Signal = new AutoResetEvent(true);
        private void Thread_Slice()
        {
            while (!Core.Closing)
            {
                _Signal.WaitOne(10); // Svegliato da OnReceive o Listener

                // 1. Accetta nuove connessioni
                CheckListener();

                // 2. Processa i NetState che hanno ricevuto dati
                while (m_Queue.TryDequeue(out NetState ns))
                {
                    if (ns != null)
                    {
                        if(!ns.IsDisposing)
                            OnReceive(ns);
                        else
                            ns.InternalFinalize();
                    }
                }
                NetState.ProcessDisposedQueue();
            }
        }

        public void OnReceive_Notify()
        {
            _Signal.Set();
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
    }
}
