﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Raven.Client.Document;

namespace Raven.Client.Shard
{
    public class Shards : List<DocumentStore>
    {
        public Shards()
        {

        }

        public Shards(IEnumerable<DocumentStore> shards) : base(shards)
        {

        }
    }
}
