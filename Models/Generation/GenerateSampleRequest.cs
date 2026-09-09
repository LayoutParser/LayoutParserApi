namespace LayoutParserApi.Models.Generation
{
    /// <summary>
    /// Body de <c>POST /api/layouts/{layoutGuid}/generate-sample</c> (issue #355).
    /// </summary>
    public class GenerateSampleRequest
    {
        /// <summary>Quantos registros gerar. Default 1.</summary>
        public int NumberOfRecords { get; set; } = 1;

        /// <summary>
        /// Semente para geração determinística. ⚠️ Ainda NÃO suportado por
        /// <see cref="Interfaces.ISyntheticDataGeneratorService"/> (usa <c>Random</c> sem seed
        /// injetável) — aceito no contrato para não quebrar quem já envia o campo, mas ignorado
        /// com aviso honesto em <c>warnings</c> do response. Ver ADR
        /// docs/architecture/adr-geracao-documento-exemplo-2026-09-09.md.
        /// </summary>
        public int? Seed { get; set; }
    }
}
