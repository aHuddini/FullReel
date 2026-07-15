using System;
using System.IO;

namespace FullVid.Common
{
    // Builds the ExtraMetadataLoader on-disk path where EML looks for a game's video trailer.
    // Contract (exact casing): <config>\ExtraMetadata\games\<guid>\VideoTrailer.mp4 where <guid>
    // is the game id with no braces. The caller MUST pass PlayniteApi.Paths.ConfigurationPath —
    // hardcoding %AppData%\Playnite breaks portable installs.
    public static class EmlPaths
    {
        public static string GetVideoTrailerPath(string configurationPath, Guid gameId)
        {
            return Path.Combine(configurationPath, "ExtraMetadata", "games", gameId.ToString(), "VideoTrailer.mp4");
        }
    }
}
