namespace LayoutParserApi.Models.Entities
{
    /// <summary>
    /// Resultado da reconstrução reversa best-effort XML→TXT (issue #151, Fase 4). Deliberadamente
    /// NÃO promete round-trip perfeito — o TXT reconstruído vem sempre acompanhado de
    /// <see cref="Warnings"/> declarando onde a fidelidade não pôde ser garantida (campo ausente no
    /// XML, valor truncado por exceder o tamanho da posição declarada no layout, campo derivado sem
    /// caminho reverso determinístico).
    /// </summary>
    public class ReconstructionResult
    {
        /// <summary>TXT posicional reconstruído (uma linha por <c>LineElement</c>/ocorrência), com as
        /// posições preenchidas a partir do XML e o restante da largura da linha em branco.</summary>
        public string ReconstructedText { get; set; } = string.Empty;

        public List<ReconstructionWarning> Warnings { get; set; } = new();

        /// <summary>Contagem de campos do layout que efetivamente foram escritos no TXT reconstruído
        /// (a partir de um valor achado no XML) — numerador honesto da taxa de sucesso do relatório
        /// de viabilidade.</summary>
        public int FieldsReconstructed { get; set; }

        /// <summary>Total de campos do layout considerados na tentativa — denominador da taxa de
        /// sucesso (<c>FieldsReconstructed / FieldsAttempted</c>).</summary>
        public int FieldsAttempted { get; set; }
    }

    /// <summary>Um alerta específico de um campo/linha que não pôde ser reconstruído com confiança
    /// total — contrato de saída explicitamente "best-effort" (issue #151, riscos §2).</summary>
    public class ReconstructionWarning
    {
        public string LineName { get; set; } = string.Empty;
        public string FieldName { get; set; } = string.Empty;
        public int Occurrence { get; set; }
        public ReconstructionWarningKind Kind { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public enum ReconstructionWarningKind
    {
        /// <summary>Nenhum valor encontrado no XML para o campo (XPath não resolveu nenhum nó).</summary>
        FieldNotFoundInXml,

        /// <summary>Valor encontrado excede o tamanho da posição declarada no layout — truncado.</summary>
        ValueTruncated,

        /// <summary>Mapeamento com múltiplas origens/computado (concatenação, função XSLT etc.) — não
        /// tem caminho reverso determinístico, não é erro fatal, só fica sem reconstrução.</summary>
        NotDeterministicallyReversible,

        /// <summary>Falha inesperada ao processar este campo específico — isolada, não aborta o resto
        /// da reconstrução (mesmo princípio de isolamento por item já usado em
        /// <see cref="Services.Transformation.StructuralResolution.FieldMappingCompositionService"/>).</summary>
        ProcessingError
    }
}
