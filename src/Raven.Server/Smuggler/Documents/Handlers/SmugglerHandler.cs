// -----------------------------------------------------------------------
//  <copyright file="SmugglerHandler.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Raven.Client.Data;
using Raven.Client.Smuggler;
using Raven.Server.Documents;
using Raven.Server.Routing;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Raven.Server.Smuggler.Documents.Data;
using Sparrow.Json;
using Sparrow.Json.Parsing;

namespace Raven.Server.Smuggler.Documents.Handlers
{
    public class SmugglerHandler : DatabaseRequestHandler
    {

        [RavenAction("/databases/*/smuggler/validateOptions", "POST")]
        public Task PostValidateOptions()
        {
            //TODO: implement me!


            return Task.CompletedTask;
        }

        [RavenAction("/databases/*/smuggler/export", "POST")]
        public async Task PostExport()
        {
            DocumentsOperationContext context;
            using (ContextPool.AllocateOperationContext(out context))
            using (context.OpenReadTransaction())
            {
                var operationId = GetIntValueQueryString("operationId", required: false);

                var exporter = new SmugglerExporter(Database)
                {
                    DocumentsLimit = GetIntValueQueryString("documentsLimit", required: false),
                    RevisionDocumentsLimit = GetIntValueQueryString("RevisionDocumentsLimit", required: false),
                };

                var operateOnTypes = GetStringQueryString("operateOnTypes", required: false);
                DatabaseItemType databaseItemType;
                if (Enum.TryParse(operateOnTypes, true, out databaseItemType))
                {
                    exporter.OperateOnTypes = databaseItemType;
                }

                var token = CreateOperationToken();

                if (operationId.HasValue)
                {
                    await Database.Operations.AddOperation("Export database: " + Database.Name, DatabaseOperations.PendingOperationType.DatabaseExport,
                        onProgress => Task.Run(() => ExportDatabaseInternal(context, exporter, onProgress, token), token.Token), operationId.Value, token);
                }
                else
                {
                    ExportDatabaseInternal(context, exporter, null, token);
                }
            }
        }

        private IOperationResult ExportDatabaseInternal(DocumentsOperationContext context, SmugglerExporter exporter, Action<IOperationProgress> onProgress, OperationCancelToken token)
        {
            try
            {



                //TODO: use optional onProgress parameter
                exporter.Export(context, ResponseBodyStream());
                return null; //TODO: pass operation result to operation status
            }
            finally
            {
                token.Dispose();
            }
        }

        [RavenAction("/databases/*/smuggler/import-s3-dir", "GET")]
        public async Task PostImportFromS3Directory()
        {
            var sp = Stopwatch.StartNew();

            var url = GetQueryStringValueAndAssertIfSingleAndNotEmpty("url");
            using (var httpClient = new HttpClient())
            {
                var result = await httpClient.GetAsync(url);
                var dirTextXml = await result.Content.ReadAsStringAsync();
                var filesListing = XElement.Parse(dirTextXml);
                var ns = XNamespace.Get("http://s3.amazonaws.com/doc/2006-03-01/");
                var urls = from content in filesListing.Elements(ns + "Contents")
                           let requestUri = url.TrimEnd('/') + "/" + content.Element(ns + "Key").Value
                           select (Func<Task<Stream>>)(async () =>
                          {
                              var respone = await httpClient.GetAsync(requestUri);
                              if (respone.IsSuccessStatusCode == false)
                                  throw new InvalidOperationException("Request failed on " + requestUri + " with " +
                                                                      await respone.Content.ReadAsStreamAsync());
                              return await respone.Content.ReadAsStreamAsync();
                          });

                var files = new BlockingCollection<Func<Task<Stream>>>(new ConcurrentQueue<Func<Task<Stream>>>(urls));
                files.CompleteAdding();
                await BulkImport(files, sp, Path.GetTempPath());
            }
        }

        [RavenAction("/databases/*/smuggler/import-dir", "GET")]
        public async Task PostImportDirectory()
        {
            var sp = Stopwatch.StartNew();

            var directory = GetQueryStringValueAndAssertIfSingleAndNotEmpty("dir");
            var files = new BlockingCollection<Func<Task<Stream>>>(new ConcurrentQueue<Func<Task<Stream>>>(
                    Directory.GetFiles(directory, "*.dump")
                        .Select(x => (Func<Task<Stream>>)(() => Task.FromResult<Stream>(File.OpenRead(x)))))
            );
            files.CompleteAdding();
            await BulkImport(files, sp, directory);
        }

        private async Task BulkImport(BlockingCollection<Func<Task<Stream>>> files, Stopwatch sp, string directory)
        {
            var results = new ConcurrentQueue<ImportResult>();
            var tasks = new Task[Environment.ProcessorCount];

            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Task.Run(async () =>
                {
                    while (files.IsCompleted == false)
                    {
                        Func<Task<Stream>> getFile;
                        DocumentsOperationContext context;
                        try
                        {
                            getFile = files.Take();
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                        using (ContextPool.AllocateOperationContext(out context))
                        using (Stream file = await getFile())
                        using (var stream = new GZipStream(file, CompressionMode.Decompress))
                        {
                            var result = await DoImport(context, stream);
                            results.Enqueue(result);
                        }
                    }
                });
            }
            await Task.WhenAll(tasks);

            var finalResult = new ImportResult();
            ImportResult importResult;
            while (results.TryDequeue(out importResult))
            {
                finalResult.DocumentsCount += importResult.DocumentsCount;
                finalResult.IdentitiesCount += importResult.IdentitiesCount;
                finalResult.IndexesCount += importResult.IndexesCount;
                finalResult.RevisionDocumentsCount += importResult.RevisionDocumentsCount;
                finalResult.TransformersCount += importResult.TransformersCount;
                finalResult.Warnings.AddRange(importResult.Warnings);
            }
            sp.Stop();

            DocumentsOperationContext finalContext;
            using (ContextPool.AllocateOperationContext(out finalContext))
            {
                var memoryStream = new MemoryStream();
                WriteImportResult(finalContext, sp, finalResult, memoryStream);
                memoryStream.Position = 0;
                try
                {
                    using (var output = File.Create(Path.Combine(directory, "smuggler.results.txt")))
                    {
                        memoryStream.CopyTo(output);
                    }
                }
                catch (Exception)
                {
                    // ignore any failure here
                }
                memoryStream.Position = 0;
                memoryStream.CopyTo(ResponseBodyStream());
            }
        }

        [RavenAction("/databases/*/smuggler/import", "GET")]
        public Task GetImport()
        {
            if (HttpContext.Request.Query.ContainsKey("file") == false &&
                HttpContext.Request.Query.ContainsKey("url") == false)
            {
                throw new ArgumentException("'file' or 'url' are mandatory when using GET /smuggler/import");
            }
            return PostImport();
        }

        [RavenAction("/databases/*/smuggler/import", "POST")]
        public async Task PostImport()
        {
            DocumentsOperationContext context;
            using (ContextPool.AllocateOperationContext(out context))
            {
                var tuple = await GetImportStream();
                using (tuple.Item2)
                using (var stream = new GZipStream(tuple.Item1, CompressionMode.Decompress))
                {
                    var sp = Stopwatch.StartNew();
                    var result = await DoImport(context, stream);
                    sp.Stop();
                    WriteImportResult(context, sp, result, ResponseBodyStream());
                }
            }
        }

        private void WriteImportResult(DocumentsOperationContext context, Stopwatch sp, ImportResult result, Stream stream)
        {
            using (var writer = new BlittableJsonTextWriter(context, stream))
            {
                context.Write(writer, new DynamicJsonValue
                {
                    ["ElapsedMilliseconds"] = sp.ElapsedMilliseconds,
                    ["Elapsed"] = sp.Elapsed.ToString(),
                    ["DocumentsCount"] = result.DocumentsCount,
                    ["RevisionDocumentsCount"] = result.RevisionDocumentsCount,
                    ["IndexesCount"] = result.IndexesCount,
                    ["IdentitiesCount"] = result.IdentitiesCount,
                    ["TransformersCount"] = result.TransformersCount,
                    ["Warnings"] = new DynamicJsonArray(result.Warnings)
                });
            }
        }

        private async Task<Tuple<Stream, IDisposable>> GetImportStream()
        {
            var file = GetStringQueryString("file", required: false);
            if (string.IsNullOrEmpty(file) == false)
                return Tuple.Create<Stream, IDisposable>(File.OpenRead(file), null);

            var url = GetStringQueryString("url", required: false);
            if (string.IsNullOrEmpty(url) == false)
            {
                var httpClient = new HttpClient();

                var stream = await httpClient.GetStreamAsync(url);
                return Tuple.Create<Stream, IDisposable>(stream, httpClient);
            }

            return Tuple.Create<Stream, IDisposable>(HttpContext.Request.Body, null);
        }

        private async Task<ImportResult> DoImport(DocumentsOperationContext context, Stream stream)
        {
            var importer = new SmugglerImporter(Database);

            var operateOnTypes = GetStringQueryString("operateOnTypes", required: false);
            DatabaseItemType databaseItemType;
            if (Enum.TryParse(operateOnTypes, true, out databaseItemType))
            {
                importer.OperateOnTypes = databaseItemType;
            }

            return await importer.Import(context, stream);
        }
    }
}