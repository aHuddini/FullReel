using System.Collections.Generic;

namespace FullVid.Models
{
    // A search preset shown as a tab in the results dialog. Query = "{gameName} {QuerySuffix}".
    // Trailers is the default (index 0) and uses the user's configurable QueryTemplate.
    public class VideoCategory
    {
        public string Label { get; }
        public string QuerySuffix { get; }

        public VideoCategory(string label, string querySuffix)
        {
            Label = label;
            QuerySuffix = querySuffix;
        }

        // The built-in categories, in tab order. Trailers' suffix is a fallback — FindVideos
        // uses the user's QueryTemplate for it so the existing setting still applies.
        public static IReadOnlyList<VideoCategory> Defaults { get; } = new[]
        {
            new VideoCategory("Trailers", "gameplay trailer"),
            new VideoCategory("Walkthroughs", "walkthrough lets play"),
            new VideoCategory("Reviews", "review"),
            new VideoCategory("Guides", "guide how to")
        };
    }
}
