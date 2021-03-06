﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using System.Numerics;
using StackExchange.Redis;

namespace ZeriumPool
{
    public static class Hash
    {
        public static byte[] CryptoNight(byte[] data)
        {
            byte[] crytoNightHash = new byte[32];
            //Dirty hack for increased stack size
            Thread t = new Thread(
                () => NativeFunctions.cn_slow_hash(data, (uint)data.Length, crytoNightHash), 1024 * 1024 * 8);
            t.Start();
            t.Join();

            return crytoNightHash;
        }
        public static byte[] CryptoNightFastHash(byte[] data)
        {
            byte[] crytoNightHash = new byte[32];
            //Dirty hack for increased stack size
            Thread t = new Thread(
                () => NativeFunctions.cn_fast_hash(data, (uint)data.Length, crytoNightHash), 1024 * 1024 * 8);
            t.Start();
            t.Join();

            return crytoNightHash;
        }
    }

    public enum ShareProcess
    {
        ValidShare,
        ValidBlock,
        InvalidShare
    }

    public enum StaticsLock
    {
        LockedByPool = 2,
        LockedByBackGroundUpdater = 1,
        NoLock = 0
    }

    public class PoolHashRateCalculation
    {
        public uint Difficulty;
        public ulong Time;
        public DateTime Begin;

        public PoolHashRateCalculation()
        {
        }
    }

    public static class Statics
    {
        public static volatile uint TotalShares;

        public static volatile StaticsLock Lock;

        public static volatile JObject CurrentBlockTemplate;

        public static volatile int CurrentBlockHeight;

        public static volatile int ReserveSeed;

        public static volatile IniFile Config;

        public static volatile JsonRPC DaemonJson;

        public static volatile JsonRPC WalletJson;

        public static volatile List<PoolBlock> BlocksPendingSubmition;

        public static volatile List<PoolBlock> BlocksPendingPayment;

        public static volatile Dictionary<string, ConnectedWorker> ConnectedClients = new Dictionary<string, ConnectedWorker>();

        public static volatile RedisPoolDatabase RedisDb;

        public static volatile PoolHashRateCalculation HashRate;
    }

    public static class Helpers
    {
        public static double GetHashRate(List<uint> difficulty, ulong time)
        {
            //Thanks surfer43
            double difficultySum = difficulty.Sum(x=>(double)x);

            return GetHashRate(difficultySum, time);
        }
        public static double GetHashRate(double difficulty, ulong time)
        {
            //Thanks surfer43    , seriously thank you, It works great

            Logger.Log(Logger.LogLevel.Debug, "Returning hash rate of {0}",difficulty/time);
            return difficulty / time;
        }

        public static double GetWorkerHashRate(ConnectedWorker worker)
        {
            ulong time =
                (ulong)
                (worker.ShareDifficulty.Skip(worker.ShareDifficulty.Count - 4).First().Key -
                 worker.ShareDifficulty.Last().Key).Seconds;
            return GetHashRate(
                worker.ShareDifficulty.Skip(worker.ShareDifficulty.Count - 4)
                      .ToDictionary(x =>x.Key, x => (uint)x.Value)
                      .Values.ToList(), time);

        }

        public static double GetMinerWorkerHashRate(MinerWorker worker)
        {
            //don't covnert to dictionary, rare but as seen in testing time stamps may be same
            double time = 0;
            double difficulty = 0;
            foreach (var shareDifficulty in worker.ShareDifficulty)
            {
                time += shareDifficulty.Key.TotalSeconds;
                difficulty += shareDifficulty.Value;
            }
            return GetHashRate(difficulty, (ulong)time);

        }
        public static double GetMinerHashRate(Miner worker)
        {
            double hashRate = 0;
            worker.MinersWorker.ForEach(x=>hashRate +=Statics.RedisDb.MinerWorkers.First(x2=>x2.Identifier==x).HashRate);
            return hashRate;
        }


        public static uint WorkerVardiffDifficulty(ConnectedWorker worker)
        {
            double aTargetTime = int.Parse(Statics.Config.IniReadValue("vardiff-targettime-seconds"));

            uint returnValue = 0;
            // Don't keep it no zone forever
            if ((DateTime.Now - worker.LastShare).TotalSeconds > aTargetTime)
            {
                double deviance = 100 - (((DateTime.Now - worker.LastShare).Seconds*100)/aTargetTime);
                if (Math.Abs(deviance) > int.Parse(Statics.Config.IniReadValue("vardiff-targettime-maxdeviation")))
                    deviance = -int.Parse(Statics.Config.IniReadValue("vardiff-targettime-maxdeviation"));
                returnValue = (uint)((worker.LastDifficulty * (100 + deviance)) / 100);
            }
            else
            {                    
                //We calculate average of last 4 shares.

                double aTime = worker.ShareDifficulty.Skip(worker.ShareDifficulty.Count-4).Take(4).Sum(x=>x.Key.TotalSeconds)/4;



                double deviance = 100 -
                                  ((aTime*100)/int.Parse(Statics.Config.IniReadValue("vardiff-targettime-seconds")));

                if (Math.Abs(deviance) < int.Parse(Statics.Config.IniReadValue("vardiff-targettime-deviation-allowed")))
                    returnValue = worker.LastDifficulty;
                else if (deviance > 0)
                {
                    if (deviance > int.Parse(Statics.Config.IniReadValue("vardiff-targettime-maxdeviation")))
                        deviance = int.Parse(Statics.Config.IniReadValue("vardiff-targettime-maxdeviation"));
                    returnValue = (uint)((worker.LastDifficulty * (100 + deviance)) / 100);
                }
                else
                {
                    if (Math.Abs(deviance) > int.Parse(Statics.Config.IniReadValue("vardiff-targettime-maxdeviation")))
                        deviance = -int.Parse(Statics.Config.IniReadValue("vardiff-targettime-maxdeviation"));
                    returnValue = (uint)((worker.LastDifficulty * (100 + deviance)) / 100);
                }
            }

            if (returnValue < uint.Parse(Statics.Config.IniReadValue("base-difficulty")))
                returnValue = uint.Parse(Statics.Config.IniReadValue("base-difficulty"));
            else if (returnValue > uint.Parse(Statics.Config.IniReadValue("vardiff-max-difficulty")))
                returnValue = uint.Parse(Statics.Config.IniReadValue("vardiff-max-difficulty"));
            Logger.Log(Logger.LogLevel.Debug, "Returning new difficulty if {0} vs previous {1}", returnValue, worker.LastDifficulty);
            return returnValue;
        }

        public static string GenerateUniqueWork(ref int seed)
        {
            seed = Statics.ReserveSeed++;
            byte[] work = StringToByteArray((string) Statics.CurrentBlockTemplate["blocktemplate_blob"]);

            Array.Copy(BitConverter.GetBytes(seed), 0, work, (int)Statics.CurrentBlockTemplate["reserved_offset"], 4);

            work = GetConvertedBlob(work);

            Logger.Log(Logger.LogLevel.Debug, "Generated unqiue work for seed {0}",seed);
            return BitConverter.ToString(work).Replace("-", "");
        }

        public static byte[] GenerateShareWork(int seed,bool convert)
        {
            byte[] work = StringToByteArray((string)Statics.CurrentBlockTemplate["blocktemplate_blob"]);

            Array.Copy(BitConverter.GetBytes(seed), 0, work, (int)Statics.CurrentBlockTemplate["reserved_offset"], 4);

            if(convert)
                work = GetConvertedBlob(work);

            Logger.Log(Logger.LogLevel.Debug, "Generated share work for seed {0}", seed);

            return work;
        }

        public static uint SwapEndianness(uint x)
        {
            return ((x & 0x000000ff) << 24) +  // First byte
                   ((x & 0x0000ff00) << 8) +   // Second byte
                   ((x & 0x00ff0000) >> 8) +   // Third byte
                   ((x & 0xff000000) >> 24);   // Fourth byte
        }

        public static uint GetTargetFromDifficulty(uint difficulty)
        {
            return uint.MaxValue/difficulty;
        }

        public static string GetRequestBody(Mono.Net.HttpListenerRequest request)
        {
            //disposale messes up mono

            string documentContents;
            StreamReader readStream = new StreamReader(request.InputStream, Encoding.UTF8);

            documentContents = readStream.ReadToEnd();

            //readStream.Dispose();

            return documentContents;
        }

        public static byte[] StringToByteArray(string hex)
        {
            return Enumerable.Range(0, hex.Length)
                             .Where(x => x % 2 == 0)
                             .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
                             .ToArray();
        }

        public static ShareProcess ProcessShare(byte[] blockHash, int blockDifficulty, uint shareDifficulty)
        {
            BigInteger diff = new BigInteger(StringToByteArray("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF00"));

            List<byte> blockList = blockHash.ToList();
            blockList.Add(0x00);
            BigInteger block = new BigInteger(blockList.ToArray());

            BigInteger blockDiff = diff / block;
            if (blockDiff >= blockDifficulty)
            {
                Logger.Log(Logger.LogLevel.General ,"Block found with hash:{0}", BitConverter.ToString(blockHash).Replace("-", ""));
                return ShareProcess.ValidBlock;

            }
            else if (blockDiff < shareDifficulty)
            {
               Logger.Log(Logger.LogLevel.General, "Invalid share found with hash:{0}", BitConverter.ToString(blockHash).Replace("-", ""));
                return ShareProcess.InvalidShare;
            }
           Logger.Log(Logger.LogLevel.General, "Valid share found with hash:{0}", BitConverter.ToString(blockHash).Replace("-", ""));
            return ShareProcess.ValidShare;
        }

        public static bool IsValidAddress(string address, uint prefix)
        {
            uint ret = NativeFunctions.check_account_address(address, prefix);
            if (ret == 0)
                return false;
            return true;
        }

        public static byte[] GetConvertedBlob(byte[] blob)
        {
            byte[] converted = new byte[128];
            uint returnLength = NativeFunctions.convert_block(blob, blob.Length, converted);
            return converted.Take((int)returnLength).ToArray();
        }
    }
}
