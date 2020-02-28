﻿/*
 * Shadowsocks-Net https://github.com/shadowsocks/Shadowsocks-Net
 */

using System;
using System.Net;
using System.Net.Sockets;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Buffers;
using System.IO;
using Argument.Check;

namespace Shadowsocks_Minimal_Crossplatform_Local
{
    using Shadowsocks;
    using Shadowsocks.Local;
    using Shadowsocks.Infrastructure;
    using Shadowsocks.Infrastructure.Sockets;

    class Program
    {
        static LocalServer localServer = null;

        static async Task Main(string[] args)
        {
            var config = new ConfigurationBuilder().AddJsonFile("app-config.json", optional: true, reloadOnChange: true).Build();
            var localConfig = config.GetSection("Proxy").Get<LocalServerConfig>();

            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder
                    .AddFilter("Microsoft", LogLevel.Warning)
                    .AddFilter("System", LogLevel.Warning)
                    .AddConsole();  //.AddNLog(config);
            });

            Console.CancelKeyPress += Console_CancelKeyPress;
            AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;

            if (null == localServer)
            {
                //var logger = loggerFactory.CreateLogger<LocalServer>();
                var logger = loggerFactory.CreateLogger("Local");
                var serverLoader = new DefaultServerLoader();

                localServer = new LocalServer(localConfig, serverLoader, logger);
            }
            await localServer.Start();
            await Task.CompletedTask;

            Console.WriteLine("press key to stop server");
            Console.ReadKey();            
            localServer.Stop();

            Console.WriteLine("press key to exit");
            Console.ReadKey();
        }

        private static void CurrentDomain_ProcessExit(object sender, EventArgs e)
        {

        }

        private static void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {

        }

    }


}