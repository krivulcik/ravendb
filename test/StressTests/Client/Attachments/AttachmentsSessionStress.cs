﻿using System.Threading.Tasks;
using FastTests;
using FastTests.Client.Attachments;
using SlowTests.Client.Attachments;
using Tests.Infrastructure;
using Xunit;

namespace StressTests.Client.Attachments
{
    public class AttachmentsSessionStress : NoDisposalNeeded
    {
        [Theory]
        [InlineData(1000)]
        [InlineData(10_000)]
        public void PutLotOfAttachments(int count)
        {
            using (var stress = new AttachmentsSession())
            {
                stress.PutLotOfAttachments(count);
            }
        }

        [Theory]
        [InlineData(10_000)]
        public async Task PutLotOfAttachmentsAsync(int count)
        {
            using (var stress = new AttachmentsSessionAsync())
            {
                await stress.PutLotOfAttachments(count);
            }
        }

        [NightlyBuildTheory]
        [InlineData(1000)]
        [InlineData(10_000)]
        [InlineData(100_000)]
        [InlineData(1_000_000)]
        public void StressPutLotOfAttachments(int count)
        {
            using (var stress = new AttachmentsSession())
            {
                stress.PutLotOfAttachments(count);
            }
        }

        [NightlyBuildTheory]
        [InlineData(10_000)]
        [InlineData(100_000)]
        [InlineData(1_000_000)]
        public async Task StressPutLotOfAttachmentsAsync(int count)
        {
            using (var stress = new AttachmentsSessionAsync())
            {
                await stress.PutLotOfAttachments(count);
            }
        }
    }
}