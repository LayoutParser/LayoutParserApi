using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Parsing.Implementations;

namespace LayoutParserApi.Tests.Parsing
{
    public class IdocLineParsingTests
    {
        private const string SyntheticIdoc =
            "EDI_DC40 SYNTHETIC_CONTROL\r\n" +
            "ZRSDM_NFE_HEADER SYNTHETIC_HEADER\r\n" +
            "ZRSDM_NFE_ITEM SYNTHETIC_ITEM";

        [Fact]
        public void Detector_e_splitter_reconhecem_idoc_por_linhas()
        {
            var detector = new LayoutDetector();
            var detectedType = detector.DetectType(SyntheticIdoc);

            var splitter = new LineSplitter(new NullTechLogger());
            var lines = splitter.SplitTextIntoLines(SyntheticIdoc, detectedType);

            Assert.Equal("idoc", detectedType);
            Assert.Equal(3, lines.Length);
            Assert.StartsWith("EDI_DC40", lines[0]);
            Assert.StartsWith("ZRSDM_NFE_HEADER", lines[1]);
            Assert.StartsWith("ZRSDM_NFE_ITEM", lines[2]);
        }

        private sealed class NullTechLogger : ITechLogger
        {
            public void LogTechnical(LogEntry entry)
            {
            }
        }
    }
}
