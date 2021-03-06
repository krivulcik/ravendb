// -----------------------------------------------------------------------
//  <copyright file="ReplicationWithMixedSecurity.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using Raven.Abstractions.Replication;
using Raven.Tests.Common;
using Raven.Tests.Common.Attributes;
using Raven.Tests.Common.Dto;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Raven.Abstractions.Data;
using Raven.Client.Connection;
using Raven.Client.Document;
using Raven.Database.Server;
using Raven.Database.Server.Security;
using Raven.Database.Server.Security.Windows;
using Raven.Json.Linq;
using Raven.Tests.Helpers.Util;

using Xunit;

namespace Raven.Tests.Security
{
    public class ReplicationWithMixedSecurity : ReplicationBase
    {
        public ReplicationWithMixedSecurity()
        {
            FactIfWindowsAuthenticationIsAvailable.LoadCredentials();
        }

        private string apiKey = "test1/ThisIsMySecret";

        private int _storeCounter, _databaseCounter;

        protected override void ModifyStore(DocumentStore store)
        {
            FactIfWindowsAuthenticationIsAvailable.LoadCredentials();

            var isApiStore = _storeCounter % 2 == 0;

            store.Conventions.FailoverBehavior = FailoverBehavior.AllowReadsFromSecondaries;

            if (isApiStore)
            {
                store.Credentials = null;
                store.ApiKey = apiKey;
            }
            else
            {
                store.Credentials = new NetworkCredential(FactIfWindowsAuthenticationIsAvailable.Admin.UserName, FactIfWindowsAuthenticationIsAvailable.Admin.Password, FactIfWindowsAuthenticationIsAvailable.Admin.Domain);
                store.ApiKey = null;
            }

            ConfigurationHelper.ApplySettingsToConventions(store.Conventions);

            _storeCounter++;
        }

        protected override void ConfigureDatabase(Database.DocumentDatabase database, string databaseName = null)
        {
            var isApiDatabase = _databaseCounter % 2 == 0;

            if (isApiDatabase)
            {
                database.Documents.Put(
                    "Raven/ApiKeys/" + apiKey.Split('/')[0],
                    null,
                    RavenJObject.FromObject(
                        new ApiKeyDefinition
                        {
                            Name = apiKey.Split('/')[0],
                            Secret = apiKey.Split('/')[1],
                            Enabled = true,
                            Databases =
                                new List<ResourceAccess>
                                {
                                    new ResourceAccess { TenantId = "*" },
                                    new ResourceAccess { TenantId = Constants.SystemDatabase },
                                    new ResourceAccess {TenantId = databaseName}
                                }
                        }),
                    new RavenJObject(),
                    null);
            }
            else
            {
                database.Documents.Put("Raven/Authorization/WindowsSettings", null,
                                                   RavenJObject.FromObject(new WindowsAuthDocument
                                                   {
                                                       RequiredUsers = new List<WindowsAuthData>
                                                   {
                                                       new WindowsAuthData()
                                                       {
                                                           Name = FactIfWindowsAuthenticationIsAvailable.Admin.UserName,
                                                           Enabled = true,
                                                           Databases = new List<ResourceAccess>
                                                           {
                                                               new ResourceAccess {TenantId = "*"},
                                                               new ResourceAccess {TenantId = Constants.SystemDatabase},
                                                               new ResourceAccess {TenantId = databaseName}
                                                           }
                                                       }
                                                   }
                                                   }), new RavenJObject(), null);
            }

            _databaseCounter++;
        }

        [Fact]
        public void DocumentStoreShouldSwitchFromApiKeyToCredentials()
        {
            var store1 = CreateStore(enableAuthorization: true);
            Authentication.EnableOnce();
            var store2 = CreateStore(enableAuthorization: true, anonymousUserAccessMode: AnonymousUserAccessMode.None);

            TellFirstInstanceToReplicateToSecondInstance(username: FactIfWindowsAuthenticationIsAvailable.Admin.UserName, password: FactIfWindowsAuthenticationIsAvailable.Admin.Password, domain: FactIfWindowsAuthenticationIsAvailable.Admin.Domain, authenticationScheme: store2.Conventions.AuthenticationScheme);

            using (var session = store1.OpenSession())
            {
                session.Store(new Company { Name = "Hibernating Rhinos" });
                session.SaveChanges();
            }

            var company = WaitForDocument<Company>(store2, "companies/1");
            Assert.Equal("Hibernating Rhinos", company.Name);

            var serverClient = ((ServerClient)store1.DatabaseCommands);
            GetReplicationInformer(serverClient).RefreshReplicationInformation(serverClient);

            servers[0].Dispose();

            using (var session = store1.OpenSession())
            {
                Assert.NotNull(session.Load<Company>(1));
            }
        }

        [Fact]
        public async Task DocumentStoreShouldSwitchFromApiKeyToCredentialsAsync()
        {
            var store1 = CreateStore(enableAuthorization: true);
            Authentication.EnableOnce();
            var store2 = CreateStore(enableAuthorization: true, anonymousUserAccessMode: AnonymousUserAccessMode.None);

            TellFirstInstanceToReplicateToSecondInstance(username: FactIfWindowsAuthenticationIsAvailable.Admin.UserName, password: FactIfWindowsAuthenticationIsAvailable.Admin.Password, domain: FactIfWindowsAuthenticationIsAvailable.Admin.Domain, authenticationScheme: store2.Conventions.AuthenticationScheme);

            using (var session = store1.OpenAsyncSession())
            {
                await session.StoreAsync(new Company { Name = "Hibernating Rhinos" });
                await session.SaveChangesAsync();
            }

            var company = WaitForDocument<Company>(store2, "companies/1");
            Assert.Equal("Hibernating Rhinos", company.Name);

            var serverClient = ((ServerClient)store1.DatabaseCommands);
            GetReplicationInformer(serverClient).RefreshReplicationInformation(serverClient);

            servers[0].Dispose();

            using (var session = store1.OpenAsyncSession())
            {
                Assert.NotNull(await session.LoadAsync<Company>(1));
            }
        }

        [Fact]
        public void DocumentStoreShouldSwitchFromCredentialsToApiKey()
        {
            var store1 = CreateStore(enableAuthorization: true);
            Authentication.EnableOnce();
            var store2 = CreateStore(enableAuthorization: true, anonymousUserAccessMode: AnonymousUserAccessMode.None);

            TellSecondInstanceToReplicateToFirstInstance(apiKey);

            using (var session = store2.OpenSession())
            {
                session.Store(new Company { Name = "Hibernating Rhinos" });
                session.SaveChanges();
            }

            var company = WaitForDocument<Company>(store1, "companies/1");
            Assert.Equal("Hibernating Rhinos", company.Name);

            var serverClient = ((ServerClient)store2.DatabaseCommands);
            GetReplicationInformer(serverClient).RefreshReplicationInformation(serverClient);

            servers[1].Dispose();

            using (var session = store2.OpenSession())
            {
                Assert.NotNull(session.Load<Company>(1));
            }
        }

        [Fact]
        public async Task DocumentStoreShouldSwitchFromCredentialsToApiKeyAsync()
        {
            var store1 = CreateStore(enableAuthorization: true);
            Authentication.EnableOnce();
            var store2 = CreateStore(enableAuthorization: true, anonymousUserAccessMode: AnonymousUserAccessMode.None);

            TellSecondInstanceToReplicateToFirstInstance(apiKey);

            using (var session = store2.OpenAsyncSession())
            {
                await session.StoreAsync(new Company { Name = "Hibernating Rhinos" });
                await session.SaveChangesAsync();
            }

            var company = WaitForDocument<Company>(store1, "companies/1");
            Assert.Equal("Hibernating Rhinos", company.Name);

            var serverClient = ((ServerClient)store2.DatabaseCommands);
            GetReplicationInformer(serverClient).RefreshReplicationInformation(serverClient);

            servers[1].Dispose();

            using (var session = store2.OpenAsyncSession())
            {
                Assert.NotNull(await session.LoadAsync<Company>(1));
            }
        }
    }
}
