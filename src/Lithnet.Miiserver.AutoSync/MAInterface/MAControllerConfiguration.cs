﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using Lithnet.Miiserver.Client;
using NLog;

namespace Lithnet.Miiserver.AutoSync
{
    [DataContract(Name = "management-agent")]
    [KnownType(typeof(ActiveDirectoryChangeTrigger))]
    [KnownType(typeof(FimServicePendingImportTrigger))]
    [KnownType(typeof(IntervalExecutionTrigger))]
    [KnownType(typeof(PowerShellExecutionTrigger))]
    [KnownType(typeof(ScheduledExecutionTrigger))]
    public class MAControllerConfiguration
    {
        private static Logger logger = LogManager.GetCurrentClassLogger();

        [DataMember(Name = "id")]
        public Guid ManagementAgentID { get; set; }

        [DataMember(Name = "name")]
        public string ManagementAgentName { get; set; }

        [DataMember(Name = "is-missing", EmitDefaultValue = false)]
        public bool IsMissing { get; set; }

        [DataMember(Name = "version")]
        public int Version { get; set; }

        internal void ResolveManagementAgent()
        {
            try
            {
                Guid? id = Global.FindManagementAgent(this.ManagementAgentName, this.ManagementAgentID);

                if (id.HasValue)
                {
                    ManagementAgent ma = ManagementAgent.GetManagementAgent(id.Value);
                    this.ManagementAgentName = ma.Name;
                    this.ManagementAgentID = ma.ID;
                    this.IsMissing = false;
                    this.ResolvePartitions(ma);
                    return;
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Exception finding management agent {this.ManagementAgentID}/{this.ManagementAgentName}");
            }

            logger.Warn($"Management agent could not be found. Name: '{this.ManagementAgentName}'. ID: '{this.ManagementAgentID}'");

            this.IsMissing = true;
        }

        internal void ResolvePartitions(ManagementAgent ma)
        {
            if (this.Partitions == null)
            {
                this.Partitions = new PartitionConfigurationCollection();
            }

            foreach (PartitionConfiguration c in this.Partitions)
            {
                bool found = false;

                foreach (Partition p in ma.Partitions.Values.Where(t => t.Selected))
                {
                    if (c.ID == p.ID || string.Equals(c.Name, p.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        c.UpdateConfiguration(p);
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    c.IsMissing = true;
                }
            }

            foreach (Partition p in ma.Partitions.Values.Where(t => t.Selected && this.Partitions.GetItemOrNull(t.ID) == null))
            {
                PartitionConfiguration c = new PartitionConfiguration(p);
                MAConfigDiscovery.DoAutoRunProfileDiscovery(c, ma);
                this.Partitions.Add(c);
            }
        }

        [DataMember(Name = "partitions")]
        public PartitionConfigurationCollection Partitions { get; set; }


        [DataMember(Name = "controller-script-path")]
        public string MAControllerPath { get; set; }

        [DataMember(Name = "disabled")]
        public bool Disabled { get; set; }

        public MAControllerConfiguration(string managementAgentName, Guid managementAgentID)
        {
            this.ManagementAgentName = managementAgentName;
            this.ManagementAgentID = managementAgentID;
            this.Triggers = new List<IMAExecutionTrigger>();
            this.Partitions = new PartitionConfigurationCollection();
        }

        [DataMember(Name = "triggers")]
        public List<IMAExecutionTrigger> Triggers { get; private set; }

        [DataMember(Name = "lock-managementagents")]
        public HashSet<string> LockManagementAgents { get; set; }

        internal string GetRunProfileName(MARunProfileType type, Guid partitionID)
        {
            PartitionConfiguration p = this.Partitions.GetItemOrNull(partitionID);

            if (p == null)
            {
                logger.Warn($"Could not map run profile {type} for partition {partitionID} because the partition was not found");
                return null;
            }

            if (!p.IsActive)
            {
                logger.Warn($"Could not map run profile {type} for partition {p.Name} ({partitionID}) because the partition is not active");
                return null;
            }

            return this.GetRunProfileName(type, p);
        }

        internal string GetRunProfileName(MARunProfileType type, string partitionName)
        {
            PartitionConfiguration p;

            if (string.IsNullOrWhiteSpace(partitionName))
            {
                p = this.Partitions.GetDefaultOrFirstActivePartition();
            }
            else
            {
                p = this.Partitions.GetItemOrNull(partitionName);
            }

            if (p == null)
            {
                logger.Warn($"Could not map run profile {type} for partition {partitionName} because the partition was not found");
                return null;
            }

            return this.GetRunProfileName(type, p);
        }

        internal string GetRunProfileName(MARunProfileType type, PartitionConfiguration partition)
        {
            switch (type)
            {
                case MARunProfileType.DeltaImport:
                    return partition.ScheduledImportRunProfileName;

                case MARunProfileType.FullImport:
                    return partition.FullImportRunProfileName;

                case MARunProfileType.Export:
                    return partition.ExportRunProfileName;

                case MARunProfileType.DeltaSync:
                    return partition.DeltaSyncRunProfileName;

                case MARunProfileType.FullSync:
                    return partition.FullSyncRunProfileName;

                default:
                case MARunProfileType.None:
                    throw new ArgumentException("Unknown run profile type");
            }
        }

        public override string ToString()
        {
            return this.ManagementAgentName ?? this.ManagementAgentID.ToString();
        }
    }
}
