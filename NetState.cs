#region Header
// **********
// ServUO - NetState.cs
// **********
#endregion

#region References
using Server.Accounting;
using Server.Diagnostics;
using Server.Gumps;
using Server.HuePickers;
using Server.Items;
using Server.Menus;
using Server.Mobiles;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using static Server.Network.SendQueue;
#endregion

namespace Server.Network
{
    public class NetState : IComparable<NetState>
    {
        public static Dictionary<uint, Account> LogInAccounts = new Dictionary<uint, Account>();

        public bool IsEnglish
        {
            get
            {
                if (m_Account != null)
                {
                    return m_Account.AutoTranslate;
                }

                return false;
            }
        }
        public const string SitoWeb = "127.0.0.1";
        private Socket m_Socket;
        private IPAddress m_Address;
        private ByteQueue m_Buffer;
        private byte[] m_RecvBuffer;
        private SendQueue m_SendQueue;
        private bool m_Running;

        public sbyte TimeOffset { get; set; }

		private SocketAsyncEventArgs m_ReceiveEventArgs, m_SendEventArgs;

        private MessagePump m_MessagePump;
        private ServerInfo[] m_ServerInfo;
        private Account m_Account;
        private PlayerMobile m_Mobile;
        private CityInfo[] m_CityInfo;
        private List<Gump> m_Gumps;
        private List<HuePicker> m_HuePickers;
        private List<IMenu> m_Menus;
        private List<SecureTrade> m_Trades;
        private bool m_CompressionEnabled;
        private string m_ToString;
        private ClientVersion m_Version;
        private bool m_BlockAllPackets;

        private DateTime m_ConnectedOn;

        public DateTime ConnectedOn => m_ConnectedOn;

        public TimeSpan ConnectedFor => (DateTime.UtcNow - m_ConnectedOn);

        public bool InGame => m_Mobile != null;

        internal int m_Seed;
        internal int m_AuthID;

        public IPAddress Address => m_Address;

        private static bool m_Paused;

        [Flags]
        private enum AsyncState
        {
            Pending = 0x01,
            Paused = 0x02
        }

        private AsyncState m_AsyncState;
        private object m_AsyncLock = new object();

        public bool SentFirstPacket { get; set; }

        public bool BlockAllPackets 
        { 
            get => m_BlockAllPackets;
            set => m_BlockAllPackets = value;
        }

        public ClientFlags Flags { get; set; }

        public ClientVersion Version
        {
            get => m_Version;
            set
            {
                m_Version = value;

                if (value >= m_Version70331)
                {
                    _ProtocolChanges = ProtocolChanges.Version70331;
                }
                else if (value >= m_Version70300)
                {
                    _ProtocolChanges = ProtocolChanges.Version70300;
                }
                else if (value >= m_Version70160)
                {
                    _ProtocolChanges = ProtocolChanges.Version70160;
                }
                else if (value >= m_Version70130)
                {
                    _ProtocolChanges = ProtocolChanges.Version70130;
                }
                else if (value >= m_Version7090)
                {
                    _ProtocolChanges = ProtocolChanges.Version7090;
                }
                else if (value >= m_Version7000)
                {
                    _ProtocolChanges = ProtocolChanges.Version7000;
                }
                else if (value >= m_Version60142)
                {
                    _ProtocolChanges = ProtocolChanges.Version60142;
                }
                else if (value >= m_Version6017)
                {
                    _ProtocolChanges = ProtocolChanges.Version6017;
                }
                else if (value >= m_Version6000)
                {
                    _ProtocolChanges = ProtocolChanges.Version6000;
                }
                else if (value >= m_Version502b)
                {
                    _ProtocolChanges = ProtocolChanges.Version502b;
                }
                else if (value >= m_Version500a)
                {
                    _ProtocolChanges = ProtocolChanges.Version500a;
                }
                else if (value >= m_Version407a)
                {
                    _ProtocolChanges = ProtocolChanges.Version407a;
                }
                else if (value >= m_Version400a)
                {
                    _ProtocolChanges = ProtocolChanges.Version400a;
                }
            }
        }

        private static ClientVersion m_Version400a = new ClientVersion("4.0.0a");
        private static ClientVersion m_Version407a = new ClientVersion("4.0.7a");
        private static ClientVersion m_Version500a = new ClientVersion("5.0.0a");
        private static ClientVersion m_Version502b = new ClientVersion("5.0.2b");
        private static ClientVersion m_Version6000 = new ClientVersion("6.0.0.0");
        private static ClientVersion m_Version6017 = new ClientVersion("6.0.1.7");
        private static ClientVersion m_Version60142 = new ClientVersion("6.0.14.2");
        private static ClientVersion m_Version7000 = new ClientVersion("7.0.0.0");
        private static ClientVersion m_Version7090 = new ClientVersion("7.0.9.0");
        private static ClientVersion m_Version70130 = new ClientVersion("7.0.13.0");
        private static ClientVersion m_Version70160 = new ClientVersion("7.0.16.0");
        private static ClientVersion m_Version70300 = new ClientVersion("7.0.30.0");
        private static ClientVersion m_Version70331 = new ClientVersion("7.0.33.1");

        private ProtocolChanges _ProtocolChanges;

        private enum ProtocolChanges
        {
            NewSpellbook = 0x00000001,
            DamagePacket = 0x00000002,
            Unpack = 0x00000004,
            BuffIcon = 0x00000008,
            NewHaven = 0x00000010,
            ContainerGridLines = 0x00000020,
            ExtendedSupportedFeatures = 0x00000040,
            StygianAbyss = 0x00000080,
            HighSeas = 0x00000100,
            NewCharacterList = 0x00000200,
            NewCharacterCreation = 0x00000400,
            ExtendedStatus = 0x00000800,
            NewMobileIncoming = 0x00001000,

            Version400a = NewSpellbook,
            Version407a = Version400a | DamagePacket,
            Version500a = Version407a | Unpack,
            Version502b = Version500a | BuffIcon,
            Version6000 = Version502b | NewHaven,
            Version6017 = Version6000 | ContainerGridLines,
            Version60142 = Version6017 | ExtendedSupportedFeatures,
            Version7000 = Version60142 | StygianAbyss,
            Version7090 = Version7000 | HighSeas,
            Version70130 = Version7090 | NewCharacterList,
            Version70160 = Version70130 | NewCharacterCreation,
            Version70300 = Version70160 | ExtendedStatus,
            Version70331 = Version70300 | NewMobileIncoming,
        }

        public bool NewSpellbook => ((_ProtocolChanges & ProtocolChanges.NewSpellbook) != 0);
        public bool DamagePacket => ((_ProtocolChanges & ProtocolChanges.DamagePacket) != 0);
        public bool Unpack => ((_ProtocolChanges & ProtocolChanges.Unpack) != 0);
        public bool BuffIcon => ((_ProtocolChanges & ProtocolChanges.BuffIcon) != 0);
        public bool NewHaven => ((_ProtocolChanges & ProtocolChanges.NewHaven) != 0);
        public bool ContainerGridLines => ((_ProtocolChanges & ProtocolChanges.ContainerGridLines) != 0);
        public bool ExtendedSupportedFeatures => ((_ProtocolChanges & ProtocolChanges.ExtendedSupportedFeatures) != 0);
        public bool StygianAbyss => ((_ProtocolChanges & ProtocolChanges.StygianAbyss) != 0);
        public bool HighSeas => ((_ProtocolChanges & ProtocolChanges.HighSeas) != 0);
        public bool NewCharacterList => ((_ProtocolChanges & ProtocolChanges.NewCharacterList) != 0);
        public bool NewCharacterCreation => ((_ProtocolChanges & ProtocolChanges.NewCharacterCreation) != 0);
        public bool ExtendedStatus => ((_ProtocolChanges & ProtocolChanges.ExtendedStatus) != 0);
        public bool NewMobileIncoming => ((_ProtocolChanges & ProtocolChanges.NewMobileIncoming) != 0);

        public bool IsUOTDClient => ((Flags & ClientFlags.UOTD) != 0 || (m_Version != null && m_Version.Type == ClientType.UOTD));

        public bool IsSAClient => (m_Version != null && m_Version.Type == ClientType.SA);

        public bool IsCUOClient => (Flags & ClientFlags.CUO) != 0;

        public List<SecureTrade> Trades => m_Trades;

        public void ValidateAllTrades()
        {
            for (int i = m_Trades.Count - 1; i >= 0; --i)
            {
                if (i >= m_Trades.Count)
                {
                    continue;
                }

                SecureTrade trade = m_Trades[i];

                if (trade.From.Mobile.Deleted || trade.To.Mobile.Deleted || !trade.From.Mobile.Alive || !trade.To.Mobile.Alive || !trade.From.Mobile.InRange(trade.To.Mobile, Item.GrabRange) || trade.From.Mobile.Map != trade.To.Mobile.Map)
                {
                    trade.Cancel();
                }
            }
        }

        public void CancelAllTrades()
        {
            for (int i = m_Trades.Count - 1; i >= 0; --i)
            {
                if (i < m_Trades.Count)
                {
                    m_Trades[i].Cancel();
                }
            }
        }

        public void RemoveTrade(SecureTrade trade)
        {
            m_Trades.Remove(trade);
        }

        public SecureTrade FindTrade(PlayerMobile m)
        {
            for (int i = 0; i < m_Trades.Count; ++i)
            {
                SecureTrade trade = m_Trades[i];

                if (trade.From.Mobile == m || trade.To.Mobile == m)
                {
                    return trade;
                }
            }

            return null;
        }

        public SecureTradeContainer FindTradeContainer(PlayerMobile m)
        {
            for (int i = 0; i < m_Trades.Count; ++i)
            {
                SecureTrade trade = m_Trades[i];

                SecureTradeInfo from = trade.From;
                SecureTradeInfo to = trade.To;

                if (from.Mobile == m_Mobile && to.Mobile == m)
                {
                    return from.Container;
                }
                else if (from.Mobile == m && to.Mobile == m_Mobile)
                {
                    return to.Container;
                }
            }

            return null;
        }

        public SecureTradeContainer AddTrade(NetState state)
        {
            SecureTrade newTrade = new SecureTrade(m_Mobile, state.m_Mobile);

            m_Trades.Add(newTrade);
            state.m_Trades.Add(newTrade);

            return newTrade.From.Container;
        }

        public bool CompressionEnabled { get => m_CompressionEnabled; set => m_CompressionEnabled = value; }

        public int Sequence { get; set; }

        public List<Gump> Gumps => m_Gumps;

        public List<HuePicker> HuePickers => m_HuePickers;

        public List<IMenu> Menus => m_Menus;

        public static int GumpCap { get; set; } = 1024;

        public static int HuePickerCap { get; set; } = 1024;

        public static int MenuCap { get; set; } = 1024;

        public void WriteConsole(string text)
        {
            Console.WriteLine("Client: {0}: {1}", this, text);
        }

        public void WriteConsole(string format, params object[] args)
        {
            WriteConsole(string.Format(format, args));
        }

        public void AddMenu(IMenu menu)
        {
            if (m_Menus == null)
            {
                m_Menus = new List<IMenu>();
            }

            if (m_Menus.Count < MenuCap)
            {
                m_Menus.Add(menu);
            }
            else
            {
                WriteConsole("Exceeded menu cap, disconnecting...");
                Dispose();
            }
        }

        public void RemoveMenu(IMenu menu)
        {
            if (m_Menus != null)
            {
                m_Menus.Remove(menu);
            }
        }

        public void RemoveMenu(int index)
        {
            if (m_Menus != null)
            {
                m_Menus.RemoveAt(index);
            }
        }

        public void ClearMenus()
        {
            if (m_Menus != null)
            {
                m_Menus.Clear();
            }
        }

        public void AddHuePicker(HuePicker huePicker)
        {
            if (m_HuePickers == null)
            {
                m_HuePickers = new List<HuePicker>();
            }

            if (m_HuePickers.Count < HuePickerCap)
            {
                m_HuePickers.Add(huePicker);
            }
            else
            {
                WriteConsole("Exceeded hue picker cap, disconnecting...");
                Dispose();
            }
        }

        public void RemoveHuePicker(HuePicker huePicker)
        {
            if (m_HuePickers != null)
            {
                m_HuePickers.Remove(huePicker);
            }
        }

        public void RemoveHuePicker(int index)
        {
            if (m_HuePickers != null)
            {
                m_HuePickers.RemoveAt(index);
            }
        }

        public void ClearHuePickers()
        {
            if (m_HuePickers != null)
            {
                m_HuePickers.Clear();
            }
        }

        public void AddGump(Gump gump)
        {
            if (m_Gumps == null)
            {
                m_Gumps = new List<Gump>();
            }

            if (m_Gumps.Count < GumpCap)
            {
                m_Gumps.Add(gump);
            }
            else
            {
                WriteConsole("Exceeded gump cap, disconnecting...");
                Dispose();
            }
        }

        public void RemoveGump(Gump gump)
        {
            if (m_Gumps != null)
            {
                m_Gumps.Remove(gump);
            }
        }

        public void RemoveGump(int index)
        {
            if (m_Gumps != null)
            {
                m_Gumps.RemoveAt(index);
            }
        }

        public void ClearGumps()
        {
            if (m_Gumps != null)
            {
                m_Gumps.Clear();
            }
        }

        public void LaunchBrowser(string url)
        {
            Send(new MessageLocalized(Serial.MinusOne, -1, MessageType.Label, 0x35, 3, 501231, "", ""));
            Send(new LaunchBrowser(url));
        }

        public CityInfo[] CityInfo { get => m_CityInfo; set => m_CityInfo = value; }

        public PlayerMobile Mobile { get => m_Mobile; set => m_Mobile = value; }

        public ServerInfo[] ServerInfo { get => m_ServerInfo; set => m_ServerInfo = value; }

        public Account Account
        {
            get => m_Account;
            set => m_Account = value;
        }

        public override string ToString()
        {
            return m_ToString;
        }

        private static List<NetState> m_Instances = new List<NetState>();

        public static List<NetState> Instances => m_Instances;

        private static BufferPool m_ReceiveBufferPool = new BufferPool("Receive", 2048, 2048);

        public NetState(Socket socket, MessagePump messagePump)
        {
            m_Socket = socket;
            m_Buffer = new ByteQueue();
            Seeded = false;
            m_Running = false;
            m_RecvBuffer = m_ReceiveBufferPool.AcquireBuffer();
            m_MessagePump = messagePump;
            m_Gumps = new List<Gump>();
            m_HuePickers = new List<HuePicker>();
            m_Menus = new List<IMenu>();
            m_Trades = new List<SecureTrade>();
            m_SendQueue = new SendQueue();

            m_NextCheckActivity = DateTime.UtcNow + TimeSpan.FromMinutes(0.5);

            m_Instances.Add(this);

            try
            {
                m_Address = Utility.Intern(((IPEndPoint)m_Socket.RemoteEndPoint).Address);
                m_ToString = m_Address.ToString();
            }
            catch (Exception ex)
            {
                if (Core.Debug)
                {
                    TraceException(ex);
                }

                m_Address = IPAddress.None;
                m_ToString = "(error)";
            }

            m_ConnectedOn = DateTime.UtcNow;
        }

        private bool _sending;
        private object _sendL = new object();
        private object _compressorLock = new object();

        public void Send(Packet p)
        {
            if (p == null)
            {
                return;
            }    
            else if (m_Socket == null || m_BlockAllPackets)
            {
                p.OnSend();
                return;
            }

            byte[] buffer = null;
            int length = 0;
            lock (_compressorLock)
            {
                buffer = p.Compile(m_CompressionEnabled, out length);
            }
            if (buffer != null)
            {
                if (buffer.Length <= 0 || length <= 0)
                {
                    p.OnSend();
                    return;
                }

                try
                {
                    SendQueue.Gram gram;

                    lock (_sendL)
                    {
                        lock (m_SendQueue)
                        {
                            gram = m_SendQueue.Enqueue(buffer, length);
                        }

                        if (gram != null && !_sending)
                        {
                            _sending = true;

						    m_SendEventArgs.SetBuffer( gram.Buffer, 0, gram.Length );
						    Send_Start();
                        }
                    }
                }
                catch (CapacityExceededException)
                {
                    Console.WriteLine("Client: {0}: Too much data pending, disconnecting...", this);
                    Dispose(false);
                }

                p.OnSend();
            }
            else
            {
                bool opl = p is OPLInfo;
                using (StreamWriter op = new StreamWriter("null_send.log", true))
                {
                    op.Write("{0} Client: {1}: null buffer send...", Core.MistedDateTime, this);
                    if (opl)
                    {
                        op.WriteLine("ignoring");
                        op.WriteLine(new StackTrace());
                        Console.WriteLine("Client: {0}: null buffer send...ignoring", this);
                    }
                    else
                    {
                        op.WriteLine("disconnecting");
                        op.WriteLine(new StackTrace());
                        Console.WriteLine("Client: {0}: null buffer send...disconnecting", this);
                        Dispose();
                    }
                }
            }
        }

		public void Start() 
        {
			m_ReceiveEventArgs = new SocketAsyncEventArgs();
			m_ReceiveEventArgs.Completed += new EventHandler<SocketAsyncEventArgs>( Receive_Completion );
			m_ReceiveEventArgs.SetBuffer( m_RecvBuffer, 0, m_RecvBuffer.Length );

			m_SendEventArgs = new SocketAsyncEventArgs();
			m_SendEventArgs.Completed += new EventHandler<SocketAsyncEventArgs>( Send_Completion );

			m_Running = true;

			if ( m_Socket == null || m_Paused ) {
				return;
			}

			Receive_Start();
		}

        internal void Receive_Start()
        {
            // Usiamo un loop per gestire le operazioni che finiscono in modo sincrono
            // senza fare ricorsione e senza usare Task.Run
            bool isPending = true;

            while (isPending)
            {
                if (!m_Running || m_Disposing || m_Paused)
                    return;

                lock (m_AsyncLock)
                {
                    // Se c'è già un'operazione pendente o siamo in pausa, usciamo
                    if ((m_AsyncState & (AsyncState.Pending | AsyncState.Paused)) != 0)
                        return;

                    m_AsyncState |= AsyncState.Pending;
                }

                try
                {
                    // Se ReceiveAsync torna false, l'operazione è completata SINCROMAMENTE
                    if (!m_Socket.ReceiveAsync(m_ReceiveEventArgs))
                    {
                        // Processiamo i dati subito
                        int byteCount = m_ReceiveEventArgs.BytesTransferred;

                        if (m_ReceiveEventArgs.SocketError != SocketError.Success || byteCount <= 0)
                        {
                            Dispose(false);
                            return;
                        }

                        // Puliamo lo stato pending PRIMA di processare per permettere il prossimo ciclo
                        lock (m_AsyncLock)
                        {
                            m_AsyncState &= ~AsyncState.Pending;
                        }

                        // Inserimento dati e notifica al MessagePump
                        ProcessReceivedData(byteCount);

                        // Il loop 'while' ricomincerà e chiamerà di nuovo ReceiveAsync
                        // Questo evita lo StackOverflow perché siamo in un ciclo, non in ricorsione
                        continue;
                    }
                    else
                    {
                        // L'operazione è asincrona (verrà gestita da Receive_Completion)
                        isPending = false;
                    }
                }
                catch (Exception ex)
                {
                    if (Core.Debug) TraceException(ex);
                    Dispose(false);
                    return;
                }
            }
        }

        private void ProcessReceivedData(int byteCount)
        {
            m_NextCheckActivity = DateTime.UtcNow + TimeSpan.FromMinutes(1.2);

            lock (m_Buffer)
            {
                m_Buffer.Enqueue(m_RecvBuffer.AsSpan(0, byteCount));
            }

            m_MessagePump.OnReceive(this);
            m_MessagePump.OnReceive_Notify();
        }

        private void Receive_Completion(object sender, SocketAsyncEventArgs e)
        {
            lock (m_AsyncLock)
            {
                m_AsyncState &= ~AsyncState.Pending;
            }

            int byteCount = e.BytesTransferred;

            if (e.SocketError != SocketError.Success || byteCount <= 0)
            {
                Dispose(false);
                return;
            }

            ProcessReceivedData(byteCount);

            // Dopo il completamento asincrono, riavviamo il loop di ricezione
            // Se il client è Throttled, non chiamiamo Receive_Start qui
            Receive_Start();
        }

        private void Send_Start()
		{
			try {
				bool result = false;

				do {
					result = !m_Socket.SendAsync( m_SendEventArgs );

					if ( result )
						Send_Process( m_SendEventArgs );
				} while ( result ); 
			} catch ( Exception ex ) {
				if(Core.Debug)
					TraceException( ex );
				Dispose( false );
			}
		}

        private void Send_Completion(object sender, SocketAsyncEventArgs e)
        {
            Send_Process(e);
            if (!m_Disposing)
            {
                // Continua l'invio della coda finché non è vuota
                Send_InternalExecute();
            }
        }

        private void Send_Process( SocketAsyncEventArgs e )
		{
			int bytes = e.BytesTransferred;

			if ( e.SocketError != SocketError.Success || bytes <= 0 ) {
				Dispose( false );
				return;
			}

			m_NextCheckActivity = DateTime.UtcNow + TimeSpan.FromMinutes( 1.2 );
		}

		public static void Pause() {
			m_Paused = true;

			for ( int i = 0; i < m_Instances.Count; ++i ) {
				NetState ns = m_Instances[i];

				lock ( ns.m_AsyncLock ) {
					ns.m_AsyncState |= AsyncState.Paused;
				}
			}
		}

		public static void Resume() {
			m_Paused = false;

			for ( int i = 0; i < m_Instances.Count; ++i ) {
				NetState ns = m_Instances[i];

				if ( ns.m_Socket == null ) {
					continue;
				}

				lock ( ns.m_AsyncLock ) {
					ns.m_AsyncState &= ~AsyncState.Paused;

					if ( ( ns.m_AsyncState & AsyncState.Pending ) == 0 )
						ns.Receive_Start();
				}
			}
		}

        private int m_IsSending = 0; // 0 = Idle, 1 = Busy
        public bool Flush()
        {
            if (m_Socket == null || !m_Running) return false;

            // Tenta di impostare m_IsSending a 1 solo se è 0. Operazione atomica.
            if (Interlocked.CompareExchange(ref m_IsSending, 1, 0) != 0)
                return false; // Era già in corso un invio

            try
            {
                Send_InternalExecute();
                return true;
            }
            catch
            {
                Interlocked.Exchange(ref m_IsSending, 0);
                return false;
            }
        }

        private void Send_InternalExecute()
        {
            bool isCompletedSynchronously;
            do
            {
                isCompletedSynchronously = false;
                SendQueue.Gram gram;
                lock (m_SendQueue)
                {
                    gram = m_SendQueue.Dequeue();
                    if (gram == null && m_SendQueue.IsFlushReady)
                        gram = m_SendQueue.CheckFlushReady();
                }
                if (gram != null)
                {
                    m_SendEventArgs.SetBuffer(gram.Buffer, 0, gram.Length);
                    if (!m_Socket.SendAsync(m_SendEventArgs))
                    {
                        Send_Process(m_SendEventArgs);
                        isCompletedSynchronously = true; // Continua il loop senza richiamare se stesso
                    }
                }
                else
                {
                    Interlocked.Exchange(ref m_IsSending, 0);
                }
            } while (isCompletedSynchronously && m_Running);
        }

        public PacketHandler GetHandler(byte packetID)
        {
            //if (ContainerGridLines)
            //{
            return PacketHandlers.Get6017Handler(packetID);
            /*}
			else
			{
				return PacketHandlers.GetHandler(packetID);
			}*/
        }

        public static void FlushAll()
        {
            if (m_Instances.Count > 1024)
            {
                Parallel.ForEach(m_Instances, ns => ns.Flush());
            }
            else
            {
                for (int i = 0; i < m_Instances.Count; ++i)
                {
                    m_Instances[i].Flush();
                }
            }
        }

        public static int CoalesceSleep { get; set; } = -1;

        private DateTime m_NextCheckActivity;

        public void CheckAlive()
        {
            if (m_Socket == null)
            {
                return;
            }

            if (DateTime.UtcNow < m_NextCheckActivity)
            {
                return;
            }

            if (!Core.LocalHost)
            {
                Console.WriteLine("Client: {0}: Disconnecting due to inactivity...", this);
                PacketHandlers.AccountLogin_ReplyRej(this, ALRReason.Idle);
            }
            else
                Console.WriteLine("Client: {0}: Client NOT Disconnected, but inactive...", this);
        }

        public static void TraceException(Exception ex)
        {
            if (!Core.Debug)
            {
                return;
            }

            try
            {
                using (StreamWriter op = new StreamWriter("network-errors.log", true))
                {
                    op.WriteLine("# {0}", Core.MistedDateTime);

                    op.WriteLine(ex);

                    op.WriteLine();
                    op.WriteLine();
                }
            }
            catch
            { }

            try
            {
                Console.WriteLine(ex);
            }
            catch
            { }
        }

        private bool m_Disposing;
        public bool IsDisposing => m_Disposing;

        public void Dispose()
        {
            Dispose(true);
        }

        public void Dispose(bool flush)
        {
            if (m_Socket == null || m_Disposing)
                return;

            m_Disposing = true;

            if (flush)
            {
                flush = Flush();
            }

            if (m_Gumps != null)
            {
                for (int i = m_Gumps.Count - 1; i >= 0; --i)
                {
                    m_Gumps[i].OnServerClose(this);
                }
            }

            m_Disposed.Enqueue(this);
        }

        public void InternalFinalize()
        {
            try
            {
                m_Socket?.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException ex)
            {
                if (Core.Debug)
                {
                    TraceException(ex);
                }
            }

            try
            {
                m_Socket?.Close();
            }
            catch (SocketException ex)
            {
                if (Core.Debug)
                {
                    TraceException(ex);
                }
            }

            if (m_RecvBuffer != null)
            {
                lock (m_ReceiveBufferPool)
                {
                    m_ReceiveBufferPool.ReleaseBuffer(m_RecvBuffer);
                }
            }

            m_Socket = null;
            m_Buffer?.Dispose();
            m_Buffer = null;
            m_RecvBuffer = null;

			m_ReceiveEventArgs = null;
			m_SendEventArgs = null;

            lock (m_SendQueue)
            {
                if (!m_SendQueue.IsEmpty)
                {
                    m_SendQueue.Clear();
                }
            }
        }

        public static void Initialize()
        {
            Timer.DelayCall(TimeSpan.FromMinutes(1.0), TimeSpan.FromMinutes(1.5), CheckAllAlive);
        }

        public static void CheckAllAlive()
        {
            try
            {
                if (m_Instances.Count >= 1024)
                {
                    Parallel.ForEach(m_Instances, ns => ns.CheckAlive());
                }
                else
                {
                    for (int i = 0; i < m_Instances.Count; ++i)
                    {
                        m_Instances[i].CheckAlive();
                    }
                }
            }
            catch (Exception ex)
            {
                if (Core.Debug)
                {
                    TraceException(ex);
                }
            }
        }

        private static ConcurrentQueue<NetState> m_Disposed = new();

        public static void ProcessDisposedQueue()
        {
            int breakout = 0;

            while (breakout < 200 && m_Disposed.Count > 0)
            {
                ++breakout;
                if (m_Disposed.TryDequeue(out NetState ns))
                {
                    ns.InternalFinalize();
                    Mobile m = ns.m_Mobile;
                    Account a = ns.m_Account;

                    if (m != null)
                    {
                        m.NetState = null;
                        ns.m_Mobile = null;
                    }

                    ns.m_Gumps.Clear();
                    ns.m_Menus.Clear();
                    ns.m_HuePickers.Clear();
                    ns.m_Account = null;
                    ns.m_ServerInfo = null;
                    ns.m_CityInfo = null;

                    m_Instances.Remove(ns);

                    if (a != null)
                    {
                        ns.WriteConsole("Disconnected. [{0} Online] [{1}]", m_Instances.Count, a);
                    }
                    else if (ns.m_ToString != SitoWeb)
                    {
                        ns.WriteConsole("Disconnected. [{0} Online]", m_Instances.Count);
                    }
                }
            }
        }

        public bool Running => m_Running;

        public bool Seeded { get; set; }

        public Socket Socket => m_Socket;

        public ByteQueue Buffer => m_Buffer;

        public ExpansionInfo ExpansionInfo
        {
            get
            {
                for (int i = ExpansionInfo.Table.Length - 1; i >= 0; i--)
                {
                    ExpansionInfo info = ExpansionInfo.Table[i];

                    if ((info.RequiredClient != null && Version >= info.RequiredClient) || ((Flags & info.ClientFlags) != 0))
                    {
                        return info;
                    }
                }

                return ExpansionInfo.GetInfo(Expansion.None);
            }
        }

        public Expansion Expansion => (Expansion)ExpansionInfo.ID;

        public bool SupportsExpansion(ExpansionInfo info, bool checkCoreExpansion)
        {
            if (info == null || (checkCoreExpansion && (int)Core.Expansion < info.ID))
            {
                return false;
            }

            if (info.RequiredClient != null)
            {
                return (Version >= info.RequiredClient);
            }

            return ((Flags & info.ClientFlags) != 0);
        }

        public bool SupportsExpansion(Expansion ex, bool checkCoreExpansion)
        {
            return SupportsExpansion(ExpansionInfo.GetInfo(ex), checkCoreExpansion);
        }

        public bool SupportsExpansion(Expansion ex)
        {
            return SupportsExpansion(ex, true);
        }

        public bool SupportsExpansion(ExpansionInfo info)
        {
            return SupportsExpansion(info, true);
        }

        public int CompareTo(NetState other)
        {
            if (other == null)
            {
                return 1;
            }

            return m_ToString.CompareTo(other.m_ToString);
        }
    }
}
