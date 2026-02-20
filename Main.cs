/***************************************************************************
 *                                  Main.cs
 *                            -------------------
 *   begin                : May 1, 2002
 *   copyright            : (C) The RunUO Software Team
 *   email                : info@runuo.com
 *
 *   $Id$
 *
 ***************************************************************************/

/***************************************************************************
 *
 *   This program is free software; you can redistribute it and/or modify
 *   it under the terms of the GNU General Public License as published by
 *   the Free Software Foundation; either version 2 of the License, or
 *   (at your option) any later version.
 *
 ***************************************************************************/

using Server.Network;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Server
{
    public delegate void Slice();

    public static class Core
    {
        static Core()
        {
            DataDirectories = new List<string>();
            Service = true;
            Debug = false;
            LocalHost = false;

            GlobalRadarRange = 40;
            GlobalMaxUpdateRange = 24;
            GlobalUpdateRange = 18;
        }

        private static bool _Crashed;
        private static Thread _TimerThread;
        private static string _BaseDirectory;
        private static string _ExePath;

        private static bool _Profiling;
        private static DateTime _ProfileStart;
        private static TimeSpan _ProfileTime;

        public static MessagePump MessagePump { get; set; }

        public static Slice Slice;

        public static bool Profiling
        {
            get => _Profiling;
            set
            {
                if (_Profiling == value)
                {
                    return;
                }

                _Profiling = value;

                if (_ProfileStart > DateTime.MinValue)
                {
                    _ProfileTime += DateTime.UtcNow - _ProfileStart;
                }

                _ProfileStart = (_Profiling ? DateTime.UtcNow : DateTime.MinValue);
            }
        }

        public static Encoding AsciiEncoding { get; private set; }

        public static TimeSpan ProfileTime
        {
            get
            {
                if (_ProfileStart > DateTime.MinValue)
                {
                    return _ProfileTime + (DateTime.UtcNow - _ProfileStart);
                }

                return _ProfileTime;
            }
        }

        public static bool Service { get; private set; }
        public static bool Debug { get; private set; }
        public static bool LocalHost { get; private set; }
        public static bool HaltOnWarning { get; private set; }
        public static bool VBdotNET { get; private set; }
        public static List<string> DataDirectories { get; private set; }
        public static Assembly Assembly { get; set; }
        public static Version Version => Assembly.GetName().Version;
        public static Process Process { get; private set; }
        public static Thread Thread { get; private set; }
        public static MultiTextWriter MultiConsoleOut { get; private set; }

        public static bool Is64Bit = Environment.Is64BitProcess;

        public static bool MultiProcessor { get; private set; }
        public static int ProcessorCount { get; private set; }

        public static bool Unix { get; private set; }

        public static string FindDataFile(string path)
        {
            if (DataDirectories.Count == 0)
            {
                throw new InvalidOperationException("Attempted to FindDataFile before DataDirectories list has been filled.");
            }

            string fullPath = null;

            foreach (string p in DataDirectories)
            {
                fullPath = Path.Combine(p, path);

                if (File.Exists(fullPath))
                {
                    break;
                }

                fullPath = null;
            }

            return fullPath;
        }

        public static string FindDataFile(string format, params object[] args)
        {
            return FindDataFile(string.Format(format, args));
        }

        #region Expansions

        public static Expansion Expansion { get; set; }
        public static CultureInfo Culture;
        public static CultureInfo InvertedDotCulture;

        public static bool T2A => Expansion >= Expansion.T2A;
        public static bool UOR => Expansion >= Expansion.UOR;
        public static bool UOTD => Expansion >= Expansion.UOTD;
        public static bool LBR => Expansion >= Expansion.LBR;
        public static bool AOS => Expansion >= Expansion.AOS;
        public static bool SE => Expansion >= Expansion.SE;
        public static bool ML => Expansion >= Expansion.ML;
        public static bool SA => Expansion >= Expansion.SA;
        public static bool HS => Expansion >= Expansion.HS;
        #endregion

        public static string ExePath => _ExePath ?? (_ExePath = Assembly.Location);

        public static string BaseDirectory
        {
            get
            {
                if (_BaseDirectory == null)
                {
                    try
                    {
                        _BaseDirectory = ExePath;

                        if (_BaseDirectory.Length > 0)
                        {
                            _BaseDirectory = Path.GetDirectoryName(_BaseDirectory);
                        }
                    }
                    catch
                    {
                        _BaseDirectory = "";
                    }
                }

                return _BaseDirectory;
            }
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Console.WriteLine(e.IsTerminating ? "Error:" : "Warning:");
            Console.WriteLine(e.ExceptionObject);

            if (e.IsTerminating)
            {
                _Crashed = true;

                bool close = false;

                try
                {
                    CrashedEventArgs args = new CrashedEventArgs(e.ExceptionObject as Exception);

                    EventSink.InvokeCrashed(args);

                    close = args.Close;
                }
                catch
                {
                }

                if (!close && !Service)
                {
                    try
                    {
                        for (int i = MessagePump.Listeners.Length - 1; i >= 0; --i)
                        {
                            MessagePump.Listeners[i].Dispose();
                        }
                    }
                    catch
                    {
                    }

                    Console.WriteLine("This exception is fatal, press return to exit");
                    Console.ReadLine();
                }

                Closing = true;
                //World.Save();
                try { Environment.Exit(1); } catch { }
            }
        }

        internal enum ConsoleEventType
        {
            CTRL_C_EVENT,
            CTRL_BREAK_EVENT,
            CTRL_CLOSE_EVENT,
            CTRL_LOGOFF_EVENT = 5,
            CTRL_SHUTDOWN_EVENT
        }

        internal delegate bool ConsoleEventHandler(ConsoleEventType type);
        internal static ConsoleEventHandler m_ConsoleEventHandler;

        private static class UnsafeNativeMethods
        {
            [DllImport("Kernel32")]
            public static extern bool SetConsoleCtrlHandler(ConsoleEventHandler callback, bool add);
        }

        private static bool OnConsoleEvent(ConsoleEventType type)
        {
            if (World.Saving || (Service && type == ConsoleEventType.CTRL_LOGOFF_EVENT))
            {
                return true;
            }

            Kill(); //Kill -> HandleClosed will handle waiting for the completion of flushing to disk

            return true;
        }

        private static void CurrentDomain_ProcessExit(object sender, EventArgs e)
        {
            HandleClosed();
        }

        public static bool Closing { get; private set; }

        private static int _CycleIndex = 1;
        private static float[] _CyclesPerSecond = new float[100];

        public static float CyclesPerSecond => _CyclesPerSecond[(_CycleIndex - 1) % _CyclesPerSecond.Length];

        public static float AverageCPS => _CyclesPerSecond.Take(_CycleIndex).Average();

        private static int _PreviousHour = -1;
        private static int _Differential;
        public static int Differential//to use in all cases where you rectify normal clocks obtained with utctimer!
        {
            get
            {
                if (_PreviousHour != DateTime.UtcNow.Hour)
                {
                    _PreviousHour = DateTime.UtcNow.Hour;
                    _Differential = DateTimeOffset.Now.Offset.Hours;
                }
                return _Differential;
            }
        }
        public static DateTime MistedDateTime => DateTime.UtcNow.AddHours(Differential);

        public static DateTime PrivateDateTime(sbyte differential)
        {
            return DateTime.UtcNow.AddHours(differential);
        }

        public static ParallelOptions ParallelOpts = new ParallelOptions { MaxDegreeOfParallelism = 4 };

        public static void Kill()
        {
            World.toReboot = false;
            Finale();
        }

        public static void Finale(bool reboot = false)
        {
            if (!World.AccountSave && World.toReboot)
            {
                if (reboot)
                {
                    NativeMethods.Reboot();
                }
                Core.Kill(World.toRestart);
            }
            else
            {
                Timer.DelayCall<bool>(TimeSpan.FromSeconds(1), Finale, reboot);
            }
        }

        public static void Kill(bool restart)
        {
            if (World.BackGroundSave || World.AccountSave)
            {
                return;
            }

            HandleClosed();

            if (restart)
            {
                Process.Start(ExePath, Arguments);
            }

            Process.Kill();
        }

        private static void HandleClosed()
        {
            if (Closing || World.BackGroundSave)
            {
                return;
            }

            Closing = true;

            Console.WriteLine("Exiting...");

            World.WaitForWriteCompletion();//flush of disk before writing...

            if (!_Crashed)
            {
                try
                {
                    if (World.CansaveOnclose && !World.Saving)
                    {
                        World.Save(true, false);
                    }
                    else if (World.CansaveOnclose && World.Saving)
                    {
                        Console.WriteLine("Salvataggio già in corso, non verrà effettuato il tentativo di save!");
                    }

                    World.WaitForWriteCompletion();//flush again to assure that save is REALLY done
                }
                catch (Exception e)
                {
                    Console.WriteLine("Salvataggio di emergenza non riuscito! {0}", e);
                }

                EventSink.InvokeShutdown(new ShutdownEventArgs());
            }

            Timer.TimerThread.Set();

            Console.WriteLine("Exiting...done");
        }

        private static AutoResetEvent _Signal = new AutoResetEvent(true);
        public static void Set()
        {
            _Signal.Set();
        }

        public static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("it-IT", false);
            Thread.CurrentThread.CurrentCulture.NumberFormat.NumberDecimalSeparator = ".";
            Thread.CurrentThread.CurrentCulture.NumberFormat.NumberGroupSeparator = ",";
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            Culture = CultureInfo.CurrentCulture;
            InvertedDotCulture = new CultureInfo("it-IT", false);
            InvertedDotCulture.NumberFormat.NumberDecimalSeparator = ",";
            InvertedDotCulture.NumberFormat.NumberGroupSeparator = ".";
            //for .net CORE
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            AsciiEncoding = Encoding.GetEncoding(1252);

            Thread = Thread.CurrentThread;
            Process = Process.GetCurrentProcess();
            Assembly = Assembly.GetEntryAssembly();

            if (!string.IsNullOrWhiteSpace(BaseDirectory))
                Directory.SetCurrentDirectory(BaseDirectory);

#if LOCALE
			LocalHost=true;
			Service=false;
			Debug=true;
#endif
            /*CultureInfo. = Thread.CurrentThread.CurrentCulture;
			CultureInfo.DefaultThreadCurrentUICulture = Thread.CurrentThread.CurrentCulture;*/

            foreach (string a in args)
            {
                if (Insensitive.Equals(a, "-debug"))
                {
                    Debug = true;
                }
                else if (Insensitive.Equals(a, "-noservice"))
                {
                    Service = false;
                }
                else if (Insensitive.Equals(a, "-profile"))
                {
                    Profiling = true;
                }
                else if (Insensitive.Equals(a, "-haltonwarning"))
                {
                    HaltOnWarning = true;
                }
                else if (Insensitive.Equals(a, "-localhost"))
                {
                    LocalHost = true;
                }
            }

            LocalConfig.Load();

            try
            {
                if (!Directory.Exists("Logs/ConsoleLogs"))
                {
                    Directory.CreateDirectory("Logs/ConsoleLogs");
                }

                if (File.Exists("Logs/Console.log"))
                {
                    FileInfo file = new FileInfo("Logs/Console.log");

                    string timeStamp = string.Format("Console_{0:yyyy}-{0:MMMM}-{0:dd}-({0:HH}_{0:mm}_{0:ss}).log", file.LastWriteTime);

                    if (timeStamp != null)
                    {
                        try { file.MoveTo(Path.Combine("Logs/ConsoleLogs", timeStamp)); }
                        catch { }
                    }
                }
                if (Service)
                {
                    Console.SetOut(MultiConsoleOut = new MultiTextWriter(TextWriter.Synchronized(new FileLogger("Logs/Console.log", false))));
                }
                else
                {
                    Console.SetOut(MultiConsoleOut = new MultiTextWriter(Console.Out, TextWriter.Synchronized(new FileLogger("Logs/Console.log", false))));
                }
            }
            catch
            {
            }

            ScriptCompiler.Force();

            if (Thread != null)
            {
                Thread.Name = "Core Thread";
                Thread.Priority = ThreadPriority.AboveNormal;
            }

            if (BaseDirectory.Length > 0)
            {
                Directory.SetCurrentDirectory(BaseDirectory);
            }

            Timer.TimerThread ttObj = new Timer.TimerThread();

            _TimerThread = new Thread(ttObj.TimerMain)
            {
                Name = "Timer Thread"
            };

            Version ver = Assembly.GetName().Version;

            // Added to help future code support on forums, as a 'check' people can ask for to it see if they recompiled core or not
            Console.WriteLine("UOI Reborn - [www.uoitalia.net] Versione {0}.{1}, Build {2}.{3}", ver.Major, ver.Minor, ver.Build, ver.Revision);
            Console.WriteLine("Core: giro su .NET Framework Versione {0}.{1}.{2}", Environment.Version.Major, Environment.Version.Minor, Environment.Version.Build);

            string s = Arguments;

            if (s.Length > 0)
            {
                Console.WriteLine("Core: Avviato con le seguenti flag: {0}", s);
            }

            ProcessorCount = Environment.ProcessorCount;

            if (ProcessorCount > 1)
            {
                MultiProcessor = true;
            }

            if (MultiProcessor || Is64Bit)
            {
                Console.WriteLine("Core: Ottimizzato per {0} processor{1} {2}", ProcessorCount, ProcessorCount == 1 ? "e" : "i", Is64Bit ? "64-bit" : "");
            }

            int platform = (int)Environment.OSVersion.Platform;
            if (platform == 4 || platform == 128)
            {
                // MS 4, MONO 128
                Unix = true;
                Console.WriteLine("Core: Unix environment detected");
            }
            else
            {
                m_ConsoleEventHandler = OnConsoleEvent;
                UnsafeNativeMethods.SetConsoleCtrlHandler(m_ConsoleEventHandler, true);
            }

            if (GCSettings.IsServerGC)
            {
                Console.WriteLine("Core: Server garbage collection mode enabled");
            }

            //ScriptCompiler.Assemblies = new Assembly[1] { Assembly.LoadFrom(Core.ExePath) };

            Console.Write("Scripts: Verifying...");

            Stopwatch watch = Stopwatch.StartNew();

            Core.VerifySerialization();

            watch.Stop();

            Console.WriteLine("done ({0} items, {1} mobiles) ({2:F2} seconds)", Core.ScriptItems, Core.ScriptMobiles,
              watch.Elapsed.TotalSeconds);

            ScriptCompiler.Invoke("Configure");

            Region.Load();
            World.Load();

            ScriptCompiler.Invoke("Initialize");

            MessagePump messagePump = MessagePump = new MessagePump();

            _TimerThread.Start();

            /*foreach (Map m in Map.AllMaps)
            {
                m.Tiles.Force();
            }*/

            NetState.Initialize();

            EventSink.InvokeServerStarted();

            try
            {
                long now, last = DateTime.UtcNow.Ticks;

                const int sampleInterval = 200;
                const float ticksPerSecond = TimeSpan.TicksPerSecond * sampleInterval;

                int sample = 0;

                while (!Closing)
                {
                    _Signal.WaitOne();

                    Task.Run(Mobile.ProcessDeltaQueue).Wait();
                    Task.Run(Item.ProcessDeltaQueue).Wait();

                    Timer.Slice();
                    messagePump.Slice();

                    NetState.FlushAll();
                    NetState.ProcessDisposedQueue();

                    Slice?.Invoke();

                    if (sample++ < sampleInterval)
                    {
                        continue;
                    }

                    sample = 0;
                    now = DateTime.UtcNow.Ticks;
                    _CyclesPerSecond[_CycleIndex++ % _CyclesPerSecond.Length] = ticksPerSecond / (now - last);
                    last = now;
                }
            }
            catch (Exception e)
            {
                CurrentDomain_UnhandledException(null, new UnhandledExceptionEventArgs(e, true));
            }
        }

        public static string Arguments
        {
            get
            {
                StringBuilder sb = new StringBuilder();

                if (Debug)
                {
                    Utility.Separate(sb, "-debug", " ");
                }

                if (!Service)
                {
                    Utility.Separate(sb, "-noservice", " ");
                }

                if (Profiling)
                {
                    Utility.Separate(sb, "-profile", " ");
                }

                if (HaltOnWarning)
                {
                    Utility.Separate(sb, "-haltonwarning", " ");
                }

                if (LocalHost)
                {
                    Utility.Separate(sb, "-localhost", " ");
                }

                if (VBdotNET)
                {
                    Utility.Separate(sb, "-vb", " ");
                }

                return sb.ToString();
            }
        }

        public static int GlobalUpdateRange { get; set; }
        public static int GlobalMaxUpdateRange { get; set; }
        public static int GlobalRadarRange { get; set; }

        private static int m_ItemCount, m_MobileCount;

        public static int ScriptItems => m_ItemCount;
        public static int ScriptMobiles => m_MobileCount;

        public static void VerifySerialization()
        {
            m_ItemCount = 0;
            m_MobileCount = 0;
            VerifySerialization(Assembly);
            /*VerifySerialization(Assembly.GetCallingAssembly());

            foreach (Assembly a in ScriptCompiler.Assemblies)
            {
                VerifySerialization(a);
            }*/
        }

        private static Type[] SerialTypeArray { get; } = { typeof(Serial) };

        private static void VerifyType(Type t)
        {
            bool isItem = t.IsSubclassOf(typeof(Item));

            if (isItem || t.IsSubclassOf(typeof(Mobile)))
            {
                if (isItem)
                {
                    Interlocked.Increment(ref m_ItemCount);
                }
                else
                {
                    Interlocked.Increment(ref m_MobileCount);
                }

                StringBuilder warningSb = null;

                try
                {
                    /*
					if( isItem && t.IsPublic && !t.IsAbstract )
					{
						ConstructorInfo cInfo = t.GetConstructor( Type.EmptyTypes );

						if( cInfo == null )
						{
							if (warningSb == null)
								warningSb = new StringBuilder();

							warningSb.AppendLine("       - No zero paramater constructor");
						}
					}*/

                    if (t.GetConstructor(SerialTypeArray) == null)
                    {
                        warningSb = new StringBuilder();

                        warningSb.AppendLine("       - No serialization constructor");
                    }

                    if (t.GetMethod("Serialize", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly) == null)
                    {
                        if (warningSb == null)
                        {
                            warningSb = new StringBuilder();
                        }

                        warningSb.AppendLine("       - No Serialize() method");
                    }

                    if (t.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly) == null)
                    {
                        if (warningSb == null)
                        {
                            warningSb = new StringBuilder();
                        }

                        warningSb.AppendLine("       - No Deserialize() method");
                    }

                    if (warningSb != null && warningSb.Length > 0)
                    {
                        Console.WriteLine("Warning: {0}\n{1}", t, warningSb.ToString());
                    }
                }
                catch
                {
                    Console.WriteLine("Warning: Exception in serialization verification of type {0}", t);
                }
            }
        }

        private static void VerifySerialization(Assembly a)
        {
            if (a != null)
            {
                Parallel.ForEach(a.GetTypes(), VerifyType);
            }
        }
    }

    public class FileLogger : TextWriter, IDisposable
    {
        public const string DateFormat = "[MMMM dd HH:mm:ss.f]: ";

        private bool _NewLine;

        public string FileName { get; private set; }

        public FileLogger(string file)
            : this(file, false)
        {
        }

        public FileLogger(string file, bool append)
        {
            FileName = file;
            using (StreamWriter writer = new StreamWriter(new FileStream(FileName, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read)))
            {
                writer.WriteLine(">>>Logging started on {0}.", Core.MistedDateTime.ToString("f")); //f = Tuesday, April 10, 2001 3:51 PM 
            }
            _NewLine = true;
        }

        public override void Write(char ch)
        {
            using (StreamWriter writer = new StreamWriter(new FileStream(FileName, FileMode.Append, FileAccess.Write, FileShare.Read)))
            {
                if (_NewLine)
                {
                    writer.Write(Core.MistedDateTime.ToString(DateFormat));
                    _NewLine = false;
                }
                writer.Write(ch);
            }
        }

        public override void Write(string str)
        {
            using (StreamWriter writer = new StreamWriter(new FileStream(FileName, FileMode.Append, FileAccess.Write, FileShare.Read)))
            {
                if (_NewLine)
                {
                    writer.Write(Core.MistedDateTime.ToString(DateFormat));
                    _NewLine = false;
                }
                writer.Write(str);
            }
        }

        public override void WriteLine(string line)
        {
            using (StreamWriter writer = new StreamWriter(new FileStream(FileName, FileMode.Append, FileAccess.Write, FileShare.Read)))
            {
                if (_NewLine)
                {
                    writer.Write(Core.MistedDateTime.ToString(DateFormat));
                }

                writer.WriteLine(line);
                _NewLine = true;
            }
        }

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
    }

    internal static class NativeMethods
    {
        /// 

        /// Reboot the computer
        /// 

        public static void Reboot()
        {
            IntPtr tokenHandle = IntPtr.Zero;

            try
            {
                // get process token
                if (!OpenProcessToken(Process.GetCurrentProcess().Handle,
                    TOKEN_QUERY | TOKEN_ADJUST_PRIVILEGES,
                    out tokenHandle))
                {
                    Console.WriteLine($"Machine Reboot: Failed to open process token handle - {Marshal.GetLastWin32Error()}");
                }

                // lookup the shutdown privilege
                TOKEN_PRIVILEGES tokenPrivs = new TOKEN_PRIVILEGES();
                tokenPrivs.PrivilegeCount = 1;
                tokenPrivs.Privileges = new LUID_AND_ATTRIBUTES[1];
                tokenPrivs.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;

                if (!LookupPrivilegeValue(null,
                    SE_SHUTDOWN_NAME,
                    out tokenPrivs.Privileges[0].Luid))
                {
                    Console.WriteLine($"Failed to open lookup shutdown privilege - {Marshal.GetLastWin32Error()}");
                }

                // add the shutdown privilege to the process token
                if (!AdjustTokenPrivileges(tokenHandle,
                    false,
                    ref tokenPrivs,
                    0,
                    IntPtr.Zero,
                    IntPtr.Zero))
                {
                    Console.WriteLine($"Machine Reboot: Failed to adjust process token privileges - {Marshal.GetLastWin32Error()}");
                }

                // reboot
                if (!ExitWindowsEx(ExitWindows.Reboot,
                        ShutdownReason.MajorApplication |
                ShutdownReason.MinorInstallation |
                ShutdownReason.FlagPlanned))
                {
                    Console.WriteLine($"Machine Reboot: Failed to reboot system - {Marshal.GetLastWin32Error()}");
                }
            }
            finally
            {
                // close the process token
                if (tokenHandle != IntPtr.Zero)
                {
                    CloseHandle(tokenHandle);
                }
            }
        }

        // everything from here on is from pinvoke.net

        [Flags]
        private enum ExitWindows : uint
        {
            // ONE of the following five:
            LogOff = 0x00,
            ShutDown = 0x01,
            Reboot = 0x02,
            PowerOff = 0x08,
            RestartApps = 0x40,
            // plus AT MOST ONE of the following two:
            Force = 0x04,
            ForceIfHung = 0x10,
        }

        [Flags]
        private enum ShutdownReason : uint
        {
            MajorApplication = 0x00040000,
            MajorHardware = 0x00010000,
            MajorLegacyApi = 0x00070000,
            MajorOperatingSystem = 0x00020000,
            MajorOther = 0x00000000,
            MajorPower = 0x00060000,
            MajorSoftware = 0x00030000,
            MajorSystem = 0x00050000,

            MinorBlueScreen = 0x0000000F,
            MinorCordUnplugged = 0x0000000b,
            MinorDisk = 0x00000007,
            MinorEnvironment = 0x0000000c,
            MinorHardwareDriver = 0x0000000d,
            MinorHotfix = 0x00000011,
            MinorHung = 0x00000005,
            MinorInstallation = 0x00000002,
            MinorMaintenance = 0x00000001,
            MinorMMC = 0x00000019,
            MinorNetworkConnectivity = 0x00000014,
            MinorNetworkCard = 0x00000009,
            MinorOther = 0x00000000,
            MinorOtherDriver = 0x0000000e,
            MinorPowerSupply = 0x0000000a,
            MinorProcessor = 0x00000008,
            MinorReconfig = 0x00000004,
            MinorSecurity = 0x00000013,
            MinorSecurityFix = 0x00000012,
            MinorSecurityFixUninstall = 0x00000018,
            MinorServicePack = 0x00000010,
            MinorServicePackUninstall = 0x00000016,
            MinorTermSrv = 0x00000020,
            MinorUnstable = 0x00000006,
            MinorUpgrade = 0x00000003,
            MinorWMI = 0x00000015,

            FlagUserDefined = 0x40000000,
            FlagPlanned = 0x80000000
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID_AND_ATTRIBUTES
        {
            public LUID Luid;
            public UInt32 Attributes;
        }

        private struct TOKEN_PRIVILEGES
        {
            public UInt32 PrivilegeCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public LUID_AND_ATTRIBUTES[] Privileges;
        }

        private const UInt32 TOKEN_QUERY = 0x0008;
        private const UInt32 TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const UInt32 SE_PRIVILEGE_ENABLED = 0x00000002;
        private const string SE_SHUTDOWN_NAME = "SeShutdownPrivilege";

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ExitWindowsEx(ExitWindows uFlags,
            ShutdownReason dwReason);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle,
            UInt32 DesiredAccess,
            out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LookupPrivilegeValue(string lpSystemName,
            string lpName,
            out LUID lpLuid);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle,
            [MarshalAs(UnmanagedType.Bool)] bool DisableAllPrivileges,
            ref TOKEN_PRIVILEGES NewState,
            UInt32 Zero,
            IntPtr Null1,
            IntPtr Null2);
    }

    public class MultiTextWriter : TextWriter
    {
        private List<TextWriter> m_Streams;

        public MultiTextWriter(params TextWriter[] streams)
        {
            m_Streams = new List<TextWriter>(streams);

            if (m_Streams.Count < 0)
            {
                throw new ArgumentException("You must specify at least one stream.");
            }
        }

        public void Add(TextWriter tw)
        {
            m_Streams.Add(tw);
        }

        public void Remove(TextWriter tw)
        {
            m_Streams.Remove(tw);
        }

        public override void Write(char ch)
        {
            for (int i = 0; i < m_Streams.Count; ++i)
            {
                m_Streams[i].Write(ch);
            }
        }

        public override void WriteLine(string line)
        {
            for (int i = 0; i < m_Streams.Count; ++i)
            {
                m_Streams[i].WriteLine(line);
            }
        }

        public override void WriteLine(string line, params object[] args)
        {
            WriteLine(string.Format(line, args));
        }

        public override void WriteLine(object value)
        {
            if(value != null)
                WriteLine(value.ToString());
        }

        public override Encoding Encoding => Encoding.UTF8;
    }
}
