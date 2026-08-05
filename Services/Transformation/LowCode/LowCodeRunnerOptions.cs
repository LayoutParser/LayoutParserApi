namespace LayoutParserApi.Services.Transformation.LowCode
{
    public class LowCodeRunnerOptions
    {
        public string RunnerPath { get; set; } = "";
        public string SysmiddleDir { get; set; } = "";
        public string GlobalFolder { get; set; } = "";
        public string Package { get; set; } = "";
        public string? DefaultMapperName { get; set; }

        // Seleção de mappers no banco (tbMapper)
        public int ProjectId { get; set; } = 2;
        public List<string> AllowedPackageGuids { get; set; } = new();

        // ✅ Seleção multi-candidato (LowCode-auto): quando há N>1 mapeadores genuinamente plausíveis
        // (MapperGuid distintos) para o mesmo layoutGuid, capamos em top-N pelos mesmos critérios de
        // prioridade já existentes (input match > target match > mais recente) antes de rodar em paralelo.
        public int MultiCandidateTopN { get; set; } = 4;

        // ✅ Timeout por invocação do runner (processo externo x86). Cobre o ciclo de vida inteiro
        // do processo (start + leitura de stdout/stderr + exit) — não só a espera de exit — porque
        // uma leitura de stream travada também precisa disparar o kill. Default 15s: bootstrap do
        // runner observado em ~0.5-1s, então 15s já é folga generosa antes de considerar o host
        // FiatMQ travado (sensível a disputa de licença/estado, ver docs de runtime do Sysmiddle).
        public int RunnerTimeoutSeconds { get; set; } = 15;

        // ✅ Limite de concorrência do runner NO PROCESSO INTEIRO da API (não por request/documento):
        // se dois uploads multi-candidato chegarem juntos, o total de processos do runner rodando ao
        // mesmo tempo ainda respeita este teto. Default 2: bootstrap caro (~0.5-1s) e host FiatMQ
        // sensível a execução concorrente — 2-3 é o intervalo seguro sugerido, escolhemos o piso.
        public int MaxConcurrentRunners { get; set; } = 2;

        // ✅ Teto para a entrega SÍNCRONA das transformações candidatas no response do upload/parse
        // (ParseController). Deliberadamente bem menor que RunnerTimeoutSeconds: como os candidatos
        // rodam em paralelo (Task.WhenAll), o caso comum (poucas variantes, runner saudável) completa
        // bem antes disso; se estourar, cai para processamento em background sem bloquear o parse.
        public int SyncDeliveryTimeoutSeconds { get; set; } = 6;

        // ✅ Teto para entregar o XML da transformação INLINE no payload do parse. Acima disso o
        // campo outputXml é omitido e o front busca pelo endpoint de corpo
        // (GET /api/parse/transformations/{ticket}/candidates/{mapperGuid}).
        // Default 262144 (256 KB): medido em material real, input de 35 KB gera saída de ~4,2 KB —
        // o caso comum cabe inline com folga enorme e o teto só protege do outlier.
        //
        // ⚠️ O deploy PRESERVA o appsettings.json do destino (ci-dev.yml/deploy.yml), então config
        // nova adicionada ao repo NÃO chega ao servidor: este default precisa ser seguro sozinho.
        // Override em produção só por variável de ambiente LowCode__InlineXmlMaxChars.
        public int InlineXmlMaxChars { get; set; } = 262144;

        // ✅ Janela de frescor do cache-first de transformações (mesmo documento + mesmo layout
        // não roda o runner de novo). Vale para o Redis (TTL da chave) e para a decisão de PULAR o
        // runner com base no índice em disco — os artefatos em disco não expiram (corpus de treino).
        // Default 2h: cobre com folga o fluxo real (parse → clique em "Gerar Transformação XML",
        // segundos depois) sem congelar para sempre um resultado de um mapper que pode ter mudado.
        // Mesmo aviso do campo acima: override só por LowCode__TransformationCacheTtlHours.
        public int TransformationCacheTtlHours { get; set; } = 2;
    }
}


