﻿using NiceHashMiner.Configs;
using NiceHashMiner.Devices;
using NiceHashMiner.Interfaces;
using NiceHashMiner.Miners.Grouping;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Forms;
using NiceHashMiner.Algorithms;
using NiceHashMiner.Benchmarking;
using NiceHashMiner.Stats;
using NiceHashMiner.Switching;
using NiceHashMinerLegacy.Common.Enums;
using Timer = System.Timers.Timer;
using NiceHashMinerLegacy.Common;
using NiceHashMiner.Miners.IntegratedPlugins;

namespace NiceHashMiner.Miners
{
    // combination of MinersManager and MiningSession
    public static class MiningManager
    {
        private const string Tag = "MiningManager";
        private const string DoubleFormat = "F12";

        private static readonly AlgorithmSwitchingManager _switchingManager;

        // session varibles fixed
        private static object _lock = new object();
        private static string _username = DemoUser.BTC;
        private static List<MiningDevice> _miningDevices = new List<MiningDevice>();
        private static Dictionary<string, GroupMiner> _runningGroupMiners = new Dictionary<string, GroupMiner>();

        // assume profitable
        private static bool _isProfitable = true;
        // assume we have internet
        private static bool _isConnectedToInternet = true;
        private static bool _isMiningRegardlesOfProfit => ConfigManager.GeneralConfig.MinimumProfit == 0;

        //// timers 
        //// check internet connection 
        //private readonly Timer _internetCheckTimer;


        public static bool IsMiningEnabled => _miningDevices.Count > 0;

        private static bool IsCurrentlyIdle => !IsMiningEnabled || !_isConnectedToInternet || !_isProfitable;

        public static List<int> GetActiveMinersIndexes()
        {
            var minerIDs = new List<int>();
            if (!IsCurrentlyIdle)
            {
                lock (_lock)
                {
                    foreach (var miner in _runningGroupMiners.Values)
                    {
                        minerIDs.AddRange(miner.DevIndexes);
                    }
                }
            }

            return minerIDs;
        }

        static MiningManager()
        {
            //_username = username;
            _switchingManager = new AlgorithmSwitchingManager();
            _switchingManager.SmaCheck += SwichMostProfitableGroupUpMethod;

            //// init timer stuff
            //// set internet checking
            //_internetCheckTimer = new Timer();
            //_internetCheckTimer.Elapsed += InternetCheckTimer_Tick;
            //_internetCheckTimer.Interval = 1 * 1000 * 60; // every minute

            //if (IsMiningEnabled)
            //{
            //    _internetCheckTimer.Start();
            //}

            //_switchingManager.Start();
        }
        #region Timers stuff

        private static void InternetCheckTimer_Tick(object sender, EventArgs e)
        {
            if (ConfigManager.GeneralConfig.IdleWhenNoInternetAccess)
            {
                _isConnectedToInternet = Helpers.IsConnectedToInternet();
            }
        }
        #endregion Timers stuff
        #region Start/Stop

        public static void StopAllMiners()
        {
            EthlargementIntegratedPlugin.Instance.Stop();
            lock(_lock)
            {
                foreach (var groupMiner in _runningGroupMiners.Values)
                {
                    groupMiner.End();
                }
                _runningGroupMiners.Clear();
            }

            _switchingManager?.Stop();
            ApplicationStateManager.ClearRatesAll();
            //_internetCheckTimer?.Stop();
            // Speed stability anamlyzer was here or deviant algo checker
        }

        #endregion Start/Stop

        public static void UpdateMiningSession(IEnumerable<ComputeDevice> devices, string username)
        {
            _username = username;
            _switchingManager?.Stop();
            // TODO check out if there is a change
            _miningDevices = GroupSetupUtils.GetMiningDevices(devices, true);
            if (_miningDevices.Count > 0)
            {
                GroupSetupUtils.AvarageSpeeds(_miningDevices);
            }
            _switchingManager?.Start();
        }

        public static async Task RestartMiners()
        {
            _switchingManager?.Stop();

            // STOP
            var stopTasks = new List<Task>();
            lock (_lock)
            {
                foreach (var key in _runningGroupMiners.Keys)
                {
                    stopTasks.Add(_runningGroupMiners[key].Stop());
                }
            }
            foreach (var stopTask in stopTasks)
            {
                await stopTask;
            }
            // START
            var startTasks = new List<Task>();
            var miningLocation = StratumService.SelectedServiceLocation;
            lock (_lock)
            {
                foreach (var key in _runningGroupMiners.Keys)
                {
                    startTasks.Add(_runningGroupMiners[key].Start(miningLocation, _username));
                }
            }
            foreach (var startTask in startTasks)
            {
                await startTask;
            }
            _switchingManager?.Start();
        }

        // full of state
        private static bool CheckIfProfitable(double currentProfit, bool log = true)
        {
            // TODO FOR NOW USD ONLY
            var currentProfitUsd = (currentProfit * ExchangeRateApi.GetUsdExchangeRate());
            _isProfitable =
                _isMiningRegardlesOfProfit
                || !_isMiningRegardlesOfProfit && currentProfitUsd >= ConfigManager.GeneralConfig.MinimumProfit;
            if (log)
            {
                Logger.Info(Tag, $"Current global profit = {currentProfitUsd.ToString("F8")} USD/Day");
                if (!_isProfitable)
                {
                    Logger.Info(Tag, $"Current global profit = NOT PROFITABLE, MinProfit: {ConfigManager.GeneralConfig.MinimumProfit.ToString("F8")} USD/Day");
                }
                else
                {
                    var profitabilityInfo = _isMiningRegardlesOfProfit
                        ? "mine always regardless of profit"
                        : ConfigManager.GeneralConfig.MinimumProfit.ToString("F8") + " USD/Day";
                    Logger.Info(Tag, $"Current global profit = IS PROFITABLE, MinProfit: {profitabilityInfo}");
                }
            }

            return _isProfitable;
        }

        private static bool CheckIfShouldMine(double currentProfit, bool log = true)
        {
            // if profitable and connected to internet mine
            var shouldMine = CheckIfProfitable(currentProfit, log) && _isConnectedToInternet;
            if (shouldMine)
            {
                ApplicationStateManager.SetProfitableState(true);
            }
            else
            {
                if (!_isConnectedToInternet)
                {
                    // change msg
                    if (log)
                    {
                        Logger.Info(Tag, $"No internet connection! Not able to min.");
                    }
                    ApplicationStateManager.DisplayNoInternetConnection();
                }
                else
                {
                    ApplicationStateManager.SetProfitableState(false);
                }

                // return don't group
                StopAllMiners();
            }

            return shouldMine;
        }

        private static async void SwichMostProfitableGroupUpMethod(object sender, SmaUpdateEventArgs e)
        {
            await SwichMostProfitableGroupUpMethodTask(sender, e);
        }


        private static void CalculateAndUpdateMiningDevicesProfits(Dictionary<AlgorithmType, double> profits)
        {
            foreach (var device in _miningDevices)
            {
                // update/calculate profits
                device.CalculateProfits(profits);
            }
        }

        private static (double currentProfit, double prevStateProfit) GetCurrentAndPreviousProfits()
        {
            var currentProfit = 0.0d;
            var prevStateProfit = 0.0d;
            foreach (var device in _miningDevices)
            {
                // check if device has profitable algo
                if (device.HasProfitableAlgo())
                {
                    currentProfit += device.GetCurrentMostProfitValue;
                    prevStateProfit += device.GetPrevMostProfitValue;
                }
            }
            return (currentProfit, prevStateProfit);
        }

        private static List<MiningPair> GetProfitableMiningPairs()
        {
            var profitableMiningPairs = new List<MiningPair>();
            foreach (var device in _miningDevices)
            {
                // check if device has profitable algo
                if (!device.HasProfitableAlgo()) continue;
                profitableMiningPairs.Add(device.GetMostProfitablePair());
            }
            return profitableMiningPairs;
        }

        private static async Task SwichMostProfitableGroupUpMethodTask(object sender, SmaUpdateEventArgs e)
        {
#if (SWITCH_TESTING)
            MiningDevice.SetNextTest();
#endif
            CalculateAndUpdateMiningDevicesProfits(e.NormalizedProfits);
            var (currentProfit, prevStateProfit) = GetCurrentAndPreviousProfits();

            // log profits scope
            {
                var stringBuilderFull = new StringBuilder();
                stringBuilderFull.AppendLine("Current device profits:");
                foreach (var device in _miningDevices)
                {
                    var stringBuilderDevice = new StringBuilder();
                    stringBuilderDevice.AppendLine($"\tProfits for {device.Device.Uuid} ({device.Device.GetFullName()}):");
                    foreach (var algo in device.Algorithms)
                    {
                        stringBuilderDevice.AppendLine(
                            $"\t\tPROFIT = {algo.CurrentProfit.ToString(DoubleFormat)}" +
                            $"\t(SPEED = {algo.AvaragedSpeed:e5}" +
                            $"\t\t| NHSMA = {algo.CurNhmSmaDataVal:e5})" +
                            $"\t[{algo.AlgorithmStringID}]"
                        );
                        // TODO second paying ratio logging
                        //if (algo is PluginAlgorithm dualAlg && dualAlg.IsDual)
                        //{
                        //    stringBuilderDevice.AppendLine(
                        //        $"\t\t\t\t\t  Secondary:\t\t {dualAlg.SecondaryAveragedSpeed:e5}" +
                        //        $"\t\t\t\t  {dualAlg.SecondaryCurNhmSmaDataVal:e5}"
                        //    );
                        //}
                    }
                    // most profitable
                    stringBuilderDevice.AppendLine(
                        $"\t\tMOST PROFITABLE ALGO: {device.GetMostProfitableString()}, PROFIT: {device.GetCurrentMostProfitValue.ToString(DoubleFormat)}");
                    stringBuilderFull.AppendLine(stringBuilderDevice.ToString());
                }
                Logger.Info(Tag, stringBuilderFull.ToString());
            }

            // check if should mine
            // Only check if profitable inside this method when getting SMA data, cheching during mining is not reliable
            if (CheckIfShouldMine(currentProfit) == false)
            {
                foreach (var device in _miningDevices)
                {
                    device.SetNotMining();
                }
                return;
            }

            // check profit threshold
            Logger.Info(Tag, $"PrevStateProfit {prevStateProfit}, CurrentProfit {currentProfit}");
            if (prevStateProfit > 0 && currentProfit > 0)
            {
                var a = Math.Max(prevStateProfit, currentProfit);
                var b = Math.Min(prevStateProfit, currentProfit);
                //double percDiff = Math.Abs((PrevStateProfit / CurrentProfit) - 1);
                var percDiff = ((a - b)) / b;
                if (percDiff < ConfigManager.GeneralConfig.SwitchProfitabilityThreshold)
                {
                    // don't switch
                    Logger.Info(Tag, $"Will NOT switch profit diff is {percDiff}, current threshold {ConfigManager.GeneralConfig.SwitchProfitabilityThreshold}");
                    // RESTORE OLD PROFITS STATE
                    foreach (var device in _miningDevices)
                    {
                        device.RestoreOldProfitsState();
                    }
                    return;
                }
                Logger.Info(Tag, $"Will SWITCH profit diff is {percDiff}, current threshold {ConfigManager.GeneralConfig.SwitchProfitabilityThreshold}");
            }

            // group new miners 
            var newGroupedMiningPairs = GroupingLogic.GetGroupedMiningPairs(GetProfitableMiningPairs());
            // check newGroupedMiningPairs and running Groups to figure out what to start/stop and keep running
            string[] currentRunningGroups;
            lock (_lock)
            {
                currentRunningGroups = _runningGroupMiners.Keys.ToArray();
            }
            // check which groupMiners should be stopped and which ones should be started and which to keep running
            var noChangeGroupMinersKeys = newGroupedMiningPairs.Where(pair => currentRunningGroups.Contains(pair.Key)).Select(pair => pair.Key).OrderBy(uuid => uuid).ToArray();
            var toStartMinerGroupKeys = newGroupedMiningPairs.Where(pair => !currentRunningGroups.Contains(pair.Key)).Select(pair => pair.Key).OrderBy(uuid => uuid).ToArray();
            var toStopMinerGroupKeys = currentRunningGroups.Where(runningKey => !newGroupedMiningPairs.Keys.Contains(runningKey)).OrderBy(uuid => uuid).ToArray();

            // first stop currently running
            var toStopGroups = new List<GroupMiner>();
            lock (_lock)
            {
                foreach (var stopKey in toStopMinerGroupKeys)
                {
                    toStopGroups.Add(_runningGroupMiners[stopKey]);
                    _runningGroupMiners.Remove(stopKey);
                    
                }
            }
            foreach (var stopGroup in toStopGroups)
            {
                await stopGroup.Stop();
                stopGroup.End();
            }
            // start new
            var toStartGroups = new List<GroupMiner>();
            lock (_lock)
            {
                foreach (var startKey in toStartMinerGroupKeys)
                {
                    var miningPairs = newGroupedMiningPairs[startKey];
                    var toStart = new GroupMiner(miningPairs, startKey);
                    _runningGroupMiners[toStart.Key] = toStart;
                    toStartGroups.Add(toStart);
                }
            }
            var miningLocation = StratumService.SelectedServiceLocation;
            foreach (var toStart in toStartGroups)
            {
                await toStart.Start(miningLocation, _username);
            }
            // log scope
            {
                var stopLog = toStopMinerGroupKeys.Length > 0 ? string.Join(",", toStopMinerGroupKeys) : "EMTPY";
                Logger.Info(Tag, $"Stop Old Mining: ({stopLog})");
                var startLog = toStartMinerGroupKeys.Length > 0 ? string.Join(",", toStartMinerGroupKeys) : "EMTPY";
                Logger.Info(Tag, $"Start New Mining : ({startLog})");
                var sameLog = noChangeGroupMinersKeys.Length > 0 ? string.Join(",", noChangeGroupMinersKeys) : "EMTPY";
                Logger.Info(Tag, $"No change : ({sameLog})");
            }

            var hasChanged = toStartMinerGroupKeys.Length > 0 || toStopMinerGroupKeys.Length > 0;
            // There is a change in groups, change GUI
            if (hasChanged)
            {
                ApplicationStateManager.ClearRatesAll();
                await MinerStatsCheck();
            }
        }

        public static async Task MinerStatsCheck()
        {
            try
            {
                // TODO lock
                foreach (var groupMiners in _runningGroupMiners.Values)
                {
                    var m = groupMiners.Miner;

                    // skip if not running or if await already in progress
                    if (!m.IsRunning || m.IsUpdatingApi) continue;

                    var ad = await m.GetSummaryAsync();
                    if (ad == null)
                    {
                        Logger.Debug(m.MinerTag(), "GetSummary returned null..");
                    }
                }
                // Update GUI
                ApplicationStateManager.RefreshRates();
                // now we shoud have new global/total rate display it
                var kwhPriceInBtc = ExchangeRateApi.GetKwhPriceInBtc();
                ApplicationStateManager.DisplayTotalRate(MiningStats.GetProfit(kwhPriceInBtc));
            }
            catch (Exception e)
            {
                Logger.Error(Tag, $"Error occured while getting mining stats: {e.Message}");
            }
        }
    }
}
