﻿using System.Diagnostics;

namespace IronFoundry.Dea
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using Microsoft.VisualBasic.Devices;

    public static class Utility
    {
        public static int GetEpochTimestamp()
        {
            return (int)((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds);
        }

        public static void CopyDirectory(DirectoryInfo source, DirectoryInfo target)
        {
            var c = new Computer();
            c.FileSystem.CopyDirectory(source.FullName, target.FullName, true);
        }

        /// <summary>
        /// TODO: which one to choose?
        /// </summary>
        public static IPAddress LocalIPAddress
        {
            get { return GetLocalIPAddresses().Last(); }
        }

        // NB: this allows "break on all exceptions" to be enabled in VS without having the SocketException break
        [DebuggerStepThrough]
        public static ushort FindNextAvailablePortAfter(ushort argStartingPort)
        {
            for (ushort port = argStartingPort; port < 65535; port++)
            {
                try
                {
                    var ep = new IPEndPoint(IPAddress.Any, port);
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    socket.Bind(ep);
                    socket.Close();
                    // Port available
                    return port;
                }
                catch (SocketException) { }
            }

            return UInt16.MinValue;
        }

        private static IEnumerable<IPAddress> GetLocalIPAddresses()
        {
            IPHostEntry host = Dns.GetHostEntry(Dns.GetHostName());
            return host.AddressList.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork).ToListOrNull();
        }

        public static string GetFileSizeString(long size)
        {
            var sizes = new[] { "B", "K", "M", "G" };
            int order = 0;
            while (size >= 1024 && order++ < sizes.Length)
                size = size / 1024;            
            return string.Format("{0:0.##}{1}", size, sizes[order]);
        }
    }
}