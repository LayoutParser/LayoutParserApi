namespace LayoutParserApi.Models.Transformation
{
    /// <summary>
    /// Rastreabilidade TXT↔XML em granularidade de LINHA/SEÇÃO (Fase 0 — issue LayoutParserApi #138 /
    /// LayoutParserReact #126). Propriedade ADITIVA de <see cref="TransformationCandidate"/>: nunca
    /// substitui/reduz nada do contrato existente de <c>execute-candidates</c> (Gap 1).
    ///
    /// <para><b>Não é rastreabilidade por CAMPO.</b> Isso é escopo de #140/#141 — este contrato resolve
    /// no máximo "esta LINHA/SEÇÃO do TXT virou este NÓ do XML", nunca "este CAMPO da linha virou este
    /// atributo/elemento". Não usar <see cref="SectionMapping"/> para tentar destacar campo a campo — a
    /// granularidade fina não foi modelada aqui e qualquer heurística feita em cima disso seria uma
    /// aproximação não solicitada. Isso também significa que <see cref="SectionMapping"/> sozinho NÃO
    /// desbloqueia a PBI LayoutParserReact #128 (highlight de campo).</para>
    /// </summary>
    public class SectionMapping
    {
        public SectionMappingSource Source { get; set; } = new();

        public List<SectionMappingTarget> Targets { get; set; } = new();

        /// <summary>
        /// "authoritative" | "best-effort". Critério objetivo (documentado também no resolver que
        /// produz o mapping): <c>authoritative</c> quando a resolução veio 100% de estrutura
        /// DECLARADA no mapper (ex.: XPath derivado da atribuição <c>T.&lt;path&gt;</c> da DSL do
        /// Sysmiddle, sem heurística/adivinhação) — <c>best-effort</c> quando houve fallback/heurística
        /// para preencher uma lacuna que a estrutura declarada não cobre. Fase 0 só emite mappings
        /// quando consegue <c>authoritative</c>; nunca inventa <c>best-effort</c> por aproximação de
        /// valor (violaria a regra "nunca comparar valor textual pra localizar nó").
        /// </summary>
        public string Confidence { get; set; } = "authoritative";
    }

    public class SectionMappingSource
    {
        /// <summary>
        /// GUID estável da linha/seção na origem, quando disponível na estrutura declarada do mapper
        /// (ex.: <c>ElementGuid</c> da regra do Sysmiddle). Pode ser <c>null</c> quando o pathway só
        /// consegue resolver o nome da linha (ver <see cref="LineName"/>), não o GUID — isso NÃO torna
        /// o mapping menos estrutural, o nome de linha já é um identificador estável da definição do
        /// layout (não é valor de conteúdo do documento).
        /// </summary>
        public string? LineGuid { get; set; }

        public string LineName { get; set; } = "";

        /// <summary>
        /// Ocorrência FÍSICA da linha/grupo repetido (1-based), nunca um índice arbitrário. Fase 0:
        /// para o pathway sysmiddle, distingue ocorrências quando a MESMA linha alimenta múltiplos
        /// destinos estruturalmente distintos dentro do mesmo mapper (regras em sequência do mesmo
        /// grupo) — não resolve a ocorrência FÍSICA real dentro do TXT recebido nesta chamada (isso
        /// exigiria plugar o parser posicional antes do runner low-code neste endpoint, fora do
        /// escopo desta fase; ver limitação documentada em <c>SysmiddleSectionMappingResolver</c>).
        /// </summary>
        public int LineOccurrence { get; set; } = 1;
    }

    public class SectionMappingTarget
    {
        /// <summary>XPath ABSOLUTO com prefixo de namespace estável (ver <see cref="TransformationCandidate.XmlNamespaces"/>).</summary>
        public string XPath { get; set; } = "";

        /// <summary>"element" | "attribute".</summary>
        public string NodeKind { get; set; } = "element";

        public int XmlOccurrence { get; set; } = 1;
    }
}
