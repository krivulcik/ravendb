﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Raven.Client.Documents.Subscriptions;
using Raven.Client.Exceptions;
using Raven.Client.Json.Converters;
using Raven.Client.Http;
using Raven.Client.Server;
using Raven.Client.Server.ETL.SQL;
using Raven.Client.Server.Operations;
using Raven.Client.Server.PeriodicBackup;
using Raven.Server.Routing;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Commands;
using Raven.Server.ServerWide.Context;
using Sparrow.Json;
using Sparrow.Json.Parsing;

namespace Raven.Server.Web.System
{
    public class OngoingTasksHandler : DatabasesHandler
    {
        [RavenAction("ongoing-tasks", "GET", AuthorizationStatus.ValidUser)]
        public Task GetOngoingTasks()
        {
            var name = GetQueryStringValueAndAssertIfSingleAndNotEmpty("databaseName");
            if (TryGetAllowedDbs(name, out var _, requireAdmin:false) == false)
                return Task.CompletedTask;

            var result = GetOngoingTasksFor(name, ServerStore);

            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                context.Write(writer, result.ToJson());
            }

            return Task.CompletedTask;
        }

        public static OngoingTasksResult GetOngoingTasksFor(string dbName, ServerStore store)
        {
            var ongoingTasksResult = new OngoingTasksResult();
            using (store.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                DatabaseTopology dbTopology;
                ClusterTopology clusterTopology;
                DatabaseRecord databaseRecord;

                using (context.OpenReadTransaction())
                {
                    databaseRecord = store.Cluster.ReadDatabase(context, dbName);
                
                    if (databaseRecord == null)
                    {
                        return ongoingTasksResult;
                    }

                    dbTopology = databaseRecord.Topology;
                    clusterTopology = store.GetClusterTopology(context);

                    ongoingTasksResult.OngoingTasksList.AddRange(CollectSubscriptionTasks(context, databaseRecord, clusterTopology, store));
                }

                foreach (var tasks in new []
                {
                    CollectExternalReplicationTasks(databaseRecord.ExternalReplication, dbTopology,clusterTopology, store),
                    CollectEtlTasks(databaseRecord, dbTopology, clusterTopology, store),
                    CollectBackupTasks(databaseRecord, dbTopology, clusterTopology, store),
                })
                {
                    ongoingTasksResult.OngoingTasksList.AddRange(tasks);
                }

                if (store.DatabasesLandlord.DatabasesCache.TryGetValue(dbName, out var database) && database.Status == TaskStatus.RanToCompletion)
                {
                    ongoingTasksResult.SubscriptionsCount = (int)database.Result.SubscriptionStorage.GetAllSubscriptionsCount();
                }

                return ongoingTasksResult;
            }
        }

        private static IEnumerable<OngoingTask> CollectSubscriptionTasks(TransactionOperationContext context, DatabaseRecord databaseRecord, ClusterTopology clusterTopology, ServerStore store)
        {
            foreach (var keyValue in ClusterStateMachine.ReadValuesStartingWith(context, SubscriptionState.SubscriptionPrefix(databaseRecord.DatabaseName)))
            {
                var subscriptionState = JsonDeserializationClient.SubscriptionState(keyValue.Value);
                var tag = databaseRecord.Topology.WhoseTaskIsIt(subscriptionState, store.IsPassive());
                yield return new OngoingTaskSubscription
                {
                    // Supply only needed fields for List View  
                    ResponsibleNode = new NodeId
                    {
                        NodeTag = tag,
                        NodeUrl = clusterTopology.GetUrlFromTag(tag)
                    },
                    TaskName = subscriptionState.SubscriptionName,
                    TaskState = subscriptionState.Disabled ? OngoingTaskState.Disabled : OngoingTaskState.Enabled,
                    TaskId = subscriptionState.SubscriptionId,
                    Collection = subscriptionState.Criteria.Collection,
                    TimeOfLastClientActivity = subscriptionState.TimeOfLastClientActivity
                };
            }
        }

        private static IEnumerable<OngoingTask> CollectExternalReplicationTasks(List<ExternalReplication> watchers, DatabaseTopology dbTopology, ClusterTopology clusterTopology, ServerStore store)
        {
            if (dbTopology == null)
                yield break;

            foreach (var watcher in watchers)
            {
                var tag = dbTopology.WhoseTaskIsIt(watcher, store.IsPassive());

                yield return new OngoingTaskReplication
                {
                    TaskId = watcher.TaskId,
                    TaskName = watcher.Name,
                    ResponsibleNode = new NodeId
                    {
                        NodeTag = tag,
                        NodeUrl = clusterTopology.GetUrlFromTag(tag)
                    },
                    DestinationDatabase = watcher.Database,
                    TaskState = watcher.Disabled ? OngoingTaskState.Disabled : OngoingTaskState.Enabled,
                    DestinationUrl = watcher.Url,
                };
            }
        }

        private static IEnumerable<OngoingTask> CollectBackupTasks(DatabaseRecord databaseRecord, DatabaseTopology dbTopology, ClusterTopology clusterTopology, ServerStore store)
        {
            if (dbTopology == null)
                yield break;

            if (databaseRecord.PeriodicBackups == null)
                yield break;

            foreach (var backupConfiguration in databaseRecord.PeriodicBackups)
            {
                var tag = dbTopology.WhoseTaskIsIt(backupConfiguration, store.IsPassive());

                var backupDestinations = GetBackupDestinations(backupConfiguration);

                yield return new OngoingTaskBackup
                {
                    TaskId = backupConfiguration.TaskId,
                    BackupType = backupConfiguration.BackupType,
                    TaskName = backupConfiguration.Name,
                    TaskState = backupConfiguration.Disabled ? OngoingTaskState.Disabled : OngoingTaskState.Enabled,
                    ResponsibleNode = new NodeId
                    {
                        NodeTag = tag,
                        NodeUrl = clusterTopology.GetUrlFromTag(tag)
                    },
                    BackupDestinations = backupDestinations
                };
            }
        }

        private static List<string> GetBackupDestinations(PeriodicBackupConfiguration backupConfiguration)
        {
            var backupDestinations = new List<string>();

            if (backupConfiguration.LocalSettings != null && backupConfiguration.LocalSettings.Disabled == false)
                backupDestinations.Add("Local");
            if (backupConfiguration.AzureSettings != null && backupConfiguration.AzureSettings.Disabled == false)
                backupDestinations.Add("Azure");
            if (backupConfiguration.S3Settings != null && backupConfiguration.S3Settings.Disabled == false)
                backupDestinations.Add("S3");
            if (backupConfiguration.GlacierSettings != null && backupConfiguration.GlacierSettings.Disabled == false)
                backupDestinations.Add("Glacier");

            return backupDestinations;
        }

        private static IEnumerable<OngoingTask> CollectEtlTasks(DatabaseRecord databaseRecord, DatabaseTopology dbTopology, ClusterTopology clusterTopology, ServerStore store)
        {
            if (dbTopology == null)
                yield break;

            if (databaseRecord.RavenEtls != null)
            {
                foreach (var ravenEtl in databaseRecord.RavenEtls)
                {
                    var tag = dbTopology.WhoseTaskIsIt(ravenEtl, store.IsPassive());

                    var taskState = OngoingTaskState.Enabled;;

                    if (ravenEtl.Disabled || ravenEtl.Transforms.All(x => x.Disabled))
                        taskState = OngoingTaskState.Disabled;
                    else if (ravenEtl.Transforms.Any(x => x.Disabled))
                        taskState = OngoingTaskState.PartiallyEnabled;
                    
                    yield return new OngoingRavenEtl
                    {
                        TaskId = ravenEtl.TaskId,
                        TaskName = ravenEtl.Name,
                        // TODO arek TaskConnectionStatus = 
                        TaskState = taskState,
                        ResponsibleNode = new NodeId
                        {
                            NodeTag = tag,
                            NodeUrl = clusterTopology.GetUrlFromTag(tag)
                        },
                        DestinationUrl = ravenEtl.Connection.Url,
                        DestinationDatabase = ravenEtl.Connection.Database,
                    };
                }
            }

            if (databaseRecord.SqlEtls != null)
            {
                foreach (var sqlEtl in databaseRecord.SqlEtls)
                {
                    var tag = dbTopology.WhoseTaskIsIt(sqlEtl, store.IsPassive());

                    var (database, server) =
                        SqlConnectionStringParser.GetDatabaseAndServerFromConnectionString(sqlEtl.FactoryName,
                            sqlEtl.Connection.ConnectionString);

                    var taskState = OngoingTaskState.Enabled; 

                    if (sqlEtl.Disabled || sqlEtl.Transforms.All(x => x.Disabled))
                        taskState = OngoingTaskState.Disabled;
                    else if (sqlEtl.Transforms.Any(x => x.Disabled))
                        taskState = OngoingTaskState.PartiallyEnabled;

                    yield return new OngoingSqlEtl
                    {
                        TaskId = sqlEtl.TaskId,
                        TaskName = sqlEtl.Name,
                        // TODO arek TaskConnectionStatus = 
                        TaskState = taskState,
                        ResponsibleNode = new NodeId
                        {
                            NodeTag = tag,
                            NodeUrl = clusterTopology.GetUrlFromTag(tag)
                        },
                        DestinationServer = server,
                        DestinationDatabase = database,
                    };
                }
            }
        }

        // Get Info about a sepcific task - For Edit View in studio
        // Note: Each task should return its own specific object and Not 'GetTaskInfoResult' - see RavenDB-7712
        [RavenAction("/task", "GET", AuthorizationStatus.ValidUser)]
        public Task GetOngoingTaskInfo()
        {
            var dbName = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");

            if (TryGetAllowedDbs(dbName, out var _, requireAdmin: false) == false)
                return Task.CompletedTask;

            if (ResourceNameValidator.IsValidResourceName(dbName, ServerStore.Configuration.Core.DataDirectory.FullPath, out string errorMessage) == false)
                throw new BadRequestException(errorMessage);

            var key = GetLongQueryString("key");
            var typeStr = GetQueryStringValueAndAssertIfSingleAndNotEmpty("type");

            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                using (context.OpenReadTransaction())
                {
                    var clusterTopology = ServerStore.GetClusterTopology(context);
                    var record = ServerStore.Cluster.ReadDatabase(context, dbName);
                    var dbTopology = record?.Topology;

                    if (Enum.TryParse<OngoingTaskType>(typeStr, true, out var type) == false)
                        throw new ArgumentException($"Unknown task type: {type}", "type");

                    string tag;

                    switch (type)
                    {
                        case OngoingTaskType.Replication:

                            var watcher = record?.ExternalReplication.Find(x => x.TaskId == key);
                            if (watcher == null)
                            {
                                HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                                break;
                            }                               

                            tag = dbTopology.WhoseTaskIsIt(watcher, ServerStore.IsPassive());

                            var replicationTaskInfo = new OngoingTaskReplication
                            {
                                TaskId = watcher.TaskId,
                                TaskName = watcher.Name,
                                ResponsibleNode = new NodeId
                                {
                                    NodeTag = tag,
                                    NodeUrl = clusterTopology.GetUrlFromTag(tag)
                                },
                                DestinationDatabase = watcher.Database,
                                TaskState = watcher.Disabled ? OngoingTaskState.Disabled : OngoingTaskState.Enabled,
                                DestinationUrl = watcher.Url
                            };

                            WriteResult(context, replicationTaskInfo);

                            break;
                           
                        case OngoingTaskType.Backup:

                            var backup = record?.PeriodicBackups?.Find(x => x.TaskId == key);
                            if (backup == null)
                            {
                                HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                                break;
                            }

                            tag = dbTopology?.WhoseTaskIsIt(backup, ServerStore.IsPassive());
                            var backupDestinations = GetBackupDestinations(backup);

                            var backupTaskInfo = new OngoingTaskBackup
                            {
                                TaskId = backup.TaskId,
                                BackupType = backup.BackupType,
                                TaskName = backup.Name,
                                TaskState = backup.Disabled ? OngoingTaskState.Disabled : OngoingTaskState.Enabled,
                                ResponsibleNode = new NodeId
                                {
                                    NodeTag = tag,
                                    NodeUrl = clusterTopology.GetUrlFromTag(tag)
                                },
                                BackupDestinations = backupDestinations
                            };

                            WriteResult(context, backupTaskInfo);
                            break;

                        case OngoingTaskType.SqlEtl:

                            var sqlEtl = record?.SqlEtls?.Find(x => x.TaskId == key);
                            if (sqlEtl == null)
                            {
                                HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                                break;
                            }

                            tag = dbTopology?.WhoseTaskIsIt(sqlEtl, ServerStore.IsPassive());
                            var taskState = OngoingTaskState.Enabled;

                            if (sqlEtl.Disabled || sqlEtl.Transforms.All(x => x.Disabled))
                                taskState = OngoingTaskState.Disabled;
                            else if (sqlEtl.Transforms.Any(x => x.Disabled))
                                taskState = OngoingTaskState.PartiallyEnabled;

                            if (record.SqlConnectionStrings.TryGetValue(sqlEtl.ConnectionStringName, out var sqlConnection) == false)
                                throw new InvalidOperationException(
                                    $"Could not find connection string named '{sqlEtl.ConnectionStringName}' in the database record for '{sqlEtl.Name}' ETL");


                            var (database, server) =
                                SqlConnectionStringParser.GetDatabaseAndServerFromConnectionString(sqlEtl.FactoryName,
                                    sqlConnection.ConnectionString);

                            var sqlTaskInfo = new OngoingSqlEtl
                            {
                                TaskId = sqlEtl.TaskId,
                                TaskState = taskState,
                                TaskName = sqlEtl.Name,
                                ResponsibleNode = new NodeId
                                {
                                    NodeTag = tag,
                                    NodeUrl = clusterTopology.GetUrlFromTag(tag)
                                },
                                DestinationServer = server,
                                DestinationDatabase = database
                            };

                            WriteResult(context, sqlTaskInfo);
                            break;

                        case OngoingTaskType.RavenEtl:

                            var ravenEtl = record?.RavenEtls?.Find(x => x.TaskId == key);
                            if (ravenEtl == null)
                            {
                                HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                                break;
                            }

                            tag = dbTopology?.WhoseTaskIsIt(ravenEtl, ServerStore.IsPassive());
                            taskState = OngoingTaskState.Enabled;

                            if (ravenEtl.Disabled || ravenEtl.Transforms.All(x => x.Disabled))
                                taskState = OngoingTaskState.Disabled;
                            else if (ravenEtl.Transforms.Any(x => x.Disabled))
                                taskState = OngoingTaskState.PartiallyEnabled;

                            if (record.RavenConnectionStrings.TryGetValue(ravenEtl.ConnectionStringName, out var connection) == false)
                                throw new InvalidOperationException(
                                    $"Could not find connection string named '{ravenEtl.ConnectionStringName}' in the database record for '{ravenEtl.Name}' ETL");

                            var ravenTaskInfo = new OngoingRavenEtl
                            {
                                TaskId = ravenEtl.TaskId,
                                TaskState = taskState,
                                TaskName = ravenEtl.Name,
                                ResponsibleNode = new NodeId
                                {
                                    NodeTag = tag,
                                    NodeUrl = clusterTopology.GetUrlFromTag(tag)
                                },
                                DestinationUrl = connection.Url,
                                DestinationDatabase = connection.Database
                            };

                            WriteResult(context, ravenTaskInfo);
                            break;
                            
                        case OngoingTaskType.Subscription:

                            var nameKey = GetQueryStringValueAndAssertIfSingleAndNotEmpty("taskName"); 
                            var itemKey = SubscriptionState.GenerateSubscriptionItemKeyName(record.DatabaseName, nameKey);
                            var doc = ServerStore.Cluster.Read(context, itemKey);
                            if (doc == null)
                            {
                                HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                                break;
                            }

                            var subscriptionState = JsonDeserializationClient.SubscriptionState(doc);
                            WriteResult(context, subscriptionState.ToJson());
                            
                            break;

                        default:
                            HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                            break;
                    }
                }
            }

            return Task.CompletedTask;
        }

        private void WriteResult(TransactionOperationContext context, OngoingTask taskInfo)
        {
            HttpContext.Response.StatusCode = (int)HttpStatusCode.OK;

            using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                context.Write(writer, taskInfo.ToJson());
                writer.Flush();
            }
        }

        private void WriteResult(TransactionOperationContext context, DynamicJsonValue dynamicJsonValue)
        {
            HttpContext.Response.StatusCode = (int)HttpStatusCode.OK;

            using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                context.Write(writer, dynamicJsonValue);
                writer.Flush();
            }
        }

        [RavenAction("/tasks/state", "POST", AuthorizationStatus.DatabaseAdmin)]
        public async Task ToggleTaskState()
        {
            var dbName = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");

            if (TryGetAllowedDbs(dbName, out var _, requireAdmin:true) == false)
                return;

            if (ResourceNameValidator.IsValidResourceName(dbName, ServerStore.Configuration.Core.DataDirectory.FullPath, out string errorMessage) == false)
                throw new BadRequestException(errorMessage);

            var key = GetLongQueryString("key");
            var typeStr = GetQueryStringValueAndAssertIfSingleAndNotEmpty("type");
            var disable = GetBoolValueQueryString("disable", required: true) ?? true;
            var taskName = GetStringQueryString("taskName", required: false);

            if (Enum.TryParse<OngoingTaskType>(typeStr, true, out var type) == false)
                throw new ArgumentException($"Unknown task type: {type}", nameof(type));

            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                var (index, _) = await ServerStore.ToggleTaskState(key, taskName, type, disable, dbName);
                await ServerStore.Cluster.WaitForIndexNotification(index); 
                
                HttpContext.Response.StatusCode = (int)HttpStatusCode.OK; 

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    context.Write(writer, new DynamicJsonValue
                    {
                        [nameof(ModifyOngoingTaskResult.TaskId)] = key,
                        [nameof(ModifyOngoingTaskResult.RaftCommandIndex)] = index
                    });
                    writer.Flush();
                }
            }
        }

        [RavenAction("/admin/external-replication", "POST", AuthorizationStatus.DatabaseAdmin)]
        public async Task UpdateExternalReplication()
        {
            var name = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");

            if (TryGetAllowedDbs(name, out var _, requireAdmin:true) == false)
                return;

            if (ResourceNameValidator.IsValidResourceName(name, ServerStore.Configuration.Core.DataDirectory.FullPath, out string errorMessage) == false)
                throw new BadRequestException(errorMessage);

            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                var updateJson = await context.ReadForMemoryAsync(RequestBodyStream(), "read-update-replication");
                if (updateJson.TryGet(nameof(UpdateExternalReplicationCommand.Watcher), out BlittableJsonReaderObject watcherBlittable) == false)
                {
                    throw new InvalidDataException($"{nameof(UpdateExternalReplicationCommand.Watcher)} was not found.");
                }
               
                var watcher = JsonDeserializationClient.ExternalReplication(watcherBlittable);
                var (index, _) = await ServerStore.UpdateExternalReplication(name, watcher);
                await ServerStore.Cluster.WaitForIndexNotification(index);

                string responsibleNode;
                using (context.OpenReadTransaction())
                {
                    var record = ServerStore.Cluster.ReadDatabase(context,name);
                    responsibleNode = record.Topology.WhoseTaskIsIt(watcher, ServerStore.IsPassive());
                }

                HttpContext.Response.StatusCode = (int)HttpStatusCode.Created;

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    context.Write(writer, new DynamicJsonValue
                    {
                        [nameof(ModifyOngoingTaskResult.TaskId)] = watcher.TaskId == 0 ? index : watcher.TaskId, 
                        [nameof(ModifyOngoingTaskResult.RaftCommandIndex)] = index, 
                        [nameof(OngoingTask.ResponsibleNode)] = responsibleNode
                    });
                    writer.Flush();
                }
            }
        }

        [RavenAction("/admin/tasks", "DELETE", AuthorizationStatus.DatabaseAdmin)]
        public async Task DeleteOngoingTask()
        {
            var dbName = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");

            if (TryGetAllowedDbs(dbName, out var _, requireAdmin:true) == false)
                return;

            if (ResourceNameValidator.IsValidResourceName(dbName, ServerStore.Configuration.Core.DataDirectory.FullPath, out string errorMessage) == false)
                throw new BadRequestException(errorMessage);

            var id = GetLongQueryString("id");
            var typeStr = GetQueryStringValueAndAssertIfSingleAndNotEmpty("type");
            var taskName = GetStringQueryString("taskName", required: false);

            if (Enum.TryParse<OngoingTaskType>(typeStr, true, out var type) == false)
                throw new ArgumentException($"Unknown task type: {type}", "type");

            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                var (index, _) = await ServerStore.DeleteOngoingTask(id, taskName, type, dbName);
                await ServerStore.Cluster.WaitForIndexNotification(index);

                HttpContext.Response.StatusCode = (int)HttpStatusCode.OK;

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    context.Write(writer, new DynamicJsonValue
                    {
                        [nameof(ModifyOngoingTaskResult.TaskId)] = id,
                        [nameof(ModifyOngoingTaskResult.RaftCommandIndex)] = index
                    });
                    writer.Flush();
                }
            }
        }

        [RavenAction("/operations/state", "GET", AuthorizationStatus.ValidUser)]
        public Task State()
        {
            var id = GetLongQueryString("id");
            // ReSharper disable once PossibleInvalidOperationException
            var operation = ServerStore.Operations.GetOperation(id);
            if (operation == null)
            {
                HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return Task.CompletedTask;
            }

            if (operation.Database == null) // server level op
            {
                if (TryGetServerAdmin() == false)
                    return Task.CompletedTask;
            }
            else if (TryGetAllowedDbs(operation.Database.Name, out var _, requireAdmin: false) == false)
            {
                return Task.CompletedTask;
            }
            
            var state = operation.State;
            
            

            using (ServerStore.ContextPool.AllocateOperationContext(out JsonOperationContext context))
            {
                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    context.Write(writer, state.ToJson());
                }
            }

            return Task.CompletedTask;
        }
    }

    public class OngoingTasksResult : IDynamicJson
    {
        public List<OngoingTask> OngoingTasksList { get; set; }
        public int SubscriptionsCount { get; set; }

        public OngoingTasksResult()
        {
            OngoingTasksList = new List<OngoingTask>();
        }

        public DynamicJsonValue ToJson()
        {
            return new DynamicJsonValue
            {
                [nameof(OngoingTasksList)] = new DynamicJsonArray(OngoingTasksList.Select(x => x.ToJson())),
                [nameof(SubscriptionsCount)] = SubscriptionsCount
            };
        }
    }

    public abstract class OngoingTask : IDynamicJson // Common info for all tasks types - used for Ongoing Tasks List View in studio
    {
        public long TaskId { get; set; }
        public OngoingTaskType TaskType { get; protected set; }
        public NodeId ResponsibleNode { get; set; }
        public OngoingTaskState TaskState { get; set; }
        public OngoingTaskConnectionStatus TaskConnectionStatus { get; set; }
        public string TaskName { get; set; }

        public virtual DynamicJsonValue ToJson()
        {
            return new DynamicJsonValue
            {
                [nameof(TaskId)] = TaskId,
                [nameof(TaskType)] = TaskType,
                [nameof(ResponsibleNode)] = ResponsibleNode?.ToJson(),
                [nameof(TaskState)] = TaskState,
                [nameof(TaskConnectionStatus)] = TaskConnectionStatus,
                [nameof(TaskName)] = TaskName
        };
        }
    }
    
    public class OngoingTaskSubscription : OngoingTask 
    {
        public OngoingTaskSubscription()
        {
            TaskType = OngoingTaskType.Subscription;
        }
       
        public string Collection { get; set; }
        public DateTime TimeOfLastClientActivity { get; set; }


        public override DynamicJsonValue ToJson()
        {
            var json = base.ToJson();
            json[nameof(Collection)] = Collection;
            json[nameof(TimeOfLastClientActivity)] = TimeOfLastClientActivity;
            return json;
        }
    }

    public class OngoingTaskReplication : OngoingTask
    {
        public OngoingTaskReplication()
        {
            TaskType = OngoingTaskType.Replication;
        }

        public string DestinationUrl { get; set; }
        public string DestinationDatabase { get; set; }

        public override DynamicJsonValue ToJson()
        {
            var json = base.ToJson();
            json[nameof(DestinationUrl)] = DestinationUrl;
            json[nameof(DestinationDatabase)] = DestinationDatabase;
            return json;
        }
    }

    public class OngoingRavenEtl : OngoingTask
    {
        public OngoingRavenEtl()
        {
            TaskType = OngoingTaskType.RavenEtl;
        }

        public string DestinationUrl { get; set; }

        public string DestinationDatabase { get; set; }

        public override DynamicJsonValue ToJson()
        {
            var json = base.ToJson();
            json[nameof(DestinationUrl)] = DestinationUrl;
            json[nameof(DestinationDatabase)] = DestinationDatabase;
            return json;
        }
    }

    public class OngoingSqlEtl : OngoingTask
    {
        public OngoingSqlEtl()
        {
            TaskType = OngoingTaskType.SqlEtl;
        }

        public string DestinationServer { get; set; }
        public string DestinationDatabase { get; set; }

        public override DynamicJsonValue ToJson()
        {
            var json = base.ToJson();
            json[nameof(DestinationServer)] = DestinationServer;
            json[nameof(DestinationDatabase)] = DestinationDatabase;
            return json;
        }
    }

    public class OngoingTaskBackup : OngoingTask
    {
        public BackupType BackupType { get; set; }
        public List<string> BackupDestinations { get; set; }

        public OngoingTaskBackup()
        {
            TaskType = OngoingTaskType.Backup;
        }

        public override DynamicJsonValue ToJson()
        {
            var json = base.ToJson();
            json[nameof(BackupType)] = BackupType;
            json[nameof(BackupDestinations)] = new DynamicJsonArray(BackupDestinations);
            return json;
        }
    }
}
