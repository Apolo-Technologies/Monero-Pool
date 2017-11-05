﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ZeriumPool
{
    public static class NativeFunctions
    {
        [DllImport("CryptoNight", EntryPoint = "cn_slow_hash")]
        public static extern void cn_slow_hash(byte[] data, uint length, byte[] hash);

        [DllImport("CryptoNight", EntryPoint = "cn_fast_hash")]
        public static extern void cn_fast_hash(byte[] data, uint length, byte[] hash);

        [DllImport("CryptoNight", EntryPoint = "check_account_address")]
        public static extern UInt32 check_account_address(string address, UInt32 prefix);

        [DllImport("CryptoNight", EntryPoint = "convert_block")]
        public static extern UInt32 convert_block(byte[] cblock, int length, byte[] convertedblock);


        public static bool IsLinux
        {
            get
            {
                int p = (int)Environment.OSVersion.Platform;
                return (p == 4) || (p == 6) || (p == 128);
            }
        }
    }
}
