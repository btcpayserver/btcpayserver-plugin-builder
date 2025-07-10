using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace PluginBuilder.Tests;

public class Utils
{
        public static int _nextPort = 8001;
        public static object _portLock = new object();

        public static int FreeTcpPort()
        {
            lock (_portLock)
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                while (true)
                {
                    try
                    {
                        var port = _nextPort++;
                        socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
                        return port;
                    }
                    catch (SocketException)
                    {
                        // Retry unless exhausted
                        if (_nextPort == 65536)
                        {
                            throw;
                        }
                    }
                }
            }
        }
    }
