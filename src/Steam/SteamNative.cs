using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Dispatch.Steam
{
    /// <summary>
    /// The few read-only Steamworks calls Dispatch uses, through the library's flat C API.
    /// Only loaded inside the short-lived lookup process.
    /// </summary>
    internal static class SteamNative
    {
        public const int LobbyMatchListCallback = 510;
        public const int DistanceWorldwide = 3;

        private const string Lib = "steam_api64";

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibraryW(string path);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SteamAPI_InitFlat(byte[] errorMessage);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SteamAPI_Shutdown();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SteamAPI_SteamMatchmaking_v009();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SteamAPI_SteamUtils_v010();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SteamAPI_ISteamMatchmaking_AddRequestLobbyListDistanceFilter(IntPtr self, int filter);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SteamAPI_ISteamMatchmaking_AddRequestLobbyListStringFilter(IntPtr self, byte[] key, byte[] value, int comparison);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong SteamAPI_ISteamMatchmaking_RequestLobbyList(IntPtr self);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyByIndex(IntPtr self, int index);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SteamAPI_ISteamMatchmaking_GetLobbyData(IntPtr self, ulong lobby, byte[] key);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SteamAPI_ISteamMatchmaking_GetLobbyMemberLimit(IntPtr self, ulong lobby);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SteamAPI_ISteamUtils_IsAPICallCompleted(IntPtr self, ulong call, [MarshalAs(UnmanagedType.I1)] out bool failed);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SteamAPI_ISteamUtils_GetAPICallResult(
            IntPtr self, ulong call, out uint result, int size, int expectedCallback, [MarshalAs(UnmanagedType.I1)] out bool failed);

        /// <summary>Size of SteamNetworkPingLocation_t, an opaque blob.</summary>
        public const int PingLocationSize = 512;

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SteamAPI_RunCallbacks();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SteamAPI_SteamNetworkingUtils_SteamAPI_v004();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(IntPtr self);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern float SteamAPI_ISteamNetworkingUtils_GetLocalPingLocation(IntPtr self, byte[] location);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SteamAPI_ISteamNetworkingUtils_ParsePingLocationString(IntPtr self, byte[] text, byte[] location);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SteamAPI_ISteamNetworkingUtils_ConvertPingLocationToString(IntPtr self, byte[] location, byte[] buffer, int size);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SteamAPI_ISteamNetworkingUtils_EstimatePingTimeBetweenTwoLocations(IntPtr self, byte[] location1, byte[] location2);

        public static byte[] Utf8(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s ?? "");
            Array.Resize(ref bytes, bytes.Length + 1);
            return bytes;
        }

        public static string FromUtf8(IntPtr p)
        {
            if (p == IntPtr.Zero)
            {
                return "";
            }

            var bytes = new List<byte>(64);
            for (var i = 0; i < 8192; i++)
            {
                var b = Marshal.ReadByte(p, i);
                if (b == 0)
                {
                    break;
                }

                bytes.Add(b);
            }

            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        public static string FromUtf8(byte[] buffer)
        {
            var end = Array.IndexOf(buffer, (byte)0);
            return Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
        }
    }
}
