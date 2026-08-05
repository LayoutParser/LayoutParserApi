namespace LayoutParserApi.Models.Transformation
{
    /// <summary>
    /// Entrada do ÍNDICE DE LEITURA do store de transformações low-code
    /// (<c>{ML:LowCodeTransformationsPath}/index/{sha256}.{layoutGuid}.json</c>).
    ///
    /// <para>Os artefatos com carimbo de tempo (<c>{sha}_{HHmmss}.*</c>) continuam exatamente como
    /// estão — o store é um corpus append-only de treino e o sufixo de hora é o que impede que
    /// reprocessar o mesmo documento destrua a amostra anterior. Este índice fica AO LADO deles e é
    /// sobrescrito a cada execução, apontando sempre para a mais recente (spec §2.1/§2.3).</para>
    ///
    /// <para>Campos além dos listados na spec §2.3 (<c>Status</c>, <c>Partial</c>, <c>CreatedAtUtc</c>):
    /// existem porque o índice também é a fonte do cache-first e do endpoint de manifesto — sem eles
    /// não dá para distinguir "ainda rodando" de "terminou", nem limitar a idade do hit.</para>
    /// </summary>
    public class LowCodeTransformationIndexEntry
    {
        /// <summary>"processing" (execução em andamento) | "completed" (execução terminou).</summary>
        public string Status { get; set; } = ProcessingStatus;

        /// <summary>
        /// True quando a execução terminou por CANCELAMENTO (teto síncrono do ParseController) e
        /// portanto o conjunto de candidatos pode estar truncado. Entrada parcial serve para LEITURA
        /// (o front vê o que deu tempo de sair), mas NUNCA para cache-first — senão um cancelamento
        /// congelaria um resultado incompleto para todo upload idêntico seguinte.
        /// </summary>
        public bool Partial { get; set; }

        public DateTime CreatedAtUtc { get; set; }

        /// <summary>Prefixo dos artefatos desta execução (<c>{sha}_{HHmmss}</c>).</summary>
        public string? BaseName { get; set; }

        /// <summary>Pasta diária dos artefatos (<c>yyyyMMdd</c>), relativa ao store.</summary>
        public string? DateFolder { get; set; }

        public string? Sha256 { get; set; }

        public string? LayoutGuid { get; set; }

        public List<LowCodeTransformationIndexCandidate> Candidates { get; set; } = new();

        public const string ProcessingStatus = "processing";
        public const string CompletedStatus = "completed";
    }

    /// <summary>Descritor de um candidato dentro do índice — sem o XML (ver split da spec §2.4).</summary>
    public class LowCodeTransformationIndexCandidate
    {
        public string MapperGuid { get; set; } = "";

        public string? MapperName { get; set; }

        public string? TargetLayoutGuid { get; set; }

        public string? PackageGuid { get; set; }

        public bool Success { get; set; }

        /// <summary>Nome do arquivo de saída dentro de <c>{store}/{DateFolder}/</c> — nunca caminho absoluto.</summary>
        public string? OutputFile { get; set; }

        public int OutputLength { get; set; }

        /// <summary>Mensagem de erro JÁ SANEADA (sem caminho de disco do servidor — spec §3.1).</summary>
        public string? ErrorMessage { get; set; }
    }
}
