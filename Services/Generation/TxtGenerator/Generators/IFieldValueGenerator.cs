using System.Collections.Generic;
using System.Threading.Tasks;
using LayoutParserApi.Services.Generation.TxtGenerator.Models;

namespace LayoutParserApi.Services.Generation.TxtGenerator.Generators
{
    /// <summary>
    /// Interface para geradores de valores de campos
    /// </summary>
    public interface IFieldValueGenerator
    {
        string GenerateValue(FieldDefinition field, int recordIndex, Dictionary<string, object> context = null);
    }

    /// <summary>
    /// Interface assíncrona para geradores que usam IA
    /// </summary>
    public interface IAsyncFieldValueGenerator : IFieldValueGenerator
    {
        Task<string> GenerateValueAsync(FieldDefinition field, int recordIndex, Dictionary<string, object> context = null);
    }
}

