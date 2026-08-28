namespace LayoutParserApi.Models.Transformation
{
    /// <summary>
    /// Um candidato de transformação, normalizado a partir de QUALQUER um dos dois pathways
    /// (sysmiddle/low-code ou tcl-xsl/canônico), para consumo pelo front-end via
    /// <c>POST /api/transformation-execution/execute-candidates</c>. Ver contrato em
    /// docs/architecture/multi-candidato-e-diagnostico-ia-contrato.md (Gap 1).
    /// </summary>
    public class TransformationCandidate
    {
        public string CandidateId { get; set; } = "";

        /// <summary>"sysmiddle" | "tcl-xsl"</summary>
        public string Pathway { get; set; } = "";

        public string TransformedXml { get; set; } = "";

        public double? Score { get; set; }

        public Dictionary<string, string>? SegmentMappings { get; set; }

        /// <summary>
        /// Mapeamentos campo-a-campo (issue #141), compostos por <see cref="LayoutParserApi.Services.Transformation.StructuralResolution.FieldMappingCompositionService"/>
        /// sobre o mesmo <c>Layout</c>/<c>MapperVo</c> já usados para produzir <see cref="TransformedXml"/>
        /// (pathway sysmiddle). <c>null</c> quando: (a) pathway é <c>tcl-xsl</c> (decisão categórica, sem
        /// fonte estrutural equivalente hoje — mesma decisão de <c>SegmentMappings</c> para esse pathway);
        /// (b) a composição falhou isoladamente (nunca derruba o candidato, vira warning); ou (c) o parse
        /// posicional compartilhado do documento falhou. Lista vazia (não nula) é resultado válido: mapper
        /// existe mas não resolveu nenhum <c>FieldToXmlMapping</c>.
        /// </summary>
        public IReadOnlyList<XslSynth.Model.FieldToXmlMapping>? FieldMappings { get; set; }

        public object? Validation { get; set; }

        /// <summary>Preenchido só quando o candidato falhou parcialmente — hoje não usado no array final
        /// (candidatos que falham não aparecem no array, ver tabela de decisão do contrato), mas mantido
        /// no schema porque o contrato o define explicitamente.</summary>
        public string? FailureReason { get; set; }

        /// <summary>
        /// Fase 0 do contrato de rastreabilidade TXT↔XML (issue #138/#126), granularidade de
        /// LINHA/SEÇÃO — NÃO campo (isso é #140/#141). Semântica obrigatória:
        /// <list type="bullet">
        /// <item><c>null</c> = este pathway não suporta rastreabilidade ainda (hoje: <c>tcl-xsl</c>).</item>
        /// <item><c>[]</c> (lista vazia) = pathway suporta, mas não encontrou mapeamentos estruturais
        /// resolvíveis para este candidato específico.</item>
        /// <item>lista preenchida = mapeamentos disponíveis, cada um com XPath absoluto e
        /// confiança (ver <see cref="SectionMapping.Confidence"/>).</item>
        /// </list>
        /// Resolução sempre ESTRUTURAL (via GUID/definição declarada do mapper) — nunca por
        /// comparação de valor textual do documento.
        /// </summary>
        public List<SectionMapping>? SectionMappings { get; set; }

        /// <summary>
        /// Namespaces XML usados nos XPaths de <see cref="SectionMappings"/> — reportado UMA VEZ por
        /// candidato (não repetido por mapping). <c>null</c> quando <see cref="SectionMappings"/> também
        /// é <c>null</c>/vazio.
        /// </summary>
        public Dictionary<string, string>? XmlNamespaces { get; set; }
    }

    public class TransformationExecutionCandidatesResponse
    {
        public bool Success { get; set; }
        public List<TransformationCandidate> Candidates { get; set; } = new();
        public string? RecommendedCandidateId { get; set; }
        public List<string> Warnings { get; set; } = new();

        /// <summary>Diagnóstico estruturado por pathway (Issue LayoutParserReact #86) — ADITIVO,
        /// não substitui <see cref="Warnings"/>. Vazio hoje: a população dos valores por pathway
        /// (sysmiddle/tcl-xsl/ai-fallback) é feita em cima desta estrutura, ver
        /// <see cref="PathwayDiagnostic"/>.</summary>
        public List<PathwayDiagnostic> PathwayDiagnostics { get; set; } = new();

        /// <summary>CorrelationId da request (<see cref="LayoutParserApi.Services.Logging.CorrelationContext.CurrentId"/>),
        /// permite ao suporte cruzar com o log estruturado completo (não sanitizado) desta chamada.</summary>
        public string? CorrelationId { get; set; }
    }
}
