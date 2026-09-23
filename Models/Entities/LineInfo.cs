namespace LayoutParserApi.Models.Entities
{
    public class LineInfo
    {
        public string LineName { get; set; } = string.Empty;
        public int LineNumber { get; set; }
        public int Occurrence { get; set; } = 1;
        public int StartPosition { get; set; }
        public int Length { get; set; }
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// Aditivo: true quando a linha foi identificada no layout (matchingLineConfig != null)
        /// mas o conteúdo bruto da linha é vazio/whitespace. Ortogonal ao Status por campo —
        /// não substitui nem altera nenhuma sinalização existente.
        /// </summary>
        public bool IsDeclaredEmpty { get; set; }

        /// <summary>
        /// Aditivo: true quando ≥2 campos consecutivos desta ocorrência de linha resolveram para
        /// a mesma posição inicial (fieldStart colapsado) — sintoma observável de degradação
        /// posicional (ex.: LINHA006), não a causa raiz nem o mapeador de origem.
        /// </summary>
        public bool PositionalAlignmentFailed { get; set; }
    }
}
