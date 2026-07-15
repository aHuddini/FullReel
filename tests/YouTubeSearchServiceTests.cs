using System;
using FullVid.Services;
using NUnit.Framework;

namespace FullVid.Tests
{
    [TestFixture]
    public class YouTubeSearchServiceTests
    {
        [Test]
        public void ParseSearchJson_RealYtDlpLine_MapsAllFields()
        {
            var line = "{\"id\":\"UAO2urG23S4\",\"title\":\"Hollow Knight - Release Trailer\",\"duration\":124.0,\"channel\":\"Team Cherry\",\"view_count\":6030428,\"thumbnails\":[{\"url\":\"https://i.ytimg.com/vi/UAO2urG23S4/hq720.jpg\"}],\"webpage_url\":\"https://www.youtube.com/watch?v=UAO2urG23S4\"}";
            var results = YouTubeSearchService.ParseSearchJson(new[] { line });
            Assert.That(results, Has.Count.EqualTo(1));
            var r = results[0];
            Assert.That(r.Id, Is.EqualTo("UAO2urG23S4"));
            Assert.That(r.Title, Is.EqualTo("Hollow Knight - Release Trailer"));
            Assert.That(r.Duration, Is.EqualTo(TimeSpan.FromSeconds(124)));
            Assert.That(r.Channel, Is.EqualTo("Team Cherry"));
            Assert.That(r.ViewCount, Is.EqualTo(6030428));
            Assert.That(r.ThumbnailUrl, Is.EqualTo("https://i.ytimg.com/vi/UAO2urG23S4/hq720.jpg"));
        }

        [Test]
        public void ParseSearchJson_MissingOptionalFields_DoesNotThrow()
        {
            var line = "{\"id\":\"x\",\"title\":\"t\"}";
            var r = YouTubeSearchService.ParseSearchJson(new[] { line })[0];
            Assert.That(r.Id, Is.EqualTo("x"));
            Assert.That(r.Duration, Is.EqualTo(TimeSpan.Zero));
            Assert.That(r.ViewCount, Is.EqualTo(0));
        }

        [Test]
        public void ParseSearchJson_BlankLines_Skipped()
        {
            Assert.That(YouTubeSearchService.ParseSearchJson(new[] { "", "  " }), Is.Empty);
        }

        [Test]
        public void ParseSearchJson_ChannelFallsBackToUploader()
        {
            var line = "{\"id\":\"x\",\"title\":\"t\",\"uploader\":\"Some Uploader\"}";
            var r = YouTubeSearchService.ParseSearchJson(new[] { line })[0];
            Assert.That(r.Channel, Is.EqualTo("Some Uploader"));
        }

        [Test]
        public void ParseSearchJson_MalformedLine_Skipped()
        {
            var good = "{\"id\":\"ok\",\"title\":\"t\"}";
            var results = YouTubeSearchService.ParseSearchJson(new[] { "{not json", good });
            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].Id, Is.EqualTo("ok"));
        }
    }
}
