﻿using Controller.XYStage;
using Extensions_Library;
using QKD_Library;
using QKD_Library.Characterization;
using QKD_Library.Synchronization;
using SecQNet;
using Stage_Library;
using Stage_Library.NewPort;
using Stage_Library.Thorlabs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using TimeTagger_Library;
using TimeTagger_Library.Correlation;
using TimeTagger_Library.TimeTagger;

namespace EQKDServer.Models
{
    public class EQKDServerModel
    {
        //-----------------------------------
        //---- C O N S T A N T S 
        //-----------------------------------

        const double REMOVEDPOS = 40;
        const double INSERTEDPOS = 91.5;

        private bool EXTERNAL_CLOCK = true;

        //-----------------------------------
        //----  P R I V A T E  F I E L D S
        //-----------------------------------

        private Action<string> _loggerCallback;
        private Func<string, string, int> _userprompt;
        private ServerSettings _currentServerSettings = new ServerSettings();
        string _serverSettings_XMLFilename = "ServerSettings.xml";
        CancellationTokenSource _cts;

        List<byte> _secureKeys = new List<byte>();
        List<byte> _bobKeys = new List<byte>();
        
        //-----------------------------------
        //----  P R O P E R T I E S
        //-----------------------------------

        //Synchronization and State correction
        public TaggerSync AliceBobSync { get; private set; }
        public StateCorrection FiberCorrection { get; private set; }
        public DensityMatrix AliceBobDensMatrix { get; private set; }
        public bool IsSyncActive { get; private set; } = false;

        //SecQNet Connection
        public int PacketSize { get; set; } = 100000;
        public long PacketTImeSpan { get; set; } = 2000000000000;
        public bool StabilizeCountrate { get; set; } = true;
        public SecQNetServer SecQNetServer { get; private set; }

        //Time Tagger
        public ITimeTagger ServerTimeTagger { get; set; }
        public ITimeTagger ClientTimeTagger { get; set; }

        //Key generation
        public ulong Key_TimeBin { get; set; } = 1000;
        public string KeyFolder { get; set; } = "Key";

        public QKey AliceKey { get; private set; } = new QKey()
        {
            RectZeroChan =0,
            DiagZeroChan=2
        };
        //Rotation Stages
        public SMC100Controller _smcController { get; private set; }
        public SMC100Stage _HWP_A { get; private set; }
        public KPRM1EStage _QWP_A { get; private set; }
        public SMC100Stage _HWP_B { get; private set; }
        public KPRM1EStage _HWP_C { get; private set; }
        public KPRM1EStage _QWP_B { get; private set; }
        public KPRM1EStage _QWP_C { get; private set; }
        public KPRM1EStage _QWP_D { get; private set; }
        public KBD101Stage PolarizerStage { get; private set; }


        //-----------------------------------
        //----  E V E N T S
        //-----------------------------------

        public event EventHandler<ServerConfigReadEventArgs> ServerConfigRead;
        private void OnServerConfigRead(ServerConfigReadEventArgs e)
        {
            ServerConfigRead?.Raise(this, e);
        }

        public event EventHandler<KeysGeneratedEventArgs> KeysGenerated;
        private void OnKeysGenerated(KeysGeneratedEventArgs e)
        {
            KeysGenerated?.Raise(this, e);
        }

        //-----------------------------------
        //---- C O N S T R U C T O R
        //-----------------------------------
        public EQKDServerModel(Action<string> loggercallback, Func<string,string,int> userprompt)
        {
            _loggerCallback = loggercallback;
            _userprompt = userprompt;

            SecQNetServer = new SecQNetServer(_loggerCallback);

            //Instanciate TimeTaggers
            HydraHarp hydra = new HydraHarp(_loggerCallback)
            {
                DiscriminatorLevel = 200,
                SyncDivider = 8,
                SyncDiscriminatorLevel = 200,
                MeasurementMode = HydraHarp.Mode.MODE_T2,
                ClockMode = EXTERNAL_CLOCK ? HydraHarp.Clock.External : HydraHarp.Clock.Internal,
                PackageMode = TimeTaggerBase.PMode.ByEllapsedTime
            };
            hydra.Connect(new List<long> { 0, -3820, -31680, -31424 });

            SITimeTagger sitagger = new SITimeTagger(_loggerCallback)
            {
                RefChan = EXTERNAL_CLOCK ? 1 : 0,
                SyncDiscriminatorVoltage = 0.2,
                RefChanDivider=100,
                SyncRate=10000000,
                PackageMode = TimeTaggerBase.PMode.ByEllapsedTime
            };

            long testoffs = 128;
            long OIC_Alice750m = -3493504;
            sitagger.Connect(new List<long> { 0+OIC_Alice750m, 0+OIC_Alice750m, -75648+OIC_Alice750m, -78208+OIC_Alice750m, 2176 + testoffs, 2176 + testoffs, 1164 + testoffs, 2176 + testoffs });


            NetworkTagger nwtagger = new NetworkTagger(_loggerCallback,SecQNetServer);

            ServerTimeTagger = hydra;
            ClientTimeTagger = nwtagger;

            //Instanciate and connect rotation Stages
            _smcController = new SMC100Controller(_loggerCallback);
            _smcController.Connect("COM4");
            List<SMC100Stage> _smcStages = _smcController.GetStages();

            _HWP_A = _smcStages[1];
            _HWP_B = _smcStages[2];

            if (_HWP_A != null)
            {
                _HWP_A.Offset = 45.01+90;
            }

            if (_HWP_B != null)
            {
                _HWP_B.Offset = 100.06;
            }


            //_HWP_C = new KPRM1EStage(_loggerCallback);
            //_HWP_C.Connect("27254524");
            //_HWP_C.Offset = 58.5+90;

            _QWP_A = new KPRM1EStage(_loggerCallback);
            _QWP_A.Connect("27254310");
            _QWP_A.Offset = 35.15;

            _QWP_B = new KPRM1EStage(_loggerCallback);
            _QWP_B.Connect("27504148");
            _QWP_B.Offset = 63.84;

            //_QWP_C = new KPRM1EStage(_loggerCallback);
            //_QWP_C.Connect("27003707");
            //_QWP_C.Offset = 27.3;

            //_QWP_D = new KPRM1EStage(_loggerCallback);
            //_QWP_D.Connect("27254574");
            //_QWP_D.Offset = 33.15 + 90; //FAST AXIS WRONG ON THORLABS PLATE --> +90°!

            ////-19.53,31.97,-40.94
            //_QWP_A.Move_Absolute(0);
            //_HWP_B.Move_Absolute(0);
            //_QWP_B.Move_Absolute(0);

            //_QWP_A.Move_Absolute(-25.5);
            //_HWP_B.Move_Absolute(24.19);
            //_QWP_B.Move_Absolute(-50.81);

            PolarizerStage = new KBD101Stage(_loggerCallback);
            PolarizerStage.Connect("28250918");
            
            AliceBobSync = new TaggerSync(ServerTimeTagger, ClientTimeTagger, _loggerCallback, _userprompt, TriggerShutter, PolarizerControl);
            FiberCorrection = new StateCorrection(AliceBobSync, new List<IRotationStage> { _QWP_A, _HWP_B, _QWP_B }, _loggerCallback);
            //AliceBobDensMatrix = new DensityMatrix(AliceBobSync, _HWP_A, _QWP_A, _HWP_B, _QWP_B, _loggerCallback);//Before fiber
            AliceBobDensMatrix = new DensityMatrix(AliceBobSync, _HWP_A, _QWP_A, _HWP_B, _QWP_C, _loggerCallback); //in Alice/Bob Boxes


            //Create key folder
            if (!Directory.Exists(KeyFolder)) Directory.CreateDirectory(KeyFolder);
        }

        private void PolarizerControl(bool status)
        {
            switch (status)
            {
                case true:
                    PolarizerStage.Move_Absolute(INSERTEDPOS);
                    break;
                case false:
                    PolarizerStage.Move_Absolute(REMOVEDPOS);
                    break;
                default:
                    break;
            }
        }
        private void TriggerShutter()
        {
            _QWP_A.SetOutput(true);
            Thread.Sleep(100);
            _QWP_A.SetOutput(false);
        }

        //--------------------------------------
        //----  M E T H O D S
        //--------------------------------------
        
        public async Task TestClock()
        {
            WriteLog("Start Testing Clocks...");
            SecQNetServer.ObscureClientTimeTags = false;
            await Task.Run(() => AliceBobSync.TestClock(PacketSize));
        }

        public async Task StartSynchronizeAsync()
        {
            if (IsSyncActive) return;

            _cts = new CancellationTokenSource();

            //Deactivate client side basis obscuring
            SecQNetServer.ObscureClientTimeTags = false;

            WriteLog("Synchronisation started");

            IsSyncActive = true;

                               
            await Task.Run(() =>
          {
              //while (!_cts.Token.IsCancellationRequested)
              //{
              TaggerSyncResults syncClockRes = AliceBobSync.GetSyncedTimeTags(packetSize: PacketSize, packetTimeSpan: PacketTImeSpan);


              if(syncClockRes.IsSync)
              {
                  List<(byte cA, byte cB)> _clockChanConfig = new List<(byte cA, byte cB)>
                {
                    //Clear Basis
                    (0,5),(0,6),(0,7),(0,8),
                    (1,5),(1,6),(1,7),(1,8),
                    (2,5),(2,6),(2,7),(2,8),
                    (3,5),(3,6),(3,7),(3,8),

                   // Obscured Basis
                    //(0,oR),(0,oD),(1,oR),(1,oD),(2,oR),(2,oD),(3,oR),(3,oD)
                };

                  Histogram trackingHist = new Histogram(_clockChanConfig, 200000, 512);

                  Kurolator trackingKurolator = new Kurolator(new List<CorrelationGroup> { trackingHist }, 200000);
                  trackingKurolator.AddCorrelations(syncClockRes.TimeTags_Alice, syncClockRes.CompTimeTags_Bob, 0);
              }

              //File.AppendAllLines("SyncTest.txt", new string[] { syncClockRes.NewLinearDriftCoeff + "\t" + syncClockRes.GroundLevel +"\t" + syncClockRes.Sigma });

              //    if (syncClockRes.IsClocksSync)
              //    {
              //        SyncCorrResults syncCorrres = TaggerSynchronization.SyncCorrelationAsync(syncClockRes.TimeTags_Alice, syncClockRes.CompTimeTags_Bob).GetAwaiter().GetResult();
              //    }
              //}

          });

            IsSyncActive = false;

            WriteLog("Synchronisation Stopped");
        }
        
        public async Task StartFiberCorrectionAsync()
        {
            SecQNetServer.ObscureClientTimeTags = false;

            //await AliceBobDensMatrix.MeasurePeakAreasAsync();

            await FiberCorrection.StartOptimizationAsync();
        }


        public void StopKeyGeneration()
        {
            _cts?.Cancel();
        }

        public double _getAverageCountrate()
        {
            return ServerTimeTagger.GetCountrate().Average();
        }

        public async Task StartKeyGenerationAsync()
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            SecQNetServer.ObscureClientTimeTags = true;

            //XYStabilizer crStabilizer = new XYStabilizer(null, null, _getAverageCountrate, loggerCallback: _loggerCallback)
            //{
            //    SetPoint = _getAverageCountrate(),
            //    SPTolerance = 10000,
            //    XYStep = 500E-9,
            //};
       
            WriteLog("Starting secure key generation");

            await Task.Run(() =>
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    //if (!crStabilizer.SetpointReached) crStabilizer.Correct();
                    
                    switch (ClientTimeTagger)
                    {
                        case NetworkTagger nwtag:
                            _generateKeysNetworkAsync();
                            break;

                        default:
                            _generateKeysLocalAsync();
                            break;
                    }
                }
            });

            WriteLog("Secure key generation stopped.");
        }

        private void _generateKeysNetworkAsync()
        {
            AliceKey.FileName = Path.Combine(KeyFolder, "Key_Alice");
            string stats_file = Path.Combine(KeyFolder,"Stats.txt");

            if (!File.Exists(stats_file)) File.WriteAllLines(stats_file, new string[] { "Time \t Rate \t GlobalTimeOffset \t PacketOverlap" });

            //Get Key Correlations
            TaggerSyncResults syncRes = AliceBobSync.GetSyncedTimeTags(packetSize: PacketSize, packetTimeSpan: PacketTImeSpan);

            if (!syncRes.IsSync)
            {
                WriteLog("Not in sync, no keys generated");
                return;
            }
                 
            var key_entries = AliceKey.GetKeyEntries(syncRes.TimeTags_Alice, syncRes.CompTimeTags_Bob);
            AliceKey.AddKey(key_entries);
            //Register key at Bob                
            TimeTags bobSiftedTimeTags = new TimeTags(new byte[] { }, key_entries.Select(fe => (long)fe.index_bob).ToArray());
            //Send sifted tags to bob
            SecQNetServer.SendSiftedTimeTags(bobSiftedTimeTags);

            //Statistics
            double overlap = Kurolator.GetOverlapRatio(syncRes.TimeTags_Alice, syncRes.CompTimeTags_Bob);
            double rate = AliceKey.GetRate(syncRes.TimeTags_Alice, key_entries);
            WriteLog($"{key_entries.Count} keys generated with a raw rate of {rate:F3} keys/s");
            File.AppendAllLines(stats_file, new string[] { DateTime.Now.ToString()+"\t"+rate.ToString("F2")+"\t"+
                                                           AliceBobSync.GlobalClockOffset.ToString()+"\t"+overlap.ToString("F2") });
        }

        private void _generateKeysLocalAsync()
        { 
            List<byte> newAliceKeys = new List<byte>();
            List<byte> newBobKeys = new List<byte>();

            TimeTags ttA = new TimeTags();
            TimeTags ttB = new TimeTags();

            List<(byte cA, byte cB)> keyCorrConfig = EXTERNAL_CLOCK
                ? new List<(byte cA, byte cB)>
                    {
                        //Rectilinear
                        (0,5),(0,6),(1,5),(1,6),
                        //Diagonal
                        (2,7),(2,8),(3,7),(3,8)
                    }
                : new List<(byte cA, byte cB)>
                    {
                        //Rectilinear
                        (1,5),(1,6),(2,5),(2,6),
                        //Diagonal
                        (3,7),(3,8),(4,7),(4,8)
                    };

            Histogram key_hist = new Histogram(keyCorrConfig, Key_TimeBin);
            Kurolator key_corr = new Kurolator(new List<CorrelationGroup> { key_hist }, Key_TimeBin);
            long tspan = 0;                  

            switch(EXTERNAL_CLOCK)
            {
                //Two timertaggers (Hydra + SI)
                case true:
                    TaggerSyncResults syncRes = AliceBobSync.GetSyncedTimeTags(packetSize: PacketSize, packetTimeSpan: PacketTImeSpan);

                    if (!syncRes.IsSync)
                    {
                        WriteLog("Not in sync, no keys generated");
                        return;
                    }

                    ttA = syncRes.TimeTags_Alice;
                    ttB = syncRes.CompTimeTags_Bob;

                    tspan = Math.Max(ttA.time.Last(), ttB.time.Last()) - Math.Min(ttA.time.First(), ttB.time.First());
                    break;

                //One Timetagger (SI)
                case false:
                    ttA = AliceBobSync.GetSingleTimeTags(0, packetSize: PacketSize, packetTimeSpan: PacketTImeSpan);
                    ttB = ttA;

                    tspan = ttA.time.Last() - ttA.time.First();
                    break;
            }


            key_corr.AddCorrelations(ttA, ttB);

            OnKeysGenerated(new KeysGeneratedEventArgs(key_hist.Histogram_X, key_hist.Histogram_Y));

            //KEY SIFTING            

            //Register key at Alice
            foreach (int i in key_hist.CorrelationIndices.Select(i => i.i1))
            {
                byte act_chan = ttA.chan[i];
                newAliceKeys.Add(act_chan == (EXTERNAL_CLOCK ? 0:1) || act_chan == (EXTERNAL_CLOCK? 2:3) ? (byte)0 : (byte)1);
            };

            //Register key at Bob
            foreach (int i in key_hist.CorrelationIndices.Select(i => i.i2))
            {
                byte act_chan = ttB.chan[i];
                newBobKeys.Add(act_chan == 5 || act_chan == 7 ? (byte)0 : (byte)1);
            };

            //Check QBER
            _secureKeys.AddRange(newAliceKeys);
            _bobKeys.AddRange(newBobKeys);

            int sum_err = 0;
            for (int i = 0; i < _secureKeys.Count; i++)
            {
                if (_secureKeys[i] != _bobKeys[i]) sum_err++;
            }

            //Write to file
            File.AppendAllLines("AliceKey.txt", newAliceKeys.Select(k => k.ToString()));
            File.AppendAllLines("BobKey.txt", newBobKeys.Select(k => k.ToString()));

            double QBER = (double)sum_err / _secureKeys.Count;
            double rate = key_hist.CorrelationIndices.Count / (tspan / 1E12);

            File.AppendAllLines("KeyStats.txt", new string[] { DateTime.Now.ToString() + "," + rate.ToString() + "," + QBER.ToString() });

            WriteLog($"QBER: {QBER:F3} | rate: {rate:F3}");

        }


        public void ReadServerConfig()
        {
            ServerSettings _readSettings = ReadConfigXMLFile(_serverSettings_XMLFilename);
            if (_readSettings == null)
            {
                WriteLog($"Could not read Configuration file '{_serverSettings_XMLFilename}', using default settings");
            }

            _currentServerSettings = _readSettings ?? new ServerSettings();

            //Set configs
            OnServerConfigRead(new ServerConfigReadEventArgs(_currentServerSettings));

        }

        private ServerSettings ReadConfigXMLFile(string filename)
        {
            ServerSettings tmp_settings = null;

            if (!File.Exists(filename)) return tmp_settings;

            try
            {
                FileStream fs = new FileStream(filename, FileMode.OpenOrCreate);
                TextReader tr = new StreamReader(fs);
                XmlSerializer xmls = new XmlSerializer(typeof(ServerSettings));

                tmp_settings = (ServerSettings)xmls.Deserialize(tr);

                tr.Close();
            }
            catch (InvalidOperationException ex) //Thrown by Serialize
            {
                throw new InvalidOperationException("Catched InvalidOperationException: " + ex.Message, ex.InnerException);
            }
            catch (IOException ex) //Thrown by FileStream
            {
                throw new InvalidOperationException("Catched IOException: " + ex.Message, ex.InnerException);
            }

            return tmp_settings;
        }

        public void SaveServerConfig()
        {
            //Get Config Data
            _currentServerSettings.PacketSize = PacketSize;

            _currentServerSettings.LinearDriftCoefficient = AliceBobSync.LinearDriftCoefficient;
            _currentServerSettings.LinearDriftCoeff_NumVar = AliceBobSync.LinearDriftCoeff_NumVar;
            _currentServerSettings.LinearDriftCoeff_Var = AliceBobSync.LinearDriftCoeff_Var;
            _currentServerSettings.TimeWindow = AliceBobSync.ClockSyncTimeWindow;
            _currentServerSettings.TimeBin = AliceBobSync.ClockTimeBin;

            //Write Config file
            SaveConfigXMLFile(_currentServerSettings, _serverSettings_XMLFilename);
        }

        private bool SaveConfigXMLFile(ServerSettings settings, string filename)
        {
            try
            {
                TextWriter tw = new StreamWriter(filename);
                XmlSerializer xmls = new XmlSerializer(settings.GetType());

                xmls.Serialize(tw, _currentServerSettings);

                tw.Close();
            }
            catch (InvalidOperationException ex) //Thrown by Serialize
            {
                throw new InvalidOperationException("Catched InvalidOperationException: " + ex.Message, ex.InnerException);
            }
            catch (IOException ex) //Thrown by FileStream
            {
                throw new InvalidOperationException("Catched IOException: " + ex.Message, ex.InnerException);
            }

            WriteLog("TimeTagger factory options saved in '" + filename + "'.");

            return true;
        }

        private void WriteLog(string message)
        {
            _loggerCallback?.Invoke("EQKD Server: " + message);
        }
    }

//##############################
// E V E N T   A R G U M E N T S
//##############################
    public class ServerConfigReadEventArgs : EventArgs
    {
        public ServerSettings StartConfig { get; private set; }

        public ServerConfigReadEventArgs(ServerSettings _config)
        {
            StartConfig = _config;
        }
    }

    public class KeysGeneratedEventArgs : EventArgs
    {
        public long[] HistogramX { get; private set; }
        public long[] HistogramY { get; private set; }

        public KeysGeneratedEventArgs(long[] histX, long[] histY)
        {
            HistogramX = histX;
            HistogramY = histY;
        }
    }

}
