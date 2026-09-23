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
        /// <summary>
        /// Fase do processamento, consultável via <c>GET /api/parse/transformations/{ticket}</c>.
        ///
        /// <para><b>Contrato aditivo (2026-08-27, ver
        /// <c>docs/architecture/contrato-linha-vazia-progresso-e-degradacao-posicional-2026-08-27.md</c>
        /// §2):</b> as fases <c>"uploaded"</c> e <c>"layout_selected"</c> são <b>client-side only</b> —
        /// o ticket só existe a partir de <see cref="LowCodeTransformationStore.BuildTicketFromContent"/>,
        /// que já depende do documento ter sido parseado com sucesso (<c>ParseController.Upload</c>
        /// calcula o ticket a partir de <c>result.RawText</c> depois do parse). Ou seja, antes disso
        /// nem existe entrada de índice para consultar — o front já SABE que fez upload e que
        /// selecionou o layout, não precisa perguntar à API. Pelo mesmo motivo, <c>"parsing"</c> não
        /// é emitida pelo backend: quando a entrada passa a existir, o parse do documento já terminou.
        /// As duas fases que o backend efetivamente emite continuam sendo as de sempre —
        /// <see cref="ProcessingStatus"/> (agora também referenciável como <see cref="TransformingStatus"/>,
        /// mesmo valor de fio, para não quebrar consumidor existente) e <see cref="CompletedStatus"/> —
        /// mais <see cref="FailedStatus"/>, novo: fecha o índice como falha estrutural quando NENHUM
        /// candidato teve sucesso (antes isso virava "completed" com todos os candidatos
        /// <c>success=false</c> — o front tinha que inferir "deu tudo errado" varrendo o array).
        /// </para>
        /// </summary>
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

        // Fases client-side only (documentadas aqui só para o front ter a referência do vocabulário
        // completo — o backend NUNCA escreve estas duas no índice, ver comentário de <see cref="Status"/>).
        public const string UploadedStatus = "uploaded";
        public const string LayoutSelectedStatus = "layout_selected";

        // Client-side only pelo mesmo motivo: quando a entrada de índice passa a existir, o parse do
        // documento já terminou (o ticket é derivado do RawText pós-parse).
        public const string ParsingStatus = "parsing";

        public const string ProcessingStatus = "processing";

        /// <summary>Alias de <see cref="ProcessingStatus"/> — mesmo valor de fio ("processing"), nome
        /// mais descritivo para o novo vocabulário de fases (spec §2). Não introduz um terceiro
        /// valor possível no campo <see cref="Status"/>, só um nome mais claro para usar no código.</summary>
        public const string TransformingStatus = ProcessingStatus;

        public const string CompletedStatus = "completed";

        /// <summary>Execução terminou e NENHUM candidato teve sucesso (falha estrutural do
        /// conjunto). Distinto de "completed" com candidatos individuais em erro — este estado é
        /// só para quando o conjunto inteiro fracassou.</summary>
        public const string FailedStatus = "failed";
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
