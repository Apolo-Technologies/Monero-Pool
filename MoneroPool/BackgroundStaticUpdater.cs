﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace MoneroPool
{
    public class BackgroundStaticUpdater
    {
        public BackgroundStaticUpdater()
        {
            
        }

        public static void ForceUpdate()
        {
            Logger.Log(Logger.LogLevel.General, "Background updater forced!");
            Statics.CurrentBlockHeight = (int) (Statics.DaemonJson.InvokeMethod("getblockcount"))["result"]["count"];
            Statics.CurrentBlockTemplate = (JObject)
                                           (Statics.DaemonJson.InvokeMethod("getblocktemplate",
                                                                            new JObject(
                                                                                new JProperty(
                                                                                    "reserve_size", 4),
                                                                                new JProperty(
                                                                                    "wallet_address",
                                                                                    Statics.Config
                                                                                           .IniReadValue(
                                                                                               "wallet-address")))))
                                               ["result"];
        }

        public async void Start()
        {
            await Task.Yield();
            Statics.CurrentBlockHeight =
                (int) (await Statics.DaemonJson.InvokeMethodAsync("getblockcount"))["result"]["count"];
            Statics.CurrentBlockTemplate = (JObject)
                                           (await
                                            Statics.DaemonJson.InvokeMethodAsync("getblocktemplate",
                                                                                 new JObject(
                                                                                     new JProperty(
                                                                                         "reserve_size", 4),
                                                                                     new JProperty(
                                                                                         "wallet_address",
                                                                                         Statics.Config
                                                                                                .IniReadValue(
                                                                                                    "wallet-address")))))
                                               ["result"];
            Statics.HashRate.Begin = DateTime.Now;
            Statics.HashRate.Difficulty = 0;
            Logger.Log(Logger.LogLevel.General, "Acquired block template and height, miners can connet now!");
            while (true)
            {


                try
                {

                    int newBlockHeight =
                        (int) (await Statics.DaemonJson.InvokeMethodAsync("getblockcount"))["result"]["count"];
                    Logger.Log(Logger.LogLevel.General, "Current pool hashrate : {0} Hashes/Second",
                               Helpers.GetHashRate(Statics.HashRate.Difficulty, Statics.HashRate.Time));

                    Statics.RedisDb.Information.CurrentBlock = newBlockHeight;
                    Statics.RedisDb.Information.NewtworkHashRate =
                        (double) ((int) Statics.CurrentBlockTemplate["difficulty"])/60;
                    Statics.RedisDb.Information.PoolHashRate = Helpers.GetHashRate(Statics.HashRate.Difficulty,
                                                                                   Statics.HashRate.Time);
                    Statics.RedisDb.Information.SharesPerSecond = (double)Statics.TotalShares/5;
                    Statics.RedisDb.Information.RoundShares += Statics.TotalShares;
                    Statics.RedisDb.Information.BaseDificulty = int.Parse(Statics.Config.IniReadValue("base-difficulty"));
                    Statics.TotalShares = 0;
                    Statics.RedisDb.SaveChanges(Statics.RedisDb.Information);

                    if (newBlockHeight != Statics.CurrentBlockHeight)
                    {
                        Statics.CurrentBlockTemplate = (JObject)
                                                       (await
                                                        Statics.DaemonJson.InvokeMethodAsync("getblocktemplate",
                                                                                             new JObject(
                                                                                                 new JProperty(
                                                                                                     "reserve_size", 4),
                                                                                                 new JProperty(
                                                                                                     "wallet_address",
                                                                                                     Statics.Config
                                                                                                            .IniReadValue
                                                                                                         (
                                                                                                             "wallet-address")))))
                                                           ["result"];

                        Logger.Log(Logger.LogLevel.General, "New block with height {0}", newBlockHeight);
                        Statics.HashRate.Difficulty = 0;
                        Statics.HashRate.Time = 0;
                        Statics.HashRate.Begin = DateTime.Now;
                        Statics.RedisDb.Information.RoundShares = 0;
                        Statics.RedisDb.SaveChanges(Statics.RedisDb.Information);
                        Statics.CurrentBlockHeight = newBlockHeight;
                    }
                    var list = Statics.ConnectedClients.ToList();
                    for (int i = 0; i < list.Count; i++)
                    {
                        try
                        {
                            Miner miner = Statics.RedisDb.Miners.First(x => x.Address == list[i].Value.Address);
                            if (miner.TimeHashRate == null)
                            {
                                miner.TimeHashRate = new Dictionary<DateTime, double>();
                                miner.TimeHashRate.Add(DateTime.Now, Helpers.GetMinerHashRate(miner));
                            }

                            if ((DateTime.Now - list[i].Value.LastSeen).TotalSeconds >
                                int.Parse(Statics.Config.IniReadValue("client-timeout-seconds")))
                            {
                                Logger.Log(Logger.LogLevel.General, "Removing time out client {0}", list[i].Key);
                                Statics.ConnectedClients.Remove(list[i].Key);

                                miner.MinersWorker.Remove(list[i].Key);
                                miner.TimeHashRate.Add(DateTime.Now, Helpers.GetMinerHashRate(miner));

                                Statics.RedisDb.Remove(
                                    Statics.RedisDb.MinerWorkers.First(x => x.Identifier == list[i].Key));
                            }
                            else if ((DateTime.Now - miner.TimeHashRate.Last().Key).Minutes > 5)
                                miner.TimeHashRate.Add(DateTime.Now, Helpers.GetMinerHashRate(miner));

                            foreach (var cur in miner.TimeHashRate.Where(x => (DateTime.Now - x.Key).Days >= 1).ToList())
                            {
                                miner.TimeHashRate.Remove(cur.Key);
                            }
                            Statics.RedisDb.SaveChanges(miner);
                        }
                        catch
                        {
                        }

                    }
                    System.Threading.Thread.Sleep(5000);
                }
                catch (Exception e)
                {
                    Logger.Log(Logger.LogLevel.Error, e.ToString());
                }
            }
        }
    }
}
