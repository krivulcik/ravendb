﻿using System;

using Raven.Server.Json;

namespace Raven.Server.Documents.Indexes
{
    public class MapIndex : MapIndex<IndexDefinitionBase>
    {
        protected MapIndex(int indexId, IndexType type)
            : base(indexId, type, null)
        {
        }
    }

    public abstract class MapIndex<TIndexDefinition> : Index<TIndexDefinition> 
        where TIndexDefinition : IndexDefinitionBase
    {
        protected MapIndex(int indexId, IndexType type, TIndexDefinition definition)
            : base(indexId, type, definition)
        {
        }

        protected override bool IsStale(RavenOperationContext databaseContext, RavenOperationContext indexContext, out long lastEtag)
        {
            long lastDocumentEtag;
            using (var tx = databaseContext.Environment.ReadTransaction())
            {
                lastDocumentEtag = DocumentsStorage.ReadLastEtag(tx);
            }

            long lastMappedEtag;
            using (var tx = indexContext.Environment.ReadTransaction())
            {
                lastMappedEtag = ReadLastMappedEtag(tx);
            }

            lastEtag = lastMappedEtag;
            return lastDocumentEtag > lastMappedEtag;
        }

        protected override Lucene.Net.Documents.Document ConvertDocument(string collection, Document document)
        {
            throw new NotImplementedException();
        }
    }
}