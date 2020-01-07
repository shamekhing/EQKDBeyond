﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Extensions_Library;
using MathNet.Numerics;
using Stage_Library;
using TimeTagger_Library;
using TimeTagger_Library.Correlation;
using TimeTagger_Library.TimeTagger;
using MathNet.Numerics.Optimization;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using QKD_Library.Synchronization;

namespace QKD_Library
{
    /// <summary>
    /// Entangled state correction with three rotation Plates and one Timetagger
    /// </summary>
    public class StateCorrection
    {
        //#################################################
        //##  P R O P E R T I E S
        //#################################################

        /// <summary>
        /// Number of tagger involved
        /// 0.. only ServerTagger
        /// 1.. only ClientTagger
        /// 2.. both tagger synchronized
        /// </summary>
        public int NumTagger { get; set; } = 0;

        //Downhill Simplex
        public Mode OptimizationMode { get; set; } = Mode.BruteForce;
        public int MaxIterations { get; set; } = 500;
        public double Accurracy_Simplex { get; set; } = 0.3;

        //Bruteforce
        public double Accurracy_BruteForce { get; set; } = 0.3;
        public double[] MinPos { get; set; } = new double[] { 40.2473958333333, 56.4453125, 104.153645833333 };
        public double[] MinPosAcc { get; set; } = new double[] { 45, 45, 45 };

        /// <summary>
        /// Perform initial "brute force" optimization in combined mode
        /// </summary>
        public bool DoInitOptimization { get; set; } = false;
        public int InitNumPoints { get; set; } = 6;
        public double InitRange { get; set; } = 10;
        
        /// <summary>
        /// Integration time in seconds
        /// </summary>
        public int PacketSize { get; set; } = 1000000;

        /// <summary>
        /// Coarse Clock Offset between TimeTaggers
        /// </summary>
        public long TaggerOffset { get; set; } = 0;

        /// <summary>
        /// Peak Integration Time Bin
        /// </summary>
        public ulong TimeBin { get; set; } = 1000;

        /// <summary>
        /// Folder for logging state Correction data. No saving if string is empty
        /// </summary>
        public string LogFolder { get; set; } = "StateCorrection";

        /// <summary>
        /// T = ln(d/e)/ln(n-1) (t n)^3
        /// ------------------------------
        /// T... Overall measurement time
        /// d... Initial Range
        /// e... Target accuracy
        /// n... Number of points per iteration
        /// t... Time for one integration (+movement)
        /// </summary>

        public object StopWatch { get; private set; }

        //#################################################
        //##  P R I V A T E S
        //#################################################

        TaggerSync _taggerSync;
        //Waveplates in order
        //0... QWP
        //1... HWP
        //2... QWP
        List<IRotationStage> _rotationStages;
        private Action<string> _loggerCallback;
        private CancellationTokenSource _cts;

        private string _logFolder = "";
        private string _currLogfile = "";
        private bool writeLog { get => !String.IsNullOrEmpty(_logFolder); }


        private List<(byte cA, byte cB)> _corrConfig2Tagger = new List<(byte cA, byte cB)>
        {
            (0,6),(1,5),(2,8),(3,7) //hv, vh, da, ad
        };

        private List<(byte cA, byte cB)> _corrConfig1Tagger = new List<(byte cA, byte cB)>
        {
            (1,6),(2,5),(3,8),(4,7) //hv, vh, da, ad 
            //(3,8),(4,7) //da, ad
        };


        //#################################################
        //##  E V E N T
        //#################################################

        public event EventHandler<LossFunctionAquiredEventArgs> LossFunctionAquired;
        private void OnLossFunctionAquired(LossFunctionAquiredEventArgs e)
        {
            LossFunctionAquired?.Raise(this, e);  
        }

        public event EventHandler<OptimizationCompleteEventArgs> OptimizationComplete;
        private void OnOptimizationComplete(OptimizationCompleteEventArgs e)
        {
            OptimizationComplete?.Raise(this, e);
        }

        //#################################################
        //##  E N U M E R A T O R 
        //#################################################

        public enum Mode
        {
            DownhillSimplex,
            BruteForce,
            Combined
        }

        //#################################################
        //##  C O N S T R U C T O R
        //#################################################
        public StateCorrection(TaggerSync taggerSync, List<IRotationStage> rotationStages, Action<string> loggerCallback = null)
        {
            _taggerSync = taggerSync;
            _rotationStages = rotationStages;
            _loggerCallback = loggerCallback;
        }

        public async Task StartOptimizationAsync()
        {
            if( _rotationStages.Any( r => r==null || !r.StageReady) )
            {
                WriteLog("Rotation stages not ready");
                return;
            }

            _cts = new CancellationTokenSource();

            if(!String.IsNullOrEmpty(LogFolder))
            {
               _logFolder = Directory.CreateDirectory(LogFolder + "_" + DateTime.Now.ToString("yyyy_MM_dd_HH_mm_ss")).FullName;
            }
                 
            WriteLog($"Starting state correction with Packetsize {PacketSize}");

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            await Task.Run(() => DoOptimize(_cts.Token));

            WriteLog($"State correction complete in {stopwatch.Elapsed}");
        }

        private void DoOptimize(CancellationToken ct)
        {
            //Initial Optimization for initial guess;
                       
            Stopwatch stopwatch = new Stopwatch();

            WriteLog("-------------------------------------");
            _currLogfile = Path.Combine(_logFolder, $"Init_Optimization.txt");


            if (DoInitOptimization && OptimizationMode != Mode.BruteForce)
            {
                WriteLog($"Initial Optimization | n={InitNumPoints} | range={InitRange}", true);
                stopwatch.Restart();

                MinPos = GetOptimumPositions(MinPos, InitNumPoints, InitRange, ct);
                MinPosAcc = Enumerable.Repeat(InitRange / (InitNumPoints - 1),MinPosAcc.Length).ToArray();
                stopwatch.Stop();
                WriteLog($"Iteration done in {stopwatch.Elapsed} | Positions: ({MinPos[0]},{MinPos[1]},{MinPos[2]})", true);
            }

            if (ct.IsCancellationRequested) return;

            switch(OptimizationMode)
            {
                case Mode.DownhillSimplex:
                    DownhillSimplex(ct);
                    break;

                case Mode.BruteForce:
                    //Bisect until Accuracy is reached
                    if (DoInitOptimization) BruteForce(InitRange / (InitNumPoints - 1), ct);
                    else BruteForce(InitRange, ct);
                    break;

                case Mode.Combined:
                    double finetune_range = 10;
                    DownhillSimplex(ct);
                    if (ct.IsCancellationRequested) _cts = new CancellationTokenSource(); //Reset cancellation token
                    BruteForce(finetune_range, _cts.Token);
                    break;
            }

        }

        private void BruteForce(double range, CancellationToken ct)
        {
            //Bisect until Accuracy is reached
            double Range = range;
            Stopwatch stopwatch = new Stopwatch();

            int iteration = 1;

            WriteLog($"Starting Brute Force optimization with target accuracy = {Accurracy_BruteForce} deg",true);

            while (Range >= Accurracy_BruteForce)
            {
                int n = 3;

                WriteLog("-------------------------------------");
                _currLogfile = Path.Combine(_logFolder, $"Iteration_{iteration:D3}.txt");
                WriteLog($"Iteration {iteration} | n={n} | range={Range}", true);

                stopwatch.Restart();

                MinPos = GetOptimumPositions(MinPos, n, Range, ct);
                if (ct.IsCancellationRequested) break;
                Range = Range / 2;

                stopwatch.Stop();

                WriteLog($"Iteration {iteration} done in {stopwatch.Elapsed} | Positions: ({MinPos[0]},{MinPos[1]},{MinPos[2]})", true);

                iteration++;
            }

            //Move stages to optimum position
            WriteLog($"Moving stages to optimum position ({ MinPos[0]},{ MinPos[1]},{ MinPos[2]})");
            _rotationStages[0].Move_Absolute(MinPos[0]);
            _rotationStages[1].Move_Absolute(MinPos[1]);
            _rotationStages[2].Move_Absolute(MinPos[2]);

        }

        private void DownhillSimplex(CancellationToken ct)
        {
            //Nelder Mead Sigleton Minimization
            double[] min_pos_tmp = new double[] { 0, 0, 0 };
            double minCost = 1;

            _currLogfile = Path.Combine(_logFolder, $"NelderMead_Minimization.txt");
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Restart();

            WriteLog($"Starting Nelder Mead Singleton Minimization with MaxIterations = {MaxIterations}, Convergence Criterium = {Accurracy_Simplex}", true);

            Func<Vector<double>, double> loss_func = (Vector<double> p) =>
            {
                ct.ThrowIfCancellationRequested(); //Exit by throwing exception

                Task taskpos1 = Task.Run(() => _rotationStages[0].Move_Absolute(p[0]));
                Task taskpos2 = Task.Run(() => _rotationStages[1].Move_Absolute(p[1]));
                Task taskpos3 = Task.Run(() => _rotationStages[2].Move_Absolute(p[2]));

                Task.WhenAll(taskpos1, taskpos2, taskpos3).GetAwaiter().GetResult();

                var loss = GetLossFunction();

                WriteLog($"Position Nr.:({p[0]:F3},{p[1]:F3},{p[2]:F3}): {loss.val:F4} ({loss.err:F4}, {100 * loss.err / loss.val:F1}%)", true);
                
                //Record minimum value
                if(loss.val<minCost)
                {
                    minCost = loss.val;
                    min_pos_tmp = new double[] { p[0], p[1], p[2] };
                }

                return loss.val;
            };

            IObjectiveFunction obj_function = ObjectiveFunction.Value(loss_func);
            Vector<double> init_guess = new DenseVector(MinPos);
            Vector<double> init_perturb = new DenseVector(new double[] { MinPosAcc[0], MinPosAcc[1], MinPosAcc[2] });
            NelderMeadSimplex solver = new NelderMeadSimplex(Accurracy_Simplex, MaxIterations);

            MinimizationResult solver_result = null;

            try
            {
                 solver_result = solver.FindMinimum(obj_function, init_guess, init_perturb);
            }
            catch(OperationCanceledException) //Cancelled by token
            {

            }
            

            stopwatch.Stop();

            switch (solver_result?.ReasonForExit)
            {
                case ExitCondition.Converged:
                    WriteLog($"Minimization converged with {solver_result.Iterations} iterations in {stopwatch.Elapsed} at optimum position ({MinPos[0]:F3},{MinPos[1]:F3},{MinPos[2]:F3})", true);

                    MinPos = solver_result.MinimizingPoint.ToArray();

                    WriteLog($"Moving to optimum position ({MinPos[0]:F3},{MinPos[1]:F3},{MinPos[2]:F3})");

                    //Write new initial perturbations
                    MinPosAcc[0] = MinPosAcc[1] = MinPosAcc[2] = Accurracy_Simplex * 10;

                    //Move stages to optimum position
                    _rotationStages[0].Move_Absolute(MinPos[0]);
                    _rotationStages[1].Move_Absolute(MinPos[1]);
                    _rotationStages[2].Move_Absolute(MinPos[2]);
                    break;

                case ExitCondition.ExceedIterations:
                    WriteLog($"Maximum iterations ({MaxIterations}) exeeded.",true);
                    break;

                case null: //Cancelled by token
                    WriteLog("Downhill simplex cancelled",true);

                    MinPos = min_pos_tmp;
                    WriteLog($"Moving to optimum position ({MinPos[0]:F3},{MinPos[1]:F3},{MinPos[2]:F3})");

                    //Move stages to optimum position
                    _rotationStages[0].Move_Absolute(MinPos[0]);
                    _rotationStages[1].Move_Absolute(MinPos[1]);
                    _rotationStages[2].Move_Absolute(MinPos[2]);
                    break;

                default:
                    WriteLog($"Other exit reason: {Enum.GetName(typeof(ExitCondition), solver_result.ReasonForExit)}", true);
                    break;
            }
        }

        public void StopCorrection()
        {
            _cts.Cancel();
        }

        private double[] GetOptimumPositions(double[] StartPos, int num_points, double range, CancellationToken ct)
        {
            double[] opt_pos = new double[] { 0, 0, 0 };

            if(_rotationStages.Count != 3)
            {
                WriteLog("Error: Number of rotation stages has to be 3");
                return opt_pos;
            }

            //Create position sets for 3 Lambda plates
            List<double[]> positions = new List<double[]>
            {
                Generate.LinearSpaced(num_points, StartPos[0] - range / 2, StartPos[0] + range / 2),
                Generate.LinearSpaced(num_points, StartPos[1] - range / 2, StartPos[1] + range / 2),
                Generate.LinearSpaced(num_points, StartPos[2] - range / 2, StartPos[2] + range / 2),
            };

            (double val, double err) cost, cost_min = (1.0,0);
            int iteration = 1;
            int totalIterations = (int) Math.Pow(num_points, 3);

            (int i0, int i1, int i2) min_indices = (0, 0, 0);
            
            //Main loop
            for (int i0=0; i0<positions[0].Length; i0++)
            {
                for (int i1 = 0; i1 < positions[0].Length; i1++)
                {
                    for (int i2 = 0; i2 < positions[0].Length; i2++)
                    {

                        if (ct.IsCancellationRequested) return StartPos;

                        //Position rotation stages
                        double p0 = positions[0][i0];
                        double p1 = positions[1][i1];
                        double p2 = positions[2][i2];

                        Task taskpos1 = Task.Run(() => _rotationStages[0].Move_Absolute(p0));
                        Task taskpos2 = Task.Run(() => _rotationStages[1].Move_Absolute(p1));
                        Task taskpos3 = Task.Run(() => _rotationStages[2].Move_Absolute(p2));

                        Task.WhenAll(taskpos1, taskpos2, taskpos3).GetAwaiter().GetResult();

                        //Get loss function value
                        cost = GetLossFunction();

                        if (cost.val+(cost.err/4) < cost_min.val-(cost_min.err/4))
                        {
                            min_indices = (i0, i1, i2);
                            cost_min = cost;
                        }
                                       
                        WriteLog($"Position Nr.{iteration}/{totalIterations} :({p0:F2},{p1:F2},{p2:F2}): {cost.val:F4} ({cost.err:F4}, {100*cost.err/cost.val:F1}%)",true);

                        iteration++;
                    }
                }                  
            }

            opt_pos = new double[] { positions[0][min_indices.i0], positions[1][min_indices.i1], positions[2][min_indices.i2] };

            WriteLog($"Minimum: {cost_min.val:F4}({cost_min.err:F4},  {100 * cost_min.err / cost_min.val:F1}%)",true);

            return opt_pos;
        }
        
        /// <summary>
        /// Returns relative middle peak area of combined histogram
        /// </summary>
        /// <returns></returns>
        private (double val, double err) GetLossFunction()
        {
            ulong timewindow = 100000;
            Histogram hist = new Histogram(NumTagger == 2 ? _corrConfig2Tagger : _corrConfig1Tagger, timewindow, hist_resolution:256);
            Kurolator corr = new Kurolator(new List<CorrelationGroup> { hist }, 100000);

            //Collect timetags
            TimeTags tt1, tt2;

            if(NumTagger==2)
            {
                TaggerSyncResults syncRes = _taggerSync.GetSyncedTimeTags(PacketSize);

                if (!syncRes.IsSync) throw new Exception("Tagger synchronization error");

                tt1 = syncRes.TimeTags_Alice;
                tt2 = syncRes.CompTimeTags_Bob;
            }
            else
            {
                tt1 = tt2 =_taggerSync.GetSingleTimeTags(NumTagger, packetSize: PacketSize);
            }
     
            corr.AddCorrelations(tt1,tt2,0);

            List<Peak> peaks = hist.GetPeaks(6250, 0.1, true, TimeBin);
            var loss = hist.GetRelativeMiddlePeakArea();

            OnLossFunctionAquired(new LossFunctionAquiredEventArgs(hist.Histogram_X, hist.Histogram_Y,loss, peaks));

            return loss;
        }

        private void WriteLog(string msg, bool doLog=false)
        {
            _loggerCallback?.Invoke("State correction: " + msg);
            if(doLog && !String.IsNullOrEmpty(_currLogfile)) File.AppendAllLines(_currLogfile, new string[] { msg });
        }

    }

    public class LossFunctionAquiredEventArgs : EventArgs
    {
        public long[] HistogramX { get; private set; }
        public long[] HistogramY { get; private set; }
        public (double val,double err) Loss { get; private set; }
        public List<Peak> Peaks { get; private set; }

        public LossFunctionAquiredEventArgs(long[] histX, long[] histY, (double,double) loss, List<Peak> peaks)
        {
            HistogramX = histX;
            HistogramY = histY;
            Loss = loss;
            Peaks = peaks;
        }
    }
    
    public class OptimizationCompleteEventArgs : EventArgs
    {

    }
}
