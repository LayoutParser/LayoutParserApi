using LayoutParserApi.Services.Generation.TxtGenerator.Models;

namespace LayoutParserApi.Services.Generation.TxtGenerator.Generators.Interfaces
{
    /// <summary>
    /// Interface para geradores de valores de campos
    /// </summary>
    public interface IFieldValueGenerator
    {
        string GenerateValue(FieldDefinition field, int recordIndex, Dictionary<string, object> context = null);
    }
}
