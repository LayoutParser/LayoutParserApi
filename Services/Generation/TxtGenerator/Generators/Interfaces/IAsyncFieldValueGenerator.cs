using LayoutParserApi.Services.Generation.TxtGenerator.Models;

namespace LayoutParserApi.Services.Generation.TxtGenerator.Generators.Interfaces
{
    /// <summary>
    /// Interface assíncrona para geradores que usam IA
    /// </summary>
    public interface IAsyncFieldValueGenerator : IFieldValueGenerator
    {
        Task<string> GenerateValueAsync(FieldDefinition field, int recordIndex, Dictionary<string, object> context = null);
    }
}
