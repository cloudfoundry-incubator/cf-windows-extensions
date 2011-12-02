﻿ using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace CloudFoundry.Net.Services.DEA
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        static void Main(string[] args)
        {
            bool debug = args.Contains("debug");

            Directory.SetCurrentDirectory(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));

            System.AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);
            
            if (!debug)
            {
                ServiceBase[] ServicesToRun;
                ServicesToRun = new ServiceBase[] { new DEAService() };
                ServiceBase.Run(ServicesToRun);
            }
            else
            {
                DEAService deaService = new DEAService();
                deaService.Start(new string[0]);
                System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
            }
        }

        static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            EventLog.WriteEntry("WinDEA", e.ExceptionObject.ToString(), EventLogEntryType.Error);
        }
    }
}
