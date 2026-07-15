using System;

namespace FullVid.Models
{
    // One structured YouTube search hit parsed from a yt-dlp --dump-json line.
    public class VideoResult
    {
        public string Id;
        public string Title;
        public TimeSpan Duration;
        public string ThumbnailUrl;
        public string Channel;
        public long ViewCount;
        public string WebpageUrl;
    }
}
