using System;
using System.Diagnostics;

namespace LiteNetLib
{
    static class NetUtils
    {
        public static int RelativeSequenceNumber(int number, int expected)
        {
            return (number - expected + NetConstants.MaxSequence + NetConstants.HalfMaxSequence) % NetConstants.MaxSequence - NetConstants.HalfMaxSequence;
        }

        public static int GetDividedPacketsCount(int size, int mtu)
        {
            return (size/mtu) + (size%mtu == 0 ? 0 : 1);
        }

        private static readonly object DebugLogLock = new object();

        [Conditional("DEBUG_MESSAGES")]
        internal static void DebugWrite(ConsoleColor color, string str, params object[] args)
        {
            lock(DebugLogLock)
            {
#if UNITY_DEBUG
                    string debugStr = string.Format(str, args);
                    UnityEngine.Debug.Log(debugStr);
#elif WINRT
                    Debug.WriteLine(str, args);
#else
                    Console.ForegroundColor = color;
                    Console.WriteLine(str, args);
                    Console.ForegroundColor = ConsoleColor.Gray;
#endif
            }
        }

        [Conditional("DEBUG_MESSAGES"), Conditional("DEBUG")]
        internal static void DebugWriteForce(ConsoleColor color, string str, params object[] args)
        {
            lock (DebugLogLock)
            {
#if UNITY_DEBUG
                string debugStr = string.Format(str, args);
                UnityEngine.Debug.Log(debugStr);
#elif WINRT
                Debug.WriteLine(str, args);
#else
                Console.ForegroundColor = color;
                Console.WriteLine(str, args);
                Console.ForegroundColor = ConsoleColor.Gray;
#endif
            }
        }
    }
}
