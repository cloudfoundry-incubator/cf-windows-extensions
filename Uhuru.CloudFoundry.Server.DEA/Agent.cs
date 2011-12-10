﻿// -----------------------------------------------------------------------
// <copyright file="Agent.cs" company="Uhuru Software">
// Copyright (c) 2011 Uhuru Software, Inc., All Rights Reserved
// </copyright>
// -----------------------------------------------------------------------

namespace Uhuru.CloudFoundry.DEA
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net.Sockets;
    using System.Threading;
    using Uhuru.Configuration;
    using Uhuru.NatsClient;
    using Uhuru.Utilities;
    using Uhuru.Utilities.ProcessPerformance;
    using Uhuru.CloudFoundry.Server.DEA.PluginBase;
    
    public delegate void BoolStateBlockCallback(bool state);

    public class Agent : VcapComponent
    {
        private const decimal Version = 0.99m;
        
        private DropletCollection Droplets = new DropletCollection();
        private Stager AgentStager = new Stager();

        private FileViewer AgentFileViewer = new FileViewer();
        private Monitoring AgentMonitoring = new Monitoring();

        private bool DisableDirCleanup;
        private bool EnforceUlimit;
        private bool MultiTenant;
        private bool Secure;
        
        private DeaReactor deaReactor;

        private HelloMessage HelloMessage = new HelloMessage(); 
        //private Dictionary<string, object> HelloMessage;
        private volatile bool ShuttingDown = false;
        private int EvacuationDelayMs = 30 * 1000;

        public Agent()
        {
            foreach (Configuration.DEA.RuntimeElement deaConf in UhuruSection.GetSection().DEA.Runtimes)
            {
                DeaRuntime dea = new DeaRuntime();

                dea.Executable = deaConf.Executable;
                dea.Version = deaConf.Version;
                dea.VersionFlag = deaConf.VersionFlag;
                dea.AdditionalChecks = deaConf.AdditionalChecks;
                dea.Enabled = true;

                foreach (Configuration.DEA.EnvironmentElement ienv in deaConf.Environment)
                {
                    dea.Environment.Add(ienv.Name, ienv.Value);
                }
                
                foreach (Configuration.DEA.DebugElement debugEnv in deaConf.Debug)
                {
                    dea.DebugEnv.Add(debugEnv.Name, new Dictionary<string, string>());
                    foreach (Configuration.DEA.EnvironmentElement ienv in debugEnv.Environment)
                    {
                        dea.DebugEnv[debugEnv.Name].Add(ienv.Name, ienv.Value);
                    }   
                }
               
                AgentStager.Runtimes.Add(deaConf.Name, dea);
            }

            AgentStager.DropletDir = UhuruSection.GetSection().DEA.BaseDir;

            EnforceUlimit = UhuruSection.GetSection().DEA.EnforceUlimit;
            DisableDirCleanup = UhuruSection.GetSection().DEA.DisableDirCleanup;
            MultiTenant = UhuruSection.GetSection().DEA.MultiTenant;
            Secure = UhuruSection.GetSection().DEA.Secure;

            AgentMonitoring.MaxMemoryMbytes = UhuruSection.GetSection().DEA.MaxMemory;

            AgentFileViewer.Port = UhuruSection.GetSection().DEA.FilerPort;
            
            AgentStager.ForeHttpFileSharing = UhuruSection.GetSection().DEA.ForceHttpSharing;

            ComponentType = "DEA";

            //apps_dump_dir = ConfigurationManager.AppSettings["logFile"] ?? Path.GetTempPath();
            AgentMonitoring.AppsDumpDirectory = Path.GetTempPath();

            //heartbeat_interval = UhuruSection.GetSection().DEA.HeartBeatInterval;

            AgentMonitoring.MaxClients = MultiTenant ? Monitoring.DefaultMaxClients : 1;

            AgentStager.StagedDir = Path.Combine(AgentStager.DropletDir, "staged");
            AgentStager.AppsDir = Path.Combine(AgentStager.DropletDir, "apps");
            AgentStager.DbDir = Path.Combine(AgentStager.DropletDir, "db");

            Droplets.AppStateFile = Path.Combine(AgentStager.DropletDir, "applications.json");
            
            deaReactor.Uuid = Uuid;

            HelloMessage.Id = Uuid;
            HelloMessage.Host = Host;
            HelloMessage.FileViewerPort = AgentFileViewer.Port;
            HelloMessage.Version = Version;
        }

        protected override void ConstructReactor()
        {
            if (deaReactor == null)
            {
                deaReactor = new DeaReactor();
                VcapReactor = deaReactor;
            }
        }


        public override void Run()
        {

            Logger.Info(Strings.StartingVcapDea, Version);

            AgentStager.SetupRuntimes();

            Logger.Info(Strings.UsingNetwork, Host);
            Logger.Info(Strings.MaxMemorySetTo, AgentMonitoring.MaxMemoryMbytes);
            Logger.Info(Strings.UtilizingCpuCores, Utils.NumberOfCores());

            if (MultiTenant)
            {
                Logger.Info(Strings.Allowingmultitenancy);
            }
            else
            {
                Logger.Info(Strings.RestrictingToSingleTenant);
            }

            Logger.Info(Strings.UsingDirectory, AgentStager.DropletDir);
            
            AgentStager.CreateDirectories();
            Droplets.AppStateFile = Path.Combine(AgentStager.DbDir, "applications.json");

            //Clean everything in the staged directory
            AgentStager.CleanCacheDirectory();
            
            
            AgentFileViewer.Start(AgentStager.AppsDir);

            VcapReactor.OnNatsError += new EventHandler<ReactorErrorEventArgs>(NatsErrorHandler);

            deaReactor.OnDeaStatus += new SubscribeCallback(DeaStatusHandler);
            deaReactor.OnDropletStatus += new SubscribeCallback(DropletStatusHandler);
            deaReactor.OnDeaDiscover += new SubscribeCallback(DeaDiscoverHandler);
            deaReactor.OnDeaFindDroplet += new SubscribeCallback(DeaFindDropletHandler);
            deaReactor.OnDeaUpdate += new SubscribeCallback(DeaUpdateHandler);

            deaReactor.OnDeaStop += new SubscribeCallback(DeaStopHandler);
            deaReactor.OnDeaStart += new SubscribeCallback(DeaStartHandler);

            deaReactor.OnRouterStart += new SubscribeCallback(RouterStartHandler);
            deaReactor.OnHealthManagerStart += new SubscribeCallback(HealthmanagerStartHandler);
            
            base.Run();  // Start the nats client
            
            RecoverExistingDroplets();
            DeleteUntrackedInstanceDirs();
            
            TimerHelper.RecurringLongCall(Monitoring.HeartbeatIntervalMilliseconds, delegate
            {
                SendHeartbeat();
            });

            TimerHelper.RecurringLongCall(Monitoring.MonitorIntervalMilliseconds, delegate
            {
                MonitorApps();
            });

            TimerHelper.RecurringLongCall(Monitoring.CrashesReaperIntervalMilliseconds, delegate
            {
                TheReaper();
            });
            
            TimerHelper.RecurringLongCall(Monitoring.VarzUpdateIntervalMilliseconds, delegate
            {
                SnapshotVarz();
            });
            
            deaReactor.SendDeaStart(HelloMessage.SerializeToJson());
        }

        public void RecoverExistingDroplets()
        {
            if (!File.Exists(Droplets.AppStateFile))
            {
                Droplets.RecoveredDroplets = true;
                return;
            }
            
            object[] instances = JsonConvertibleObject.DeserializeFromJsonArray(File.ReadAllText(Droplets.AppStateFile));

            foreach (object obj in instances)
            {
                DropletInstance instance = new DropletInstance();
                
                try
                {
                    instance.Properties.FromJsonIntermediateObject(obj);
                    instance.Properties.Orphaned = true;
                    instance.Properties.ResourcesTracked = false;
                    AgentMonitoring.AddInstanceResources(instance);
                    instance.Properties.StopProcessed = false;

                    try
                    {
                        instance.LoadPlugin();


                        instance.Properties.EnvironmentVarialbes[VcapAppPidVariable] = instance.Properties.ProcessId.ToString();
                        List<ApplicationVariable> appVariables = new List<ApplicationVariable>();
                        foreach (KeyValuePair<string, string> appEnv in instance.Properties.EnvironmentVarialbes)
                        {
                            ApplicationVariable appVariable = new ApplicationVariable();
                            appVariable.Name = appEnv.Key;
                            appVariable.Value = appEnv.Value;
                            appVariables.Add(appVariable);
                        }

                        instance.Plugin.RecoverApplication(appVariables.ToArray());
                    }
                    catch (Exception ex)
                    {
                        instance.ErrorLog.Error(ex.ToString());
                    }

                    if (instance.Properties.State == DropletInstanceState.Starting)
                    {
                        DetectAppReady(instance);
                    }
                    
                    
                    Droplets.AddDropletInstance(instance);
                }
                catch (Exception ex)
                {
                    Logger.Warning("Error recovering droplet {0}. Exception: {1}", instance.Properties.InstanceId, ex.ToString());
                }
            }
            
            Droplets.RecoveredDroplets = true;

            if (AgentMonitoring.Clients > 0)
            {
                Logger.Info(Strings.DeaRecoveredApplications, AgentMonitoring.Clients);
            }

            MonitorApps();
            Droplets.ForEach(delegate(DropletInstance instance)
            {
                RegisterInstanceWithRouter(instance);
            });
            SendHeartbeat();
            Droplets.ScheduleSnapshotAppState();
        }

        private void DeleteUntrackedInstanceDirs()
        {
            HashSet<string> trackedInstanceDirs = new HashSet<string>();

            Droplets.ForEach(delegate(DropletInstance instance)
            {
                trackedInstanceDirs.Add(instance.Properties.Directory);
            });
                        
            List<string> allInstanceDirs = Directory.GetDirectories(AgentStager.AppsDir, "*", SearchOption.TopDirectoryOnly).ToList();

            List<string> to_remove = (from dir in allInstanceDirs
                                      where !trackedInstanceDirs.Contains(dir)
                                      select dir).ToList();

            foreach (string dir in to_remove)
            {
                Logger.Warning(Strings.RemovingInstanceDoesn, dir);
                try
                {
                    // todo: vladi: this must be completed with cleaning up IIS sites
                    //Clean up the instance, including the IIS Web Site and the Windows User Accoung
                    //netiis is slow on cleanup
                    //Utils.ExecuteCommand(String.Format("netiis -cleanup={0}", dir));
                    Directory.Delete(dir, true);
                }
                catch (Exception e)
                {
                    Logger.Warning(Strings.CloudNotRemoveInstance, dir, e.ToString());
                }
            }
        }

        private void NatsErrorHandler(object sender,ReactorErrorEventArgs args)
        {
            string errorThrown = args.Message == null ? String.Empty : args.Message;
            Logger.Error(Strings.ExitingNatsError, errorThrown);

            // Only snapshot app state if we had a chance to recover saved state. This prevents a connect error
            // that occurs before we can recover state from blowing existing data away.
            if (Droplets.RecoveredDroplets)
            {
                Droplets.SnapshotAppState();
            }

            throw new Exception(String.Format(CultureInfo.InvariantCulture, Strings.NatsError, errorThrown));
        }

        public void EvacuateAppsThenQuit()
        {
            ShuttingDown = true;

            Logger.Info(Strings.Evacuatingapplications);

            Droplets.ForEach(delegate(DropletInstance instance)
            {
                try
                {
                    instance.Lock.EnterWriteLock();
                    if (instance.Properties.State != DropletInstanceState.Crashed)
                    {
                        Logger.Debug(Strings.EvacuatingApp, instance.Properties.InstanceId);

                        instance.Properties.ExitReason = DropletExitReason.DeaEvacuation;
                        deaReactor.SendDropletExited(instance.GenerateDropletExitedMessage().SerializeToJson());
                        instance.Properties.Evacuated = true;
                    }
                }
                finally
                {
                    instance.Lock.ExitWriteLock();
                }
            });

            Logger.Info(Strings.SchedulingShutdownIn, EvacuationDelayMs);

            Droplets.ScheduleSnapshotAppState();

            TimerHelper.DelayedCall(EvacuationDelayMs, delegate
            {
                Shutdown();
            });

        }

        public void Shutdown()
        {
            ShuttingDown = true;
            Logger.Info(Strings.ShuttingDownMessage);

            Droplets.ForEach(true, delegate(DropletInstance instance)
            {
                try
                {
                    instance.Lock.EnterWriteLock();
                    if (instance.Properties.State != DropletInstanceState.Crashed)
                    {
                        instance.Properties.ExitReason = DropletExitReason.DeaShutdown;
                    }
                    StopDroplet(instance);
                }
                finally
                {
                    instance.Lock.ExitWriteLock();
                }
            });

            // Allows messages to get out.
            TimerHelper.DelayedCall(250, delegate
            {
                Droplets.SnapshotAppState();
                AgentFileViewer.Stop();
                deaReactor.NatsClient.Stop();
                Logger.Info(Strings.ByeMessage);
            });

        }

        //todo: do this the right way
        public void WaitForExit()
        {
            while (deaReactor.NatsClient.Status == ConnectionStatus.Open)
            {
                Thread.Sleep(100);
            }
        }

        private void SendHeartbeat()
        {
            string response = Droplets.GenerateHeartbeatMessage().SerializeToJson();
            deaReactor.SendDeaHeartbeat(response);
        }

        void SnapshotVarz()
        {
            try
            {
                VarzLock.EnterWriteLock();
                Varz["apps_max_memory"] = AgentMonitoring.MaxMemoryMbytes;
                Varz["apps_reserved_memory"] = AgentMonitoring.MemoryReservedMbytes;
                Varz["apps_used_memory"] = AgentMonitoring.MemoryUsageKbytes / 1024;
                Varz["num_apps"] = AgentMonitoring.MaxClients;
                if (ShuttingDown)
                    Varz["state"] = "SHUTTING_DOWN";
            }
            finally
            {
                VarzLock.ExitWriteLock();
            }
        }

        void DeaStatusHandler(string message, string reply, string subject)
        {
            Logger.Debug(Strings.DEAreceivedstatusmessage);
            DeaStatusMessageResponse response = new DeaStatusMessageResponse();

            response.Id = Uuid;
            response.Host = Host;
            response.FileViewerPort = AgentFileViewer.Port;
            response.Version = Version;
            response.MaxMemoryMbytes = AgentMonitoring.MaxMemoryMbytes;
            response.MemoryReservedMbytes = AgentMonitoring.MemoryReservedMbytes; ;
            response.MemoryUsageKbytes = AgentMonitoring.MemoryUsageKbytes;
            response.NumberOfClients = AgentMonitoring.Clients;
            if (ShuttingDown)
                response.State = "SHUTTING_DOWN";

            deaReactor.SendReply(reply, response.SerializeToJson());
        }

        void DropletStatusHandler(string message, string reply, string subject)
        {
            if (ShuttingDown)
                return;

            Logger.Debug(Strings.DeaReceivedRouterStart, message);

            Droplets.ForEach(delegate(DropletInstance instance)
            {
                try
                {
                    instance.Lock.EnterReadLock();
                    if (instance.Properties.State == DropletInstanceState.Running || instance.Properties.State == DropletInstanceState.Starting)
                    {
                        DropletStatusMessageResponse response = instance.GenerateDropletStatusMessage();
                        response.Host = Host;
                        deaReactor.SendReply(reply, response.SerializeToJson());
                    }
                }
                finally
                {
                    instance.Lock.ExitReadLock();
                }
            });
        }

        void DeaDiscoverHandler(string message, string reply, string subject)
        {
            Logger.Debug(Strings.DeaReceivedDiscoveryMessage, message);
            if (ShuttingDown || AgentMonitoring.Clients >= AgentMonitoring.MaxClients || AgentMonitoring.MemoryReservedMbytes > AgentMonitoring.MaxMemoryMbytes)
            {
                Logger.Debug(Strings.IgnoringRequest);
                return;
            }
            
            DeaDiscoverMessageRequest pmessage = new DeaDiscoverMessageRequest();
            pmessage.FromJsonIntermediateObject(JsonConvertibleObject.DeserializeFromJson(message));

            if (!AgentStager.RuntimeSupported(pmessage.Runtime))
            {
                Logger.Debug(Strings.IgnoringRequestRuntime, pmessage.Runtime);
                return;
            }

            if (AgentMonitoring.MemoryReservedMbytes + pmessage.Limits.MemoryMbytes > AgentMonitoring.MaxMemoryMbytes)
            {
                Logger.Debug(Strings.IgnoringRequestNotEnoughMemory);
                return;
            }

            double taintMs = 0;

            try
            {
                Droplets.Lock.EnterReadLock();

                if(Droplets.Droplets.ContainsKey(pmessage.DropletId))
                {
                    taintMs += Droplets.Droplets[pmessage.DropletId].DropletInstances.Count * Monitoring.TaintPerAppMilliseconds;
                }
            }
            finally
            {
                Droplets.Lock.ExitReadLock();
            }

            try
            {
                AgentMonitoring.Lock.EnterReadLock();
                taintMs += Monitoring.TaintForMemoryMilliseconds * (AgentMonitoring.MemoryReservedMbytes / AgentMonitoring.MaxMemoryMbytes);
                taintMs = Math.Min(taintMs, Monitoring.TaintMaxMilliseconds);
            }
            finally
            {
                AgentMonitoring.Lock.ExitReadLock();
            }

            Logger.Debug(Strings.SendingDeaDiscoverResponse, taintMs);
            TimerHelper.DelayedCall(taintMs, delegate()
            {
                deaReactor.SendReply(reply, HelloMessage.SerializeToJson());
            });
        }


        void DeaFindDropletHandler(string message, string reply, string subject)
        {
            if (ShuttingDown)
                return;

            DeaFindDropletMessageRequest pmessage = new DeaFindDropletMessageRequest();
            pmessage.FromJsonIntermediateObject(JsonConvertibleObject.DeserializeFromJson(message));

            Logger.Debug(Strings.DeaReceivedFindDroplet, message);

            Droplets.ForEach(delegate(DropletInstance instance)
            {
                try
                {
                    instance.Lock.EnterReadLock();

                    bool droplet_match = instance.Properties.DropletId == pmessage.DropletId;
                    bool version_match = pmessage.Version == null || pmessage.Version == instance.Properties.Version;
                    bool instace_match = pmessage.InstanceIds == null || pmessage.InstanceIds.Contains(instance.Properties.InstanceId);
                    bool index_match = pmessage.Indexes == null || pmessage.Indexes.Contains(instance.Properties.InstanceIndex);
                    bool state_match = pmessage.States == null || pmessage.States.Contains(instance.Properties.State);

                    DeaFindDropletMessageResponse response = new DeaFindDropletMessageResponse();

                    if (droplet_match && version_match && instace_match && index_match && state_match)
                    {
                        response.DeaId = Uuid;
                        response.Version = instance.Properties.Version;
                        response.DropletId = instance.Properties.DropletId;
                        response.InstanceId = instance.Properties.InstanceId;
                        response.Index = instance.Properties.InstanceIndex;
                        response.State = instance.Properties.State;
                        response.StateTimestamp = instance.Properties.StateTimestamp;
                        response.FileUri = String.Format(CultureInfo.InvariantCulture, Strings.HttpDroplets, Host, AgentFileViewer.Port);
                        response.FileAuth = AgentFileViewer.Credentials;
                        response.Staged = instance.Properties.Staged;
                        response.DebugIP = instance.Properties.DebugIP;
                        response.DebugPort = instance.Properties.DebugPort;

                        if (pmessage.IncludeStates && instance.Properties.State == DropletInstanceState.Running)
                        {
                            response.Stats = instance.GenerateDropletStatusMessage();
                            response.Stats.Host = Host;
                            response.Stats.Cores = Utils.NumberOfCores();
                        }

                        deaReactor.SendReply(reply, response.SerializeToJson());
                    }
                }
                finally
                {
                    instance.Lock.ExitReadLock();
                }
            });
        }

        void DeaUpdateHandler(string message, string replay, string subject)
        {
            if (ShuttingDown)
                return;

            DeaUpdateMessageRequest pmessage = new DeaUpdateMessageRequest();
            pmessage.FromJsonIntermediateObject(JsonConvertibleObject.DeserializeFromJson(message));

            Logger.Debug(Strings.DeaReceivedUpdateMessage, message);

            Droplets.ForEach(delegate(DropletInstance instance)
            {
                if (instance.Properties.DropletId == pmessage.DropletId)
                {
                    try
                    {
                        instance.Lock.EnterWriteLock();

                        Logger.Debug(Strings.MappingnewURIs);
                        Logger.Debug(Strings.NewCurrent, JsonConvertibleObject.SerializeToJson(pmessage.Uris), JsonConvertibleObject.SerializeToJson(instance.Properties.Uris));

                        List<string> toUnregister = new List<string>(instance.Properties.Uris.Except(pmessage.Uris));
                        List<string> toRegister = new List<string>(pmessage.Uris.Except(instance.Properties.Uris));

                        instance.Properties.Uris = toUnregister.ToArray();
                        UnregisterInstanceFromRouter(instance);

                        instance.Properties.Uris = toRegister.ToArray();
                        RegisterInstanceWithRouter(instance);

                        instance.Properties.Uris = pmessage.Uris.ToArray();
                    }
                    finally
                    {
                        instance.Lock.ExitWriteLock();
                    }
                }
            });
        }

        void DeaStopHandler(string message, string replay, string subject)
        {
            if (ShuttingDown)
                return;

            DeaStopMessageRequest pmessage = new DeaStopMessageRequest();
            pmessage.FromJsonIntermediateObject(JsonConvertibleObject.DeserializeFromJson(message));

            Logger.Debug(Strings.DeaReceivedStopMessage, message);

            Droplets.ForEach(true, delegate(DropletInstance instance)
            {
                try
                {
                    instance.Lock.EnterWriteLock();

                    bool droplet_match = instance.Properties.DropletId == pmessage.DropletId;
                    bool version_match = pmessage.Version == null || pmessage.Version == instance.Properties.Version;
                    bool instace_match = pmessage.InstanceIds == null || pmessage.InstanceIds.Contains(instance.Properties.InstanceId);
                    bool index_match = pmessage.Indexes == null || pmessage.Indexes.Contains(instance.Properties.InstanceIndex);
                    bool state_match = pmessage.States == null || pmessage.States.Contains(instance.Properties.State);

                    if (droplet_match && version_match && instace_match && index_match && state_match)
                    {
                        if (instance.Properties.State == DropletInstanceState.Starting || instance.Properties.State == DropletInstanceState.Running)
                        {
                            instance.Properties.ExitReason = DropletExitReason.Stopped;
                        }
                        if (instance.Properties.State == DropletInstanceState.Crashed)
                        {
                            instance.Properties.State = DropletInstanceState.Deleted;
                            instance.Properties.StopProcessed = false;
                        }

                        StopDroplet(instance);
                    }
                }
                finally
                {
                    instance.Lock.ExitWriteLock();
                }
            });
        }

        //onply stops the droplet instance from running, no cleanup or resource untracking
        void StopDroplet(DropletInstance instance)
        {
            try
            {
                instance.Lock.EnterWriteLock();

                if (instance.Properties.StopProcessed)
                    return;

                // Unplug us from the system immediately, both the routers and health managers.
                if (!instance.Properties.NotifiedExited)
                {
                    UnregisterInstanceFromRouter(instance);

                    if (instance.Properties.ExitReason == null)
                    {
                        instance.Properties.ExitReason = DropletExitReason.Crashed;
                        instance.Properties.State = DropletInstanceState.Crashed;
                        instance.Properties.StateTimestamp = DateTime.Now;
                        if (!instance.IsRunning)
                        {
                            instance.Properties.ProcessId = 0;
                        }
                    }

                    deaReactor.SendDropletExited(instance.GenerateDropletExitedMessage().SerializeToJson());

                    instance.Properties.NotifiedExited = true;
                }

                Logger.Info(Strings.StoppingInstance, instance.Properties.LoggingId);

                // if system thinks this process is running, make sure to execute stop script

                if (instance.Properties.State == DropletInstanceState.Starting || instance.Properties.State == DropletInstanceState.Running)
                {
                    instance.Properties.State = DropletInstanceState.Stopped;
                    instance.Properties.StateTimestamp = DateTime.Now;
                    if (instance.Plugin != null)
                    {
                        try
                        {
                            instance.Plugin.StopApplication();
                        }
                        catch (Exception ex)
                        {
                            instance.ErrorLog.Error(ex.ToString());
                        }
                    }
                }

                AgentMonitoring.RemoveInstanceResources(instance);
                instance.Properties.StopProcessed = true;

            }
            catch (Exception ex)
            {
                Logger.Error("Error stoping droplet: {0}, instance: {1}, exception:", instance.Properties.DropletId, instance.Properties.InstanceId, ex.ToString());
            }
            finally
            {
                instance.Lock.ExitWriteLock();
            }
        }

        private void CleanupDroplet(DropletInstance instance)
        {
            // Drop usage and resource tracking regardless of state
            AgentMonitoring.RemoveInstanceResources(instance);

            // clean up the in memory instance and directory only if the instance didn't crash
            if (instance.Properties.State != DropletInstanceState.Crashed)
            {
                Droplets.RemoveDropletInstance(instance);
                Droplets.ScheduleSnapshotAppState();

                if (!DisableDirCleanup)
                {

                    for (int retryAttempts = 5; retryAttempts > 0; retryAttempts--)
                    {
                        try
                        {
                            Directory.Delete(instance.Properties.Directory, true);
                            Logger.Debug(Strings.CleandUpDir, instance.Properties.Name, instance.Properties.Directory);
                            break;
                        }
                        catch (UnauthorizedAccessException e)
                        {
                            Logger.Warning(Strings.UnableToDeleteDirectory, instance.Properties.Directory, e.ToString());
                            Thread.Sleep(300);
                        }
                        catch (Exception e)
                        {
                            Logger.Warning(Strings.UnableToDeleteDirectory, instance.Properties.Directory, e.ToString());
                            break;
                        }
                    }
                }
            }
        }

        void DeaStartHandler(string message, string reply, string subject)
        {
            DeaStartMessageRequest pmessage;
            DropletInstance instance;

            try
            {
                Droplets.Lock.EnterWriteLock();
  
                if (ShuttingDown) return;
                Logger.Debug(Strings.DeaReceivedStartMessage, message);

                pmessage = new DeaStartMessageRequest();
                pmessage.FromJsonIntermediateObject(JsonConvertibleObject.DeserializeFromJson(message));

                long MemoryMbytes = pmessage.Limits != null && pmessage.Limits.MemoryMbytes != null ? pmessage.Limits.MemoryMbytes.Value : Monitoring.DefaultAppMemoryMbytes;
                long DiskMbytes = pmessage.Limits != null && pmessage.Limits.DiskMbytes != null ? pmessage.Limits.DiskMbytes.Value : Monitoring.DefaultAppDiskMbytes;
                long Fds = pmessage.Limits != null && pmessage.Limits.Fds != null ? pmessage.Limits.Fds.Value : Monitoring.DefaultAppFds;

                if (AgentMonitoring.MemoryReservedMbytes + MemoryMbytes > AgentMonitoring.MaxMemoryMbytes || AgentMonitoring.Clients >= AgentMonitoring.MaxClients)
                {
                    Logger.Info(Strings.Donothaveroomforthisclient);
                    return;
                }

                if (String.IsNullOrEmpty(pmessage.Sha1) || String.IsNullOrEmpty(pmessage.ExecutableFile) || String.IsNullOrEmpty(pmessage.ExecutableUri) )
                {
                    Logger.Warning(Strings.StartRequestMissingProper, message);
                    return;
                }

                if (!AgentStager.RuntimeSupported(pmessage.Runtime))
                {
                    Logger.Warning(Strings.CloudNotStartRuntimeNot, message);
                }


                instance = Droplets.CreateDropletInstance(pmessage);

                instance.Properties.MemoryQuotaBytes = MemoryMbytes * 1024 * 1024;
                instance.Properties.DiskQuotaBytes = DiskMbytes * 1024 * 1024;
                instance.Properties.FdsQuota = Fds;
                instance.Properties.Staged = instance.Properties.Name + "-" + instance.Properties.InstanceIndex + "-" + instance.Properties.InstanceId;
                instance.Properties.Directory = Path.Combine(AgentStager.AppsDir, instance.Properties.Staged);

                if (!String.IsNullOrEmpty(instance.Properties.DebugMode))
                {
                    instance.Properties.DebugPort = NetworkInterface.GrabEphemeralPort();
                    instance.Properties.DebugIP = Host;
                }

                instance.Properties.Port = NetworkInterface.GrabEphemeralPort();

                instance.Properties.EnvironmentVarialbes = SetupInstanceEnv(instance, pmessage.Environment, pmessage.Services);

                AgentMonitoring.AddInstanceResources(instance);
            }
            finally
            {
                Droplets.Lock.ExitWriteLock();    
            }
             
            //toconsider: the pre-starting stage should be able to gracefuly stop when the shutdown flag is set
            ThreadPool.QueueUserWorkItem(delegate(object data)
            {
                StartDropletInstance(instance, pmessage.Sha1, pmessage.ExecutableFile, pmessage.ExecutableUri);
            });
        }


        private void StartDropletInstance(DropletInstance instance, string sha1, string executableFile, string executableUri)
        {
            try
            {
                string TgzFile = Path.Combine(AgentStager.StagedDir, sha1 + ".tgz");
                AgentStager.StageAppDirectory(executableFile, executableUri, sha1, TgzFile, instance);

                Logger.Debug(Strings.Downloadcompleate);

                string starting = string.Format(CultureInfo.InvariantCulture, Strings.StartingUpInstanceOnPort, instance.Properties.LoggingId, instance.Properties.Port);
                
                if (!String.IsNullOrEmpty(instance.Properties.DebugMode))
                    Logger.Info(starting + Strings.WithDebuggerPort, instance.Properties.DebugPort);
                else
                    Logger.Info(starting);

                Logger.Debug(Strings.Clients, AgentMonitoring.Clients);
                Logger.Debug(Strings.ReservedMemoryUsageMb, AgentMonitoring.MemoryReservedMbytes, AgentMonitoring.MaxMemoryMbytes);


                List<ApplicationVariable> appVariables = new List<ApplicationVariable>();
                try
                {
                    instance.Lock.EnterWriteLock();

                    instance.Properties.WindowsPassword = "P4s$" + Credentials.GenerateCredential();
                    instance.Properties.WindowsUsername = WindowsVcapUsers.CreateUser(instance.Properties.InstanceId, instance.Properties.WindowsPassword);

                    instance.Properties.EnvironmentVarialbes.Add(VcapWindowsUserVariable, instance.Properties.WindowsUsername);
                    instance.Properties.EnvironmentVarialbes.Add(VcapWindowsUserPasswordVariable, instance.Properties.WindowsPassword);
                    instance.Properties.EnvironmentVarialbes.Add(VcapPluginStagingInfoVariable, File.ReadAllText(Path.Combine(instance.Properties.Directory, "startup")));

                    foreach (KeyValuePair<string, string> appEnv in instance.Properties.EnvironmentVarialbes)
                    {
                        ApplicationVariable appVariable = new ApplicationVariable();
                        appVariable.Name = appEnv.Key;
                        appVariable.Value = appEnv.Value;
                        appVariables.Add(appVariable);
                    }

                }
                finally
                {
                    instance.Lock.ExitWriteLock();
                }

                instance.LoadPlugin();
                instance.Plugin.ConfigureApplication(appVariables.ToArray());
                instance.Plugin.StartApplication();

                int pid = instance.Plugin.GetApplicationProcessID();

                try
                {
                    instance.Lock.EnterWriteLock();

                    if (pid != 0 && !instance.Properties.StopProcessed)
                    {
                        Logger.Info(Strings.PidAssignedToDroplet, pid, instance.Properties.LoggingId);
                        instance.Properties.ProcessId = pid;
                        Droplets.ScheduleSnapshotAppState();
                    }
                }
                finally
                {
                    instance.Lock.ExitWriteLock();
                }


                DetectAppReady(instance);
            }
            catch(Exception ex)
            {
                Logger.Warning(Strings.FailedStagingAppDir, instance.Properties.Directory, instance.Properties.LoggingId, ex.ToString());
                try
                {
                    instance.Lock.EnterWriteLock();

                    instance.Properties.State = DropletInstanceState.Crashed;
                    instance.Properties.ExitReason = DropletExitReason.Crashed;
                    instance.Properties.StateTimestamp = DateTime.Now;
                                        
                    StopDroplet(instance);

                }
                finally
                {
                    instance.Lock.ExitWriteLock();
                }
            }
        }

        private void DetectAppReady(DropletInstance instance)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                DetectAppReady(instance,
                        delegate(bool detected)
                        {
                            try
                            {
                                instance.Lock.EnterWriteLock();
                                if (detected)
                                {
                                    if (instance.Properties.State == DropletInstanceState.Starting)
                                    {
                                        Logger.Info(Strings.InstanceIsReadyForConnections, instance.Properties.LoggingId);
                                        instance.Properties.State = DropletInstanceState.Running;
                                        instance.Properties.StateTimestamp = DateTime.Now;

                                        deaReactor.SendDeaHeartbeat(instance.GenerateHeartbeat().SerializeToJson());
                                        RegisterInstanceWithRouter(instance);
                                        Droplets.ScheduleSnapshotAppState();
                                    }
                                }
                                else
                                {
                                    Logger.Warning(Strings.GivingUpOnConnectingApp);
                                    StopDroplet(instance);
                                }
                            }
                            finally
                            {
                                instance.Lock.ExitWriteLock();
                            }
                        }
                    );
            });
        }

        private void DetectAppReady(DropletInstance instance, BoolStateBlockCallback callBack)
        {
            //string state_file = manifest.ContainsKey("state_file") ? manifest["state_file"] : null;
            //if (state_file != null && state_file != String.Empty)
            //{
            //    state_file = Path.Combine(instance.Dir, state_file);
            //    detect_state_ready(instance, state_file, block);
            //}
            //else
            //{
            DetectPortReady(instance, callBack);
            //}
        }

        private void DetectPortReady(DropletInstance instance, BoolStateBlockCallback callBack)
        {
            int port = instance.Properties.Port;

            int attempts = 0;
            bool keep_going = true;
            while (attempts <= 1000 && instance.Properties.State == DropletInstanceState.Starting && keep_going == true)
            {
                if (instance.IsPortReady)
                {
                    keep_going = false;
                    callBack(true);
                }
                else
                {
                    Thread.Sleep(100);
                    attempts++;
                }
            }

            if (keep_going)
            {
                callBack(false);
            }
        }


        //todo: stefi: consider removing this method
        private int DetectAppPid(DropletInstance instance)
        {
            int detect_attempts = 0;
            int pid = 0;

            while(true)
            {
                try
                {
                    string pid_file = Path.Combine(instance.Properties.Directory, "run.pid");
                    if (File.Exists(pid_file))
                    {
                        pid = Convert.ToInt32(File.ReadAllText(pid_file), CultureInfo.InvariantCulture);
                        break;
                    }
                    else
                    {
                        detect_attempts++;
                        if (detect_attempts > 300 || !(instance.Properties.State == DropletInstanceState.Starting || instance.Properties.State == DropletInstanceState.Running))
                        {
                            Logger.Warning(Strings.GivingUpDetectingStopFile);
                            break;
                        }
                    }
                }
                catch 
                {
                }

                Thread.Sleep(500);
            }
            
            return pid;
        }


        private const string HomeVariable = "HOME";
        private const string VcapApplicationVariable = "VCAP_APPLICATION";
        private const string VcapServicesVariable = "VCAP_SERVICES";
        private const string VcapAppHostVariable = "VCAP_APP_HOST";
        private const string VcapAppPortVariable = "VCAP_APP_PORT";
        private const string VcapAppDebugIpVariable = "VCAP_DEBUG_IP";
        private const string VcapAppDebugPortVariable = "VCAP_DEBUG_PORT";
        private const string VcapPluginStagingInfoVariable = "VCAP_PLUGIN_STAGING_INFO";
        private const string VcapWindowsUserVariable = "VCAP_WINDOWS_USER";
        private const string VcapWindowsUserPasswordVariable = "VCAP_WINDOWS_USER_PASSWORD";
        private const string VcapAppPidVariable = "VCAP_APP_PID";


        private Dictionary<string, string> SetupInstanceEnv(DropletInstance instance, string[] app_env, Dictionary<string, object>[] services)
        {
            Dictionary<string, string> env = new Dictionary<string, string>();

            env.Add(HomeVariable, instance.Properties.Directory);
            env.Add(VcapApplicationVariable, create_instance_for_env(instance));
            env.Add(VcapServicesVariable, create_services_for_env(services));
            env.Add(VcapAppHostVariable, Host);
            env.Add(VcapAppPortVariable, instance.Properties.Port.ToString());

            env.Add(VcapAppDebugIpVariable, instance.Properties.DebugIP);
            env.Add(VcapAppDebugPortVariable, instance.Properties.DebugPort != null ? instance.Properties.DebugPort.ToString() : null);

            if (instance.Properties.DebugPort != null && AgentStager.Runtimes[instance.Properties.Runtime].DebugEnv != null)
            {
                if (AgentStager.Runtimes[instance.Properties.Runtime].DebugEnv.ContainsKey(instance.Properties.DebugMode))
                {
                    foreach (KeyValuePair<string, string> debugEnv in AgentStager.Runtimes[instance.Properties.Runtime].DebugEnv[instance.Properties.DebugMode])
                    {
                        env.Add(debugEnv.Key, debugEnv.Value);
                    }
                }
            }
            

            // Do the runtime environment settings
            foreach (KeyValuePair<string, string> runtimeEnv in AgentStager.Runtimes[instance.Properties.Runtime].Environment)
            {
                env.Add(runtimeEnv.Key, runtimeEnv.Value);
            }

            // User's environment settings
            if (app_env != null)
            {
                foreach (string appEnv in app_env)
                {
                    string[] envVar = appEnv.Split(new char[] { '=' }, 2);
                    env.Add(envVar[0], envVar[1]);
                }
            }

            return env;
        }

        private string create_instance_for_env(DropletInstance instance)
        {
            List<string> whitelist = new List<string>() { "instance_id", "instance_index", "name", "uris", "users", "version", "start", "runtime", "state_timestamp", "port" };
            Dictionary<string, object> result = new Dictionary<string, object>();

            Dictionary<string, object> jInstance = instance.Properties.ToJsonIntermediateObject();

            foreach (string key in whitelist)
            {
                if (jInstance[key] != null)
                {
                    //result[key] = JsonConvertibleObject.ObjectToValue<object>(jInstance[key]);
                    result[key] = jInstance[key];
                }
            }


            result["host"] = Host;
            result["limits"] = new Dictionary<string, object>() {
                {"fds", instance.Properties.FdsQuota},
                {"mem", instance.Properties.MemoryQuotaBytes},
                {"disk", instance.Properties.DiskQuotaBytes}
            };

            

            return JsonConvertibleObject.SerializeToJson(result);
        }
        
        private string create_legacy_services_for_env(Dictionary<string, object>[] services = null)
        {
            List<string> whitelist = new List<string>() { "name", "type", "vendor", "version" };

            List<Dictionary<string, object>> as_legacy = new List<Dictionary<string, object>>();

            foreach (Dictionary<string, object> svc in services)
            {
                Dictionary<string, object> leg_svc = new Dictionary<string, object>();
                foreach (string key in whitelist)
                {
                    if (svc.ContainsKey(key))
                    {
                        leg_svc[key] = svc[key];
                    }
                }
                leg_svc["tier"] = svc["plan"];
                leg_svc["options"] = svc["credentials"];

                as_legacy.Add(leg_svc);
            }

            return JsonConvertibleObject.SerializeToJson(as_legacy);
        }

        private string create_services_for_env(Dictionary<string, object>[] services = null)
        {
            List<string> whitelist = new List<string>() { "name", "label", "plan", "tags", "plan_option", "credentials" };
            Dictionary<string, List<Dictionary<string, object>>> svcs_hash = new Dictionary<string, List<Dictionary<string, object>>>();

            foreach (Dictionary<string, object> service in services)
            {
                string label = service["label"].ToString();
                if (!svcs_hash.ContainsKey(label))
                {
                    svcs_hash[label] = new List<Dictionary<string, object>>();
                }
                Dictionary<string, object> svc_hash = new Dictionary<string, object>();

                foreach (string key in whitelist)
                {
                    if (service[key] != null)
                    {
                        svc_hash[key] = service[key];
                    }
                }

                svcs_hash[label].Add(svc_hash);
            }

            return JsonConvertibleObject.SerializeToJson(svcs_hash);
        }

        void RouterStartHandler(string message, string reply, string subject)
        {
            if (ShuttingDown)
                return;

            Logger.Debug(Strings.DeaReceivedRouterStart, message);
            
            Droplets.ForEach(delegate(DropletInstance instance)
            {
                if (instance.Properties.State == DropletInstanceState.Running)
                {
                    RegisterInstanceWithRouter(instance);
                }
            });
        }

        void RegisterInstanceWithRouter(DropletInstance instance)
        {
            RouterMessage response = new RouterMessage();
            try
            {
                instance.Lock.EnterReadLock();

                if (instance.Properties.Uris == null || instance.Properties.Uris.Length == 0) return;

                response.DeaId = Uuid;
                response.Host = Host;
                response.Port = Port;
                response.Uris = new List<string>(instance.Properties.Uris).ToArray();

                response.Tags = new RouterMessage.TagsObject();
                response.Tags.Framework = instance.Properties.Framework;
                response.Tags.Runtime = instance.Properties.Runtime;
            }
            finally
            {
                instance.Lock.ExitReadLock();
            }

            deaReactor.SendRouterRegister(response.SerializeToJson());
        }

        void UnregisterInstanceFromRouter(DropletInstance instance)
        {
            RouterMessage response = new RouterMessage();
            try
            {
                instance.Lock.EnterReadLock();

                if (instance.Properties.Uris == null || instance.Properties.Uris.Length == 0) return;

                response.DeaId = Uuid;
                response.Host = Host;
                response.Port = Port;
                response.Uris = instance.Properties.Uris;

                response.Tags = new RouterMessage.TagsObject();
                response.Tags.Framework = instance.Properties.Framework;
                response.Tags.Runtime = instance.Properties.Runtime;
            }
            finally
            {
                instance.Lock.ExitReadLock();
            }

            deaReactor.SendRouterUnregister(response.SerializeToJson());
        }

        void HealthmanagerStartHandler(string message, string replay, string subject)
        {
            if (ShuttingDown)
                return;

            Logger.Debug(Strings.DeaReceivedHealthmanagerStart, message);

            SendHeartbeat();
        }

        void MonitorApps()
        {
            //AgentMonitoring.MemoryUsageKbytes = 0;
            long memoryUsageKbytes = 0;
            List<object> runningApps = new List<object>();

            if (Droplets.NoMonitorableApps())
            {
                AgentMonitoring.MemoryUsageKbytes = 0;
                return;
            }
                        
            DateTime start = DateTime.Now;

            ProcessData[] processStatuses = ProcessInformation.GetProcessUsage();

            TimeSpan elapsed = DateTime.Now - start;
            if (elapsed.TotalMilliseconds > 800) 
                Logger.Warning(Strings.TookXSecondsToExecutePs, elapsed.TotalSeconds, processStatuses.Length);

            Dictionary<int, ProcessData> pidInfo = new Dictionary<int, ProcessData>();
            foreach (ProcessData processStatus in processStatuses)
            {
                pidInfo[processStatus.ProcessId] = processStatus;
            }

            DateTime duStart = DateTime.Now;

            DiskUsageEntry[] duAll = DiskUsage.GetDiskUsage(AgentStager.AppsDir, false);
    
            TimeSpan duElapsed = DateTime.Now - duStart;

            if (duElapsed.TotalMilliseconds > 800)
            {
                Logger.Warning(Strings.TookXSecondsToExecuteDu, duElapsed.TotalSeconds);
                if ((duElapsed.TotalSeconds > 10) && ((DateTime.Now - AgentMonitoring.LastAppDump).TotalSeconds > Monitoring.AppsDumpIntervalMilliseconds))
                {
                    AgentMonitoring.DumpAppsDirDiskUsage(AgentStager.AppsDir);
                    AgentMonitoring.LastAppDump = DateTime.Now;
                }
            }

            Dictionary<string, long> duHash = new Dictionary<string, long>();
            foreach (DiskUsageEntry entry in duAll)
            {
                duHash[entry.Directory] = entry.SizeKB * 1024;
            }

            Dictionary<string, Dictionary<string, Dictionary<string, long>>> metrics = new Dictionary<string, Dictionary<string, Dictionary<string, long>>>() 
            {
                {"framework", new Dictionary<string, Dictionary<string, long>>()}, 
                {"runtime", new Dictionary<string, Dictionary<string, long>>()}
            };
            
            Droplets.ForEach(true, delegate(DropletInstance instance)
            {

                if (!instance.Lock.TryEnterWriteLock(10)) return;

                try
                {
                    
                    //todo: consider only checking for starting and running apps

                    try
                    {
                        instance.Properties.ProcessId = instance.Plugin.GetApplicationProcessID();
                    }
                    catch (Exception ex)
                    {
                        if (instance.ErrorLog != null) instance.ErrorLog.Error(ex.ToString());
                    }

                    int pid = instance.Properties.ProcessId;
                    if ((pid != 0 && pidInfo.ContainsKey(pid)) || instance.IsPortReady)
                    {

                        long memBytes = pid != 0 && pidInfo.ContainsKey(pid) ? (long)pidInfo[pid].WorkingSetBytes : 0;
                        long cpu = pid != 0 && pidInfo.ContainsKey(pid) ? (long)pidInfo[pid].Cpu : 0;
                        long diskBytes = duHash.ContainsKey(instance.Properties.Directory) ? duHash[instance.Properties.Directory] : 0;

                        instance.AddUsage(memBytes, cpu, diskBytes);

                        if (Secure)
                        {
                            //todo: after the monitorapps is working properly, enable checkusage
                            //CheckUsage(instance, curUsage);
                        }

                        memoryUsageKbytes += memBytes / 1024;

                        foreach (KeyValuePair<string, Dictionary<string, Dictionary<string, long>>> kvp in metrics)
                        {
                            Dictionary<string, long> metric = new Dictionary<string, long>() 
                                    {
                                        {"used_memory", 0},
                                        {"reserved_memory", 0},
                                        {"used_disk", 0},
                                        {"used_cpu", 0}
                                    };

                            if (kvp.Key == "framework")
                            {
                                if (!metrics.ContainsKey(instance.Properties.Framework))
                                    kvp.Value[instance.Properties.Framework] = metric;
                                
                                metric = kvp.Value[instance.Properties.Framework];
                            }
                            if (kvp.Key == "runtime")
                            {
                                if (!metrics.ContainsKey(instance.Properties.Runtime))
                                    kvp.Value[instance.Properties.Runtime] = metric;
                                
                                metric = kvp.Value[instance.Properties.Runtime];
                            }

                            metric["used_memory"] += memBytes / 1024;
                            metric["reserved_memory"] += instance.Properties.MemoryQuotaBytes / 1024;
                            metric["used_disk"] += diskBytes;
                            metric["used_cpu"] += cpu;
                        }

                        // Track running apps for varz tracking
                        runningApps.Add(instance.Properties.ToJsonIntermediateObject());
                    }
                    else
                    {

                        instance.Properties.ProcessId = 0;
                        if (instance.Properties.State == DropletInstanceState.Running)
                        {
                            if (!instance.IsPortReady)
                            {
                                StopDroplet(instance);
                            }
                        }

                    }
                }
                finally
                {
                    instance.Lock.ExitWriteLock();
                }
            });

            // export running app information to varz
            Varz["running_apps"] = runningApps;
            Varz["frameworks"] = metrics["framework"];
            Varz["runtimes"] = metrics["runtime"];

            TimeSpan ttlog = DateTime.Now - start;
            if (ttlog.TotalMilliseconds > 1000)
            {
                Logger.Warning(Strings.TookXSecondsToProcessPsAndDu, ttlog.TotalSeconds);
            }
        }

        // This is only called when in secure mode, cur_usage is in kb, quota is in bytes.
        private void CheckUsage(DropletInstance instance, DropletInstanceUsage usage)
        {
            if (instance == null || usage == null)
                return;

            // Check Mem
            if (usage.MemoryKbytes > (instance.Properties.MemoryQuotaBytes / 1024))
            {
                FileLogger logger = new FileLogger(Path.Combine(instance.Properties.Directory, "logs\\err.log"));

                logger.Fatal(Strings.MemoryLimitOfExceeded, instance.Properties.MemoryQuotaBytes / 1024 / 1024);
                logger.Fatal(Strings.ActualUsageWasProcessTerminated, usage.MemoryKbytes / 1024);
                StopDroplet(instance);
            }

            // Check Disk
            if (usage.DiskBytes > instance.Properties.DiskQuotaBytes)
            {
                FileLogger logger = new FileLogger(Path.Combine(instance.Properties.Directory, "logs\\err.log"));
                logger.Fatal(Strings.DiskUsageLimitOf, instance.Properties.DiskQuotaBytes / 1024 / 1024);
                logger.Fatal(Strings.ActualUsageWasProcessTerminated, usage.DiskBytes / 1024 / 1024);
                StopDroplet(instance);
            }

            // Check CPU
            if (instance.Usage.Count == 0)
            {
                return;
            }

            if (usage.Cpu > Monitoring.BeginReniceCpuThreshold)
            {
                int nice = instance.Properties.Nice + 1;
                if (nice < Monitoring.MaxReniceValue)
                {
                    instance.Properties.Nice = nice;
                    ProcessPriorityClass priority = 
                        nice == 0 ? ProcessPriorityClass.RealTime : nice == 1 ? ProcessPriorityClass.High :
                        nice == 2 ? ProcessPriorityClass.AboveNormal : nice == 3 ? ProcessPriorityClass.Normal : 
                        nice == 4 ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Idle;

                    Logger.Info(Strings.LoweringPriorityOnCpuBound, instance.Properties.Name, priority);

                    //TODO: vladi: make sure this works on Windows
                    Process.GetProcessById(instance.Properties.ProcessId).PriorityClass = priority;
                }
            }

            // TODO, Check for an attack, or what looks like one, and look at history?
            // pegged_cpus = @num_cores * 100
        }

        private void TheReaper()
        {

            Droplets.ForEach(true, delegate(DropletInstance instance)
            {
                if (!instance.Lock.TryEnterWriteLock(10)) return;

                bool removeDroplet = false;

                try
                {
                    bool isOldCrash = instance.Properties.State == DropletInstanceState.Crashed && (DateTime.Now - instance.Properties.StateTimestamp).TotalMilliseconds > Monitoring.CrashesReaperTimeoutMilliseconds;
                    bool isStopped = instance.Properties.State == DropletInstanceState.Stopped;
                    bool isDeleted = instance.Properties.State == DropletInstanceState.Deleted;

                    if (isOldCrash || isStopped || isDeleted)
                    {

                        Logger.Debug(Strings.CrashesReaperDeleted, instance.Properties.InstanceId);


                        if (instance.Plugin != null)
                        {
                            try
                            {
                                instance.Plugin.CleanupApplication(instance.Properties.Directory);
                                instance.Plugin = null;
                                WindowsVcapUsers.DeleteUser(instance.Properties.InstanceId);
                            }
                            catch (Exception ex)
                            {
                                instance.ErrorLog.Error(ex.ToString());
                            }
                        }

                        if (DisableDirCleanup) instance.Properties.Directory = null;
                        if (instance.Properties.Directory != null && instance.Plugin == null)
                        {
                            try
                            {
                                Directory.Delete(instance.Properties.Directory, true);
                                instance.Properties.Directory = null;
                            }
                            catch { }
                        }

                        if (instance.Plugin == null && instance.Properties.Directory == null)
                        {
                            removeDroplet = true;
                        }

                    }

                }
                finally
                {
                    instance.Lock.ExitWriteLock();
                }

                if (removeDroplet)
                {
                    Droplets.RemoveDropletInstance(instance);
                }

            });


        }
    }
}
