namespace LayoutParserApi.Models.Entities
{
    public class ParsedField
    {
        public string LineName { get; set; }
        public string FieldName { get; set; }
        public int Sequence { get; set; }
        public int Start { get; set; }
        public int Length { get; set; }
        public string Value { get; set; }
        public string Status { get; set; }
        public bool IsRequired { get; set; }
        public string DataType { get; set; }
        public int Occurrence { get; set; } = 1;

        /// <summary>
        /// Total de ocorrências físicas reais existentes para o grupo (LineName+FieldName) deste
        /// campo. Constante para todos os ParsedFields do mesmo grupo, incluindo o agregado
        /// (Occurrence=0) gerado por AggregatePositionalGroupRepetitions. Default 1 (campo sem
        /// repetição posicional).
        /// </summary>
        public int OccurrenceCount { get; set; } = 1;

        /// <summary>
        /// true apenas na entrada que representa o valor lógico final/agregado de uma LineElement
        /// marcada IsPositionalGroupRepetition (gerada por AggregatePositionalGroupRepetitions,
        /// Occurrence=0). false nos fragmentos físicos brutos (Occurrence >= 1) e em campos sem
        /// repetição posicional. Permite ao consumidor (front-end) escolher a entrada correta sem
        /// depender da convenção implícita "Occurrence==0".
        /// </summary>
        public bool IsAggregatedOccurrence { get; set; }

        public bool IsMissing { get; set; }
        public string LineSequence { get; set; }
        public string FullPath => $"{LineName}.{FieldName}";
        public bool IsAutoDiscovered { get; set; }

        public string ValidationMessage { get; set; }
    }
}
