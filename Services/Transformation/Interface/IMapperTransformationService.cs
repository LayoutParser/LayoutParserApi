using LayoutParserApi.Services.Transformation.Models;

namespace LayoutParserApi.Services.Transformation.Interface
{
    public interface IMapperTransformationService
    {
        Task<TransformationResult> TransformAsync(string inputText, string inputLayoutGuid, string targetLayoutGuid, string mapperXml = null);
    }
}
