using System;
using FullVid.Common;
using NUnit.Framework;

namespace FullVid.Tests
{
    [TestFixture]
    public class EmlPathsTests
    {
        // Locks the ExtraMetadataLoader on-disk contract: <config>\ExtraMetadata\games\<guid>\VideoTrailer.mp4
        // with EXACT casing (ExtraMetadata Pascal, games lower, VideoTrailer.mp4) and a brace-less GUID.
        [Test]
        public void GetVideoTrailerPath_BuildsEmlContract()
        {
            var id = Guid.Parse("4552c5a8-679e-4c65-ab04-789e3898b032");
            var p = EmlPaths.GetVideoTrailerPath(@"C:\Users\x\AppData\Roaming\Playnite", id);
            Assert.That(p, Is.EqualTo(@"C:\Users\x\AppData\Roaming\Playnite\ExtraMetadata\games\4552c5a8-679e-4c65-ab04-789e3898b032\VideoTrailer.mp4"));
        }
    }
}
