﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.ServiceModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lithnet.Logging;
using Lithnet.Miiserver.Client;

namespace Lithnet.Miiserver.AutoSync
{
    internal class ExecutionEngine : MarshalByRefObject
    {
        private Dictionary<string, MAExecutor> maExecutors;

        private ServiceHost service;

        internal static object ServiceControlLock = new object();

        private CancellationTokenSource cancellationToken;

        public ExecutorState State { get; set; }

        public ExecutionEngine()
        {
            this.service = EventService.CreateInstance();
            Logger.WriteLine("Initialized event service host");

            this.InitializeMAExecutors();
        }

        public void Start()
        {
            lock (ExecutionEngine.ServiceControlLock)
            {
                this.State = ExecutorState.Starting;
                this.StartMAExecutors();
                this.State = ExecutorState.Running;
            }
        }

        public void Stop()
        {
            lock (ExecutionEngine.ServiceControlLock)
            {
                this.State = ExecutorState.Stopping;
                this.StopMAExecutors();
                this.State = ExecutorState.Stopped;
            }
        }

        public void ShutdownService()
        {
            try
            {
                if (this.service != null)
                {
                    if (this.service.State != CommunicationState.Closed)
                    {
                        this.service.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("The service host did not shutdown cleanly");
                Logger.WriteException(ex);
            }

            this.service = null;
        }

        public void Stop(string managementAgentName)
        {
            this.GetExecutorOrThrow(managementAgentName).Stop();
        }

        public void Start(string managementAgentName)
        {
            MAConfigParameters c = Program.ActiveConfig.ManagementAgents.GetItemOrDefault(managementAgentName);

            if (c == null)
            {
                throw new InvalidOperationException($"There was no active configuration found for the management agent {managementAgentName}");
            }

            Trace.WriteLine($"Starting {managementAgentName}");
            this.GetExecutorOrThrow(managementAgentName).Start(c);
        }

        internal IList<MAStatus> GetMAState()
        {
            List<MAStatus> states = new List<MAStatus>();

            if (this.maExecutors == null)
            {
                return states;
            }

            foreach (MAExecutor x in this.maExecutors.Values)
            {
                states.Add(x.InternalStatus);
            }

            return states;
        }

        internal MAStatus GetMAState(string managementAgentName)
        {
            return this.GetExecutorOrThrow(managementAgentName).InternalStatus;
        }

        private void InitializeMAExecutors()
        {
            this.maExecutors = new Dictionary<string, MAExecutor>(StringComparer.OrdinalIgnoreCase);

            foreach (ManagementAgent ma in ManagementAgent.GetManagementAgents())
            {
                MAExecutor x = new MAExecutor(ma);
                x.StateChanged += this.X_StateChanged;
                this.maExecutors.Add(ma.Name, x);
            }
        }

        private void StartMAExecutors()
        {
            this.cancellationToken = new CancellationTokenSource();

            if (RegistrySettings.ExecutionEngineEnabled)
            {
                foreach (MAConfigParameters c in Program.ActiveConfig.ManagementAgents)
                {
                    if (c.IsMissing)
                    {
                        Logger.WriteLine("{0}: Skipping management agent because it is missing from the Sync Engine", c.ManagementAgentName);
                        continue;
                    }

                    if (this.maExecutors.ContainsKey(c.ManagementAgentName))
                    {
                        Trace.WriteLine($"Starting {c.ManagementAgentName}");
                        Task.Run(() => this.maExecutors[c.ManagementAgentName].Start(c), this.cancellationToken.Token);
                    }
                    else
                    {
                        Logger.WriteLine($"Cannot start management agent executor '{c.ManagementAgentName}' because the management agent was not found");
                    }
                }
            }
            else
            {
                Logger.WriteLine("Execution engine has been disabled");
            }
        }

        private void X_StateChanged(object sender, MAStatusChangedEventArgs e)
        {
            EventService.NotifySubscribers(e.Status);
        }

        private void StopMAExecutors()
        {
            if (this.maExecutors == null)
            {
                return;
            }

            this.cancellationToken?.Cancel();

            List<Task> stopTasks = new List<Task>();

            foreach (MAExecutor x in this.maExecutors.Values)
            {
                stopTasks.Add(Task.Factory.StartNew(() =>
                {
                    try
                    {
                        x.Stop();
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }));
            }

            Logger.WriteLine("Waiting for executors to stop");

            if (!Task.WaitAll(stopTasks.ToArray(), 90000))
            {
                Logger.WriteLine("Timeout waiting for executors to stop");
                throw new TimeoutException();
            }
            else
            {
                Logger.WriteLine("Executors stopped successfully");
            }

            this.State = ExecutorState.Stopped;
        }

        private MAExecutor GetExecutorOrThrow(string managementAgentName)
        {
            if (this.maExecutors.ContainsKey(managementAgentName))
            {
                return this.maExecutors[managementAgentName];
            }
            else
            {
                throw new NoSuchManagementAgentException(managementAgentName);
            }
        }
    }
}
