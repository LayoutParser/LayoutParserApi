namespace LayoutParserApi.Services.Generation.Interfaces
{
    /// <summary>
    /// Gerador de valor sintético por TIPO LÓGICO (cpf, cnpj, date, decimal, text…), compartilhado
    /// entre o caminho <c>TextPositional</c> (<see cref="Implementations.SyntheticDataGeneratorService"/>)
    /// e o caminho <c>Xml</c> (<see cref="Implementations.XmlSampleDocumentGeneratorService"/>) da
    /// geração de documento de exemplo (issues #355/#356).
    ///
    /// Nome distinto de <c>TxtGenerator.Generators.Interfaces.IFieldValueGenerator</c> DE PROPÓSITO:
    /// aquele é acoplado a <c>FieldDefinition</c> + <c>recordIndex</c> e vive só no pipeline TXT; este
    /// é uma folha determinística por nome/tipo, sem modelo de campo. Os dígitos verificadores de
    /// CPF/CNPJ são os módulo 11 reais (mesma implementação validada na issue #357).
    /// </summary>
    public interface ITypedValueGenerator
    {
        /// <summary>
        /// Infere o tipo lógico a partir do nome do campo. Se <paramref name="explicitType"/> vier
        /// preenchido, ele vence a heurística (retornado em minúsculas).
        /// </summary>
        string InferType(string? fieldName, string? explicitType = null);

        /// <summary>
        /// Gera um valor sintético para o tipo lógico dado.
        /// <paramref name="length"/> &gt; 0 aplica alinhamento/padding (uso posicional);
        /// 0 devolve o valor "cru", sem padding (uso XML).
        /// </summary>
        string Generate(string fieldType, int length = 0, string? context = null);
    }
}
