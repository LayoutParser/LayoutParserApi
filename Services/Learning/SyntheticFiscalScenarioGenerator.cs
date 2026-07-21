using Bogus;
using Bogus.Extensions.Brazil;
using LayoutParserApi.Services.Learning.Models;
using LayoutParserApi.Services.Validation;

namespace LayoutParserApi.Services.Learning
{
    /// <summary>
    /// Nível 1 (item 4.1/4.2 do dispatch de IA, docs/architecture/ai-roadmap-dispatch.md,
    /// 2026-07-21): gera CENÁRIOS FISCAIS SINTÉTICOS rotulados via Bogus (MIT, locale
    /// <c>pt_BR</c>, CPF/CNPJ com dígito verificador REAL - <c>Bogus.Extensions.Brazil</c>).
    ///
    /// ⚠️ REFRAME (confirmado pelo dono do projeto, 2026-07-21): o propósito NÃO é fixture
    /// de teste descartável - é alimentar um índice RAG para a IA fiscal especializada
    /// (ia-fiscal-diagnosis-vision.md §4): cada cenário rotulado vira um exemplo que o
    /// modelo pequeno recupera ao explicar uma divergência REAL parecida. NÃO é treino/
    /// fine-tuning (servidor de produção sem GPU, confirmado - ver memória de
    /// <c>@lp-architect</c> <c>production-server-hardware</c>).
    ///
    /// Reusa <see cref="CfopOperationCatalogService"/> (item 6.1, mesma sessão) para
    /// escolher CFOPs REAIS por categoria em vez de inventar códigos - e para
    /// AUTO-VALIDAR o rótulo de divergência (<see cref="CfopOperationCatalogService.CheckConsistenciaComFinalidade"/>),
    /// para o rótulo nunca dessincronizar da regra de negócio se a classificação evoluir.
    ///
    /// Escopo (Nível 1, deliberado): valores de CAMPO (partes, produto, CFOP, finNFe) - NÃO
    /// codifica o cenário de volta em layout posicional TXT (isso acopla com o motor
    /// Excel/TCL de ai/XslSynth, fora do escopo desta rodada). Nível 2 (modelagem generativa
    /// pesada) foi rebaixado de prioridade pelo próprio dispatch - não perseguido aqui.
    /// </summary>
    public class SyntheticFiscalScenarioGenerator
    {
        private readonly ILogger<SyntheticFiscalScenarioGenerator> _logger;
        private readonly CfopOperationCatalogService _cfopCatalog;

        public SyntheticFiscalScenarioGenerator(
            ILogger<SyntheticFiscalScenarioGenerator> logger,
            CfopOperationCatalogService cfopCatalog)
        {
            _logger = logger;
            _cfopCatalog = cfopCatalog;
        }

        /// <summary>Arquétipos de cenário — cobre o exemplo dado pelo dono do projeto e sua contraparte consistente.</summary>
        public enum Arquetipo
        {
            /// <summary>Venda normal - CFOP categoria Venda + finNFe=1 (consistente).</summary>
            VendaNormal,

            /// <summary>Devolução normal - CFOP categoria Devolucao/Retorno + finNFe=4 (consistente).</summary>
            DevolucaoNormal,

            /// <summary>
            /// O exemplo-âncora da visão fiscal: "CFOP de venda está errado porque a nota é
            /// devolução" - CFOP categoria Venda + finNFe=4 (divergência deliberada).
            /// </summary>
            DivergenciaVendaRotuladaComoDevolucao,
        }

        /// <summary>
        /// Gera UM cenário do arquétipo pedido. <paramref name="seed"/> nulo = aleatório
        /// (não reprodutível); informe uma semente para depurar/repetir um caso específico.
        /// Degrade gracioso: se o catálogo CFOP não tiver nenhum código da categoria
        /// necessária (catálogo vazio/corrompido), devolve null em vez de inventar um CFOP -
        /// nunca produz um cenário rotulado com dado fiscal fictício.
        /// </summary>
        public SyntheticFiscalScenario? Generate(Arquetipo arquetipo, int? seed = null)
        {
            try
            {
                var usedSeed = seed ?? Random.Shared.Next();
                var faker = new Faker("pt_BR") { Random = new Randomizer(usedSeed) };
                var emitente = new Bogus.DataSets.Company("pt_BR") { Random = faker.Random };
                var destPessoa = new Person("pt_BR", usedSeed + 1);
                var destEmpresa = new Bogus.DataSets.Company("pt_BR") { Random = faker.Random };

                var (categoria, finNFe, divergente) = arquetipo switch
                {
                    Arquetipo.VendaNormal => ("Venda", "1", false),
                    Arquetipo.DevolucaoNormal => ("Devolucao", "4", false),
                    Arquetipo.DivergenciaVendaRotuladaComoDevolucao => ("Venda", "4", true),
                    _ => throw new ArgumentOutOfRangeException(nameof(arquetipo)),
                };

                var cfopEscolhido = EscolherCfopDaCategoria(categoria, faker);
                if (cfopEscolhido is null)
                {
                    _logger.LogWarning(
                        "Nenhum CFOP de categoria {Categoria} disponível no catalogo (vazio/degradado) - " +
                        "cenario {Arquetipo} nao gerado.", categoria, arquetipo);
                    return null;
                }

                var destinatarioEhPessoaFisica = faker.Random.Bool();
                var scenario = new SyntheticFiscalScenario
                {
                    Seed = usedSeed,
                    EmitenteNome = emitente.CompanyName(),
                    EmitenteCnpj = emitente.Cnpj(),
                    DestinatarioNome = destinatarioEhPessoaFisica ? destPessoa.FullName : destEmpresa.CompanyName(),
                    DestinatarioDocumento = destinatarioEhPessoaFisica ? destPessoa.Cpf() : destEmpresa.Cnpj(),
                    ProdutoDescricao = faker.Commerce.ProductName(),
                    ValorTotal = decimal.Parse(faker.Commerce.Price(10, 50000)),
                    Cfop = cfopEscolhido.Cfop,
                    CfopDescricao = cfopEscolhido.Descricao,
                    CfopCategoria = cfopEscolhido.Categoria,
                    FinNFe = finNFe,
                    EhDivergente = divergente,
                };

                // Auto-verificação: o rótulo declarado (divergente/consistente) precisa bater
                // com o que o cruzamento determinístico do item 6.1 realmente confirma -
                // nunca confiar cegamente no switch acima se a regra de negócio evoluir.
                var check = _cfopCatalog.CheckConsistenciaComFinalidade(scenario.Cfop, scenario.FinNFe);
                if (check.IsConsistente == divergente)
                {
                    _logger.LogError(
                        "Rotulo do cenario sintetico ({Arquetipo}) NAO bate com CheckConsistenciaComFinalidade " +
                        "(CFOP={Cfop} finNFe={FinNFe} declarado_divergente={Divergente} check_consistente={Consistente}) " +
                        "- descartando cenario para nao indexar rotulo errado no RAG.",
                        arquetipo, scenario.Cfop, scenario.FinNFe, divergente, check.IsConsistente);
                    return null;
                }

                scenario.Rotulo = MontarRotulo(scenario, check);
                return scenario;
            }
            catch (Exception ex)
            {
                // Degrade gracioso: geração sintética nunca pode derrubar o chamador -
                // este é exatamente o tipo de dependência "opcional" que o projeto trata
                // como tal (não é external service, mas o princípio é o mesmo: falhar aqui
                // não pode quebrar nada crítico).
                _logger.LogError(ex, "Erro ao gerar cenario fiscal sintetico ({Arquetipo})", arquetipo);
                return null;
            }
        }

        private CfopEntry? EscolherCfopDaCategoria(string categoria, Faker faker)
        {
            // CfopOperationCatalogService não expõe enumeração pública hoje (só lookup por
            // código) - percorremos os códigos-folha conhecidos e filtramos por categoria.
            // Lista pequena (< 610) e a chamada não é hot-path (geração sintética é
            // offline/batch) - custo desprezível.
            var candidatos = new List<CfopEntry>();
            foreach (var cfop in CfopsFolhaConhecidos())
            {
                if (_cfopCatalog.TryGet(cfop, out var entry) && entry is { IsGrupo: false } &&
                    entry.Categoria == categoria)
                    candidatos.Add(entry);
            }
            return candidatos.Count == 0 ? null : faker.PickRandom(candidatos);
        }

        // Amostra representativa de códigos-folha reais (não a tabela inteira) cobrindo as
        // categorias usadas pelos arquétipos acima. Evita reimplementar enumeração completa
        // no serviço de catálogo só para este consumidor - se um consumidor futuro precisar
        // de mais cobertura/todas as categorias, é sinal para promover isto a um método
        // público `Entries` no CfopOperationCatalogService (não feito agora - YAGNI).
        private static IEnumerable<string> CfopsFolhaConhecidos() =>
        [
            "5101", "5102", "5103", "5104", "5105", "5106", "6101", "6102", "6108",
            "5401", "5403", "5405", "5651", "5652",
            "1201", "1202", "1208", "2201", "2202", "5201", "5202", "5410", "5411",
            "1414", "1415", "1904", "2904", "1902", "5657",
        ];

        private static string MontarRotulo(SyntheticFiscalScenario s, CfopSemanticCheckResult check)
        {
            var partes = $"Emitente {s.EmitenteNome} (CNPJ {s.EmitenteCnpj}) para {s.DestinatarioNome} " +
                $"(doc. {s.DestinatarioDocumento}), produto \"{s.ProdutoDescricao}\", valor R$ {s.ValorTotal:F2}, " +
                $"CFOP {s.Cfop} ({s.CfopDescricao}), finNFe={s.FinNFe}.";

            if (!s.EhDivergente)
                return $"[CENARIO CONSISTENTE] {partes} CFOP e finNFe compativeis (categoria {s.CfopCategoria}).";

            var motivo = check.Divergencias.Count > 0
                ? check.Divergencias[0]
                : $"CFOP de categoria {s.CfopCategoria} declarado com finNFe={s.FinNFe}.";
            return $"[CENARIO DIVERGENTE - SINTETICO, ROTULADO] {partes} DIVERGENCIA: {motivo}";
        }
    }
}
