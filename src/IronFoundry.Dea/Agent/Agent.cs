﻿namespace IronFoundry.Dea.Agent
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using IronFoundry.Dea.Config;
    using IronFoundry.Dea.Logging;
    using IronFoundry.Dea.Properties;
    using IronFoundry.Dea.Types;
    using Providers;

    public sealed class Agent : IAgent
    {
        private readonly TimeSpan OneSecondInterval = TimeSpan.FromSeconds(1);
        private readonly TimeSpan TwoSecondsInterval = TimeSpan.FromSeconds(2);
        private readonly TimeSpan FiveSecondsInterval = TimeSpan.FromSeconds(5);

        private readonly ILog log;
        private readonly IConfig config;
        private readonly IMessagingProvider messagingProvider;
        private readonly IFilesManager filesManager;
        private readonly IConfigManager configManager;
        private readonly IDropletManager dropletManager;
        private readonly IWebServerAdministrationProvider webServerProvider;
        private readonly IVarzProvider varzProvider;

        private readonly Hello helloMessage;

        private readonly Task heartbeatTask;
        private readonly Task varzTask;
        private readonly Task monitorAppsTask;

        private bool shutting_down = false;

        private ushort maxMemoryMB;

        private VcapComponentDiscover discoverMessage;

        public Agent(ILog log, IConfig config,
            IMessagingProvider messagingProvider,
            IFilesManager filesManager,
            IConfigManager configManager,
            IDropletManager dropletManager,
            IWebServerAdministrationProvider webServerAdministrationProvider,
            IVarzProvider varzProvider)
        {
            this.log               = log;
            this.config            = config;
            this.messagingProvider = messagingProvider;
            this.filesManager      = filesManager;
            this.configManager     = configManager;
            this.dropletManager    = dropletManager;
            this.webServerProvider = webServerAdministrationProvider;
            this.varzProvider      = varzProvider;

            helloMessage = new Hello(messagingProvider.UniqueIdentifier, config.LocalIPAddress, config.FilesServicePort);

            heartbeatTask = new Task(HeartbeatLoop);
            varzTask = new Task(SnapshotVarz);
            monitorAppsTask = new Task(MonitorApps);

            this.maxMemoryMB = config.MaxMemoryMB;
        }

        public bool Error { get; private set; }

        public void Start()
        {
            if (messagingProvider.Start())
            {
                discoverMessage = new VcapComponentDiscover(
                    type: Resources.Agent_DEAComponentType,
                    uuid: messagingProvider.UniqueIdentifier,
                    host: config.MonitoringServiceHostStr,
                    credentials: config.MonitoringCredentials);

                varzProvider.Discover = discoverMessage;

                messagingProvider.Subscribe(NatsSubscription.VcapComponentDiscover,
                    (msg, reply) =>
                    {
                        discoverMessage.UpdateUptime();
                        messagingProvider.Publish(reply, discoverMessage);
                    });

                messagingProvider.Publish(new VcapComponentAnnounce(discoverMessage));

                messagingProvider.Subscribe(NatsSubscription.DeaStatus, ProcessDeaStatus);
                messagingProvider.Subscribe(NatsSubscription.DropletStatus, ProcessDropletStatus);
                messagingProvider.Subscribe(NatsSubscription.DeaDiscover, ProcessDeaDiscover);
                messagingProvider.Subscribe(NatsSubscription.DeaFindDroplet, ProcessDeaFindDroplet);
                messagingProvider.Subscribe(NatsSubscription.DeaUpdate, ProcessDeaUpdate);
                messagingProvider.Subscribe(NatsSubscription.DeaStop, ProcessDeaStop);
                messagingProvider.Subscribe(NatsSubscription.GetDeaInstanceStartFor(messagingProvider.UniqueIdentifier), ProcessDeaStart);
                messagingProvider.Subscribe(NatsSubscription.RouterStart, ProcessRouterStart);
                messagingProvider.Subscribe(NatsSubscription.HealthManagerStart, ProcessHealthManagerStart);

                messagingProvider.Publish(helloMessage);

                RecoverExistingDroplets();

                heartbeatTask.Start();
                varzTask.Start();
                monitorAppsTask.Start();
            }
            else
            {
                log.Error(Resources.Agent_ErrorDetectedInNats_Message);
                Error = true;
            }
        }

        public void Stop()
        {
            if (shutting_down)
            {
                return;
            }

            shutting_down = true;

            log.Info(Resources.Agent_ShuttingDown_Message);

            dropletManager.ForAllInstances((instance) =>
                {
                    if (false == instance.IsCrashed)
                    {
                        instance.DeaShutdown();
                        StopDroplet(instance);
                    }
                });

            TakeSnapshot();

            messagingProvider.Dispose();

            log.Info(Resources.Agent_Shutdown_Message);
        }

        private void HeartbeatLoop()
        {
            while (false == shutting_down)
            {
                SendHeartbeat();
                Thread.Sleep(FiveSecondsInterval);
            }
        }

        private void ProcessDeaStart(string message, string reply)
        {
            if (shutting_down)
            {
                return;
            }

            log.Debug(Resources.Agent_ProcessDeaStart_Fmt, message);

            Droplet droplet = Message.FromJson<Droplet>(message);
            if (false == droplet.FrameworkSupported)
            {
                log.Info(Resources.Agent_NonAspDotNet_Message);
                return;
            }

            var instance = new Instance(config.AppDir, droplet);

            if (filesManager.Stage(droplet, instance))
            {
                WebServerAdministrationBinding binding = webServerProvider.InstallWebApp(filesManager.GetApplicationPathFor(instance), instance.Staged);
                if (null == binding)
                {
                    log.Error(Resources.Agent_ProcessDeaStartNoBindingAvailable, instance.Staged);
                    filesManager.CleanupInstanceDirectory(instance, true);
                }
                else
                {
                    instance.Host = binding.Host;
                    instance.Port = binding.Port;

                    configManager.BindServices(droplet, instance);
                    configManager.SetupEnvironment(droplet, instance);

                    instance.OnDeaStart();

                    if (false == shutting_down)
                    {
                        SendSingleHeartbeat(new Heartbeat(instance));

                        RegisterWithRouter(instance, instance.Uris);

                        dropletManager.Add(droplet.ID, instance);

                        TakeSnapshot();
                    }
                }
            }
        }

        private void ProcessDeaUpdate(string message, string reply)
        {
            if (shutting_down)
            {
                return;
            }

            log.Debug(Resources.Agent_ProcessDeaUpdate_Fmt, message);

            Droplet droplet = Message.FromJson<Droplet>(message);

            dropletManager.ForAllInstances(droplet.ID, (instance) =>
                {
                    string[] currentUris = instance.Uris.ToArrayOrNull(); // NB: will create new array

                    instance.Uris = droplet.Uris;

                    var toRemove = currentUris.Except(droplet.Uris);

                    var toAdd = droplet.Uris.Except(currentUris);

                    UnregisterWithRouter(instance, toRemove.ToArray());

                    RegisterWithRouter(instance, toAdd.ToArray());
                });

            TakeSnapshot();
        }

        private void ProcessDeaDiscover(string message, string reply)
        {
            if (shutting_down)
            {
                return;
            }

            log.Debug(Resources.Agent_ProcessDeaDiscover_Fmt, message, reply);

            Discover discover = Message.FromJson<Discover>(message);
            if (discover.Runtime == Constants.SupportedRuntime)
            {
                uint delay = 0;
                dropletManager.ForAllInstances(discover.DropletID, (instance) =>
                    {
                        delay += 10; // NB: 10 milliseconds delay per app
                    });
                messagingProvider.Publish(reply, helloMessage, delay);
            }
            else
            {
                log.Debug(Resources.Agent_ProcessDeaDiscoverUnsupportedRuntime_Fmt, discover.Runtime);
            }
        }

        private void ProcessDeaStop(string message, string reply)
        {
            if (shutting_down)
            {
                return;
            }

            log.Debug(Resources.Agent_ProcessDeaStop_Fmt, message);

            StopDroplet stopDropletMsg = Message.FromJson<StopDroplet>(message);

            uint dropletID = stopDropletMsg.DropletID;

            dropletManager.ForAllInstances(dropletID, (instance) =>
                {
                    if (stopDropletMsg.AppliesTo(instance))
                    {
                        instance.OnDeaStop();
                        StopDroplet(instance);
                    }
                });
        }

        private void StopDroplet(Instance instance)
        {
            if (instance.StopProcessed)
            {
                return;
            }

            log.Info(Resources.Agent_StoppingInstance_Fmt, instance.LogID);

            SendExitedMessage(instance);

            webServerProvider.UninstallWebApp(instance.Staged);

            dropletManager.InstanceStopped(instance);

            instance.DeaStopComplete();

            filesManager.CleanupInstanceDirectory(instance);

            log.Info(Resources.Agent_StoppedInstance_Fmt, instance.LogID);
        }

        private void SendExitedMessage(Instance instance)
        {
            if (instance.IsNotified)
            {
                return;
            }

            UnregisterWithRouter(instance);

            if (false == instance.HasExitReason)
            {
                instance.Crashed();
            }

            SendExitedNotification(instance);

            instance.IsNotified = true;
        }

        private void ProcessDeaStatus(string message, string reply)
        {
            if (shutting_down)
            {
                return;
            }

            log.Debug(Resources.Agent_ProcessDeaStatus_Fmt, message, reply);

            var statusMessage = new Status(helloMessage)
            {
                MaxMemory      = 4096,
                UsedMemory     = 0,
                ReservedMemory = 0,
                NumClients     = 20
            };
            messagingProvider.Publish(reply, statusMessage);
        }

        private void ProcessDeaFindDroplet(string message, string reply)
        {
            if (shutting_down)
            {
                return;
            }

            log.Debug(Resources.Agent_ProcessDeaFindDroplet_Fmt, message);

            FindDroplet findDroplet = Message.FromJson<FindDroplet>(message);

            dropletManager.ForAllInstances((instance) =>
            {
                if (instance.DropletID == findDroplet.DropletID)
                {
                    if (instance.Version == findDroplet.Version)
                    {
                        if (findDroplet.States.Contains(instance.State))
                        {
                            var startDate = DateTime.ParseExact(instance.Start, Constants.JsonDateFormat, CultureInfo.InvariantCulture);
                            var span = DateTime.Now - startDate;

                            var response = new FindDropletResponse(messagingProvider.UniqueIdentifier, instance, span)
                            {
                                FileUri = String.Format(CultureInfo.InvariantCulture, Resources.Agent_Droplets_Fmt, config.LocalIPAddress, config.FilesServicePort),
                                Credentials = config.FilesCredentials.ToArray(),
                            };

                            if (response.State != VcapStates.RUNNING)
                            {
                                response.Stats = null;
                            }

                            messagingProvider.Publish(reply, response);
                        }
                    }
                }
            });
        }

        private void ProcessDropletStatus(string message, string reply)
        {
            if (shutting_down)
            {
                return;
            }

            log.Debug(Resources.Agent_ProcessDropletStatus_Fmt, message, reply);

            dropletManager.ForAllInstances((instance) =>
            {
                if (instance.IsStarting || instance.IsRunning)
                {
                    var startDate = DateTime.ParseExact(instance.Start, Constants.JsonDateFormat, CultureInfo.InvariantCulture);
                    var span = DateTime.Now - startDate;
                    var response = new Stats(instance, span)
                    {
                        //Usage = 20
                    };
                    messagingProvider.Publish(reply, response);
                }
            });
        }

        private void ProcessRouterStart(string message, string reply)
        {
            if (shutting_down)
            {
                return;
            }

            log.Debug(Resources.Agent_ProcessRouterStart_Fmt, message, reply);

            dropletManager.ForAllInstances((instance) =>
                {
                    if (instance.IsRunning)
                    {
                        RegisterWithRouter(instance, instance.Uris);
                    }
                });
        }

        private void ProcessHealthManagerStart(string message, string reply)
        {
            log.Debug(Resources.Agent_ProcessHealthManagerStart_Fmt, message, reply);
            SendHeartbeat();
        }

        private void RegisterWithRouter(Instance instance, string[] uris)
        {
            if (shutting_down || instance.Uris.IsNullOrEmpty())
            {
                return;
            }

            var routerRegister = new RouterRegister
            {
                Dea  = messagingProvider.UniqueIdentifier,
                Host = instance.Host,
                Port = instance.Port,
                Uris = uris ?? instance.Uris,
                Tag  = new Tag { Framework = instance.Framework, Runtime = instance.Runtime }
            };

            messagingProvider.Publish(routerRegister);
        }

        private void UnregisterWithRouter(Instance instance)
        {
            UnregisterWithRouter(instance, instance.Uris);
        }

        private void UnregisterWithRouter(Instance instance, string[] uris)
        {
            if (instance.Uris.IsNullOrEmpty())
            {
                return;
            }

            var routerUnregister = new RouterUnregister
            {
                Dea  = messagingProvider.UniqueIdentifier,
                Host = instance.Host,
                Port = instance.Port,
                Uris = uris ?? instance.Uris,
            };

            messagingProvider.Publish(routerUnregister);
        }

        private void SendExitedNotification(Instance instance)
        {
            if (false == instance.IsEvacuated)
            {
                messagingProvider.Publish(new InstanceExited(instance));
                log.Debug(Resources.Agent_InstanceExited_Fmt, instance.LogID);
            }
        }

        private void SendHeartbeat()
        {
            if (shutting_down || dropletManager.IsEmpty)
            {
                return;
            }

            var heartbeats = new List<Heartbeat>();

            dropletManager.ForAllInstances((instance) =>
            {
                instance.UpdateState(GetApplicationState(instance.Staged));
                instance.StateTimestamp = Utility.GetEpochTimestamp();
                heartbeats.Add(new Heartbeat(instance));
            });

            var message = new DropletHeartbeat
            {
                Droplets = heartbeats.ToArray()
            };

            messagingProvider.Publish( message);
        }

        private string GetApplicationState(string name)
        {
            if (false == webServerProvider.DoesApplicationExist(name))
            {
                return VcapStates.DELETED;
            }

            ApplicationInstanceStatus status = webServerProvider.GetStatus(name);

            string rv;

            switch (status)
            {
                case ApplicationInstanceStatus.Started:
                    rv = VcapStates.RUNNING;
                    break;
                case ApplicationInstanceStatus.Starting:
                    rv = VcapStates.STARTING;
                    break;
                case ApplicationInstanceStatus.Stopping:
                    rv = VcapStates.SHUTTING_DOWN;
                    break;
                case ApplicationInstanceStatus.Stopped:
                    rv = VcapStates.STOPPED;
                    break;
                case ApplicationInstanceStatus.Unknown:
                    rv = VcapStates.CRASHED;
                    break;
                default:
                    rv = VcapStates.CRASHED;
                    break;
            }

            return rv;
        }

        private void TakeSnapshot()
        {
            Snapshot snapshot = dropletManager.GetSnapshot();
            filesManager.TakeSnapshot(snapshot);
        }

        private void RecoverExistingDroplets()
        {
            Snapshot snapshot = filesManager.GetSnapshot();
            if (null != snapshot)
            {
                dropletManager.FromSnapshot(snapshot);
                SendHeartbeat();
                TakeSnapshot();
            }
        }

        private void SendSingleHeartbeat(Heartbeat heartbeat)
        {
            var message = new DropletHeartbeat
            {
                Droplets = new[] { heartbeat }
            };
            messagingProvider.Publish(message);
        }

        private void SnapshotVarz()
        {
            while (false == shutting_down)
            {
                varzProvider.MaxMemoryMB = this.maxMemoryMB;
                varzProvider.MemoryReservedMB = 0; // TODO
                varzProvider.MemoryUsedMB = 0; // TODO
                varzProvider.MaxClients = 1024;
                if (shutting_down)
                {
                    varzProvider.State = VcapStates.SHUTTING_DOWN;
                }
                Thread.Sleep(OneSecondInterval);
            }
        }

        private void MonitorApps()
        {
            while (false == shutting_down)
            {
                Thread.Sleep(TwoSecondsInterval);

                var runningAppsJson = new List<string>();

                if (dropletManager.IsEmpty)
                {
                    varzProvider.MemoryUsedMB = 0;
                    return;
                }

                var metrics = new Dictionary<string, IDictionary<string, Metric>>
                {
                    { "framework", new Dictionary<string, Metric>() }, 
                    { "runtime", new Dictionary<string, Metric>() }
                };

                dropletManager.ForAllInstances((instance) =>
                    {
                        if (false == instance.IsRunning)
                        {
                            return;
                        }

                        foreach (KeyValuePair<string, IDictionary<string, Metric>> kvp in metrics)
                        {
                            var metric = new Metric();

                            if (kvp.Key == "framework")
                            {
                                if (false == metrics.ContainsKey(instance.Framework))
                                {
                                    kvp.Value[instance.Framework] = metric;
                                }
                                metric = kvp.Value[instance.Framework];
                            }

                            if (kvp.Key == "runtime")
                            {
                                if (!metrics.ContainsKey(instance.Runtime))
                                {
                                    kvp.Value[instance.Runtime] = metric;
                                }
                                metric = kvp.Value[instance.Runtime];
                            }

                            metric.UsedMemory = 0; // TODO KB
                            metric.ReservedMemory = 0; // TODO KB
                            metric.UsedDisk = 0; // TODO BYTES
                            metric.UsedCpu = 0; // TODO
                        }

                        string instanceJson = instance.ToJson();
                        runningAppsJson.Add(instanceJson);
                    }
                );

                varzProvider.RunningAppsJson = runningAppsJson;
                varzProvider.FrameworkMetrics = metrics["framework"];
                varzProvider.RuntimeMetrics = metrics["runtime"];
            }
        }
    }
}