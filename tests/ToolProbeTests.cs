using System.IO;
using FullVid.Common;
using NUnit.Framework;

namespace FullVid.Tests
{
    [TestFixture]
    public class ToolProbeTests
    {
        // A real yt-dlp is at this path on the dev machine and prints a single line
        // like "2025.11.12" for --version. If it's absent the version-parse test is
        // ignored (the missing-path test always runs — it needs no binary).
        private const string RealYtDlpPath = @"C:\Users\asad2\Downloads\yt-dlp.exe";

        [Test]
        public void ProbeVersion_ValidExe_ReturnsVersionString()
        {
            if (!File.Exists(RealYtDlpPath))
                Assert.Ignore("Real yt-dlp.exe not present at " + RealYtDlpPath);

            var probe = new ToolProbe();
            var status = probe.Probe(RealYtDlpPath, "--version");

            Assert.That(status, Does.StartWith("✓ Found"));
            Assert.That(status, Does.Contain("2025.11.12"));
        }

        [Test]
        public void ProbeVersion_MissingPath_ReturnsNotFound()
        {
            var probe = new ToolProbe();
            Assert.That(probe.Probe(@"C:\nope\yt-dlp.exe", "--version"), Does.StartWith("✗ Not found"));
        }

        [Test]
        public void ProbeVersion_EmptyPath_ReturnsNotFound()
        {
            var probe = new ToolProbe();
            Assert.That(probe.Probe("", "--version"), Does.StartWith("✗ Not found"));
        }
    }
}
