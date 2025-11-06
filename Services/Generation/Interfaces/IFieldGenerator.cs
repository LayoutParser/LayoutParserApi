namespace LayoutParserApi.Services.Generation.Interfaces
{
    /// <summary>
    /// Interface para geradores de campos específicos
    /// </summary>
    public interface IFieldGenerator
    {
        /// <summary>
        /// Verifica se este gerador pode gerar o campo especificado
        /// </summary>
        bool CanGenerate(string fieldName, int length, string dataType = null);

        /// <summary>
        /// Gera o valor do campo
        /// </summary>
        string Generate(string fieldName, int length, string alignment, int recordIndex = 0, Dictionary<string, object> context = null);
    }
}

