using LayoutParserApi.Models.Parsing;

namespace LayoutParserApi.Services.Parsing.Interfaces
{
    public interface IAutomaticLayoutDetectionService
    {
        Task<AutomaticLayoutDetectionResult> DetectAsync(
            string documentContent,
            CancellationToken cancellationToken = default);
    }
}
