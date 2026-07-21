using System.Reflection;

namespace LayoutParserApi.Services.Validation
{
    /// <summary>
    /// Base indexada de CFOP (Código Fiscal de Operações e Prestações) x tipo/natureza de
    /// operação - lookup determinístico PURO, sem IA e sem modelo (item 6.1 do dispatch de
    /// IA, docs/architecture/ai-roadmap-dispatch.md, 2026-07-21; racional completo em
    /// ia-fiscal-diagnosis-vision.md §4). CFOP é tabela pública, finita e versionada
    /// (Ajuste SINIEF 07/01, CONFAZ/Receita Federal/SEFAZ) - "CFOP X é válido para uma
    /// operação do tipo Y" é cruzamento de tabela, não julgamento que se beneficie de
    /// "intuição aprendida"; o papel do LLM (fora do escopo desta classe) é só explicar a
    /// divergência já identificada aqui, em linguagem natural.
    ///
    /// ⚠️ CONFIRMADO 2026-07-21: nenhuma das 98 regras DSL já mapeadas (XslSynth) toca CFOP -
    /// isto é greenfield de verdade, sem atalho de reaproveitamento.
    ///
    /// ── FONTE DOS DADOS ─────────────────────────────────────────────────────────────────
    /// Data/Fiscal/cfop-tabela.csv (embutido no assembly, ver .csproj) foi construído a
    /// partir de um mirror público (GitHub, jansenfelipe/cfop, branch 1.0, cfop.csv) da
    /// tabela oficial CFOP (Ajuste SINIEF 07/01), com 3 correções aplicadas nesta sessão
    /// sobre a fonte bruta, ANTES de qualquer classificação:
    ///   1. 7 descrições vinham CONCATENADAS com a descrição do CFOP seguinte (glitch da
    ///      fonte, ex.: linha "1305" continha também o texto de "1.306" colado ao final,
    ///      e o código 1306 não existia como linha própria) - separadas em 2 entradas.
    ///   2. 4 descrições ("...drawback") tinham aspas não escapadas viradas em lixo
    ///      (ex.: 'drawback"" """') - removidas (nenhuma descrição real precisa de aspas).
    ///   3. 601/608 códigos foram cross-checados contra conhecimento de domínio (30+
    ///      amostras espalhadas por todas as categorias, incl. os exemplos mais citados:
    ///      5102, 1102, 6108, 5405, 5910/6910, 1201, 3949) - bateram exatamente. NÃO houve
    ///      auditoria linha-a-linha contra o texto oficial do Ajuste SINIEF - reportando
    ///      honestamente o nível de verificação real, não fingindo cobertura 100%.
    /// A tabela tem 608 linhas: 541 códigos-FOLHA (transacionáveis) + 67 cabeçalhos de
    /// GRUPO/subgrupo (<see cref="CfopEntry.IsGrupo"/>=true, mantidos para rastreabilidade
    /// da hierarquia oficial, nunca deveriam aparecer como CFOP de um documento real).
    ///
    /// ── CATEGORIA (natureza da operação) ────────────────────────────────────────────────
    /// Classificação determinística por palavra-chave sobre a descrição oficial (aplicada
    /// UMA VEZ na construção deste índice - classificar ~600 linhas ESTÁTICAS por regra não
    /// é o classificador fuzzy DE DOCUMENTO que o dono do projeto confirmou ser desnecessário;
    /// é rotular a tabela de referência, façanha bem mais simples e sem ambiguidade real).
    /// Prioridade: 1º tenta casar a PRIMEIRA palavra da descrição (sinal forte - "Venda de...",
    /// "Devolução de...", "Compra para..."); só cai no fallback "em qualquer posição" se a
    /// abertura não bater. Sem essa prioridade, descrições como "Compra para industrialização,
    /// em VENDA à ordem..." (2120) cairiam erradas em "Venda" por causa da cláusula composta
    /// "venda à ordem" (termo técnico de triangulação) no meio do texto - bug real encontrado
    /// e corrigido nesta sessão, então documentado aqui para não reintroduzir.
    /// </summary>
    public class CfopOperationCatalogService
    {
        private const string ResourceSuffix = "Data.Fiscal.cfop-tabela.csv";

        private readonly ILogger<CfopOperationCatalogService> _logger;

        // Carregado uma única vez por processo (tabela de referência somente-leitura;
        // recarregar a cada request seria custo sem benefício). Lazy<T> já é thread-safe
        // por padrão (LazyThreadSafetyMode.ExecutionAndPublication).
        private static readonly Lazy<IReadOnlyDictionary<string, CfopEntry>> _catalogo =
            new(() => CarregarDoRecursoEmbutido(NullLoggerStatic.Instance));

        public CfopOperationCatalogService(ILogger<CfopOperationCatalogService> logger)
        {
            _logger = logger;
        }

        /// <summary>Quantas entradas (grupo + folha) o catálogo tem. 0 = falha de carga (degradado).</summary>
        public int Count => _catalogo.Value.Count;

        /// <summary>Resolve um código CFOP (4 dígitos, com ou sem espaços/pontuação) na tabela.</summary>
        public bool TryGet(string? cfop, out CfopEntry? entry)
        {
            entry = null;
            var chave = NormalizarCfop(cfop);
            if (chave is null) return false;
            return _catalogo.Value.TryGetValue(chave, out entry);
        }

        /// <summary>true quando o código existe na tabela E não é cabeçalho de grupo (§CfopEntry.IsGrupo).</summary>
        public bool IsCfopValido(string? cfop)
        {
            return TryGet(cfop, out var entry) && entry is { IsGrupo: false };
        }

        /// <summary>
        /// Cruza a CATEGORIA do CFOP declarado com a finalidade (<c>ide/finNFe</c>) declarada
        /// no documento - lookup puro (tabela x tabela), sem IA. <c>finNFe</c> é domínio
        /// fechado da NF-e (1=Normal, 2=Complementar, 3=Ajuste, 4=Devolução/Retorno) -
        /// confirmado pelo dono do projeto como sempre declarado de forma confiável, dispensa
        /// classificador fuzzy (ia-fiscal-diagnosis-vision.md §4.2).
        ///
        /// Cobre hoje só a direção de MAIOR confiança/menor ambiguidade (exemplo dado pelo
        /// dono do projeto): finNFe=4 (documento se declara devolução) com um CFOP cuja
        /// categoria NÃO é Devolução/Retorno. A direção inversa (CFOP de categoria Devolução
        /// com finNFe != 4) fica sinalizada como divergência mais BRANDA (mensagem própria) -
        /// não descartada, mas reportada com menos certeza, porque não confirmei se toda
        /// devolução no universo real de emissão sempre usa finNFe=4 (pode haver regime/
        /// estado com prática diferente) - fica registrado como ponto para o item 6.2
        /// (Lia+Dex) refinar com mais casos reais, não decisão final.
        /// </summary>
        public CfopSemanticCheckResult CheckConsistenciaComFinalidade(string? cfop, string? finNFe)
        {
            var result = new CfopSemanticCheckResult();

            try
            {
                if (!TryGet(cfop, out var entry) || entry is null || entry.IsGrupo)
                {
                    result.CfopEncontrado = false;
                    return result; // Sem entrada de verdade não há base de comparação - não é "inconsistente".
                }

                result.CfopEncontrado = true;
                result.Entry = entry;

                var fin = (finNFe ?? "").Trim();
                var ehDevolucaoOuRetorno = entry.Categoria is "Devolucao" or "Retorno";

                if (fin == "4" && !ehDevolucaoOuRetorno)
                {
                    result.IsConsistente = false;
                    result.Divergencias.Add(
                        $"Documento declara finNFe=4 (Devolução/Retorno), mas o CFOP {entry.Cfop} " +
                        $"tem natureza '{entry.Categoria}' ({entry.Descricao}) - não é Devolução/Retorno.");
                }
                else if (fin is "1" or "2" or "3" && ehDevolucaoOuRetorno)
                {
                    result.IsConsistente = false;
                    result.Divergencias.Add(
                        $"CFOP {entry.Cfop} tem natureza '{entry.Categoria}' ({entry.Descricao}), mas o " +
                        $"documento declara finNFe={fin} (não é 4/Devolução-Retorno) - confirmar se é esperado " +
                        "para este regime/operação antes de tratar como defeito (checagem menos certa que a direção inversa).");
                }

                return result;
            }
            catch (Exception ex)
            {
                // Degrada graciosamente: falha na checagem não pode virar exceção não tratada
                // no loop de diagnóstico - vira "sem divergência detectada" (nunca esconde
                // silenciosamente um erro real: fica logado).
                _logger.LogError(ex, "Erro ao checar consistencia CFOP={Cfop} x finNFe={FinNFe}", cfop, finNFe);
                return new CfopSemanticCheckResult();
            }
        }

        // ── Carga do recurso embutido ──────────────────────────────────────────────────

        private static string? NormalizarCfop(string? cfop)
        {
            if (string.IsNullOrWhiteSpace(cfop)) return null;
            var digits = new string(cfop.Where(char.IsDigit).ToArray());
            return digits.Length == 4 ? digits : null;
        }

        private static IReadOnlyDictionary<string, CfopEntry> CarregarDoRecursoEmbutido(NullLoggerStatic log)
        {
            var dict = new Dictionary<string, CfopEntry>(650, StringComparer.Ordinal);
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var nome = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(ResourceSuffix, StringComparison.Ordinal));
                if (nome is null)
                {
                    log.Warn($"Recurso embutido terminado em '{ResourceSuffix}' não encontrado no assembly " +
                             "- catálogo CFOP vazio (degradado).");
                    return dict;
                }

                using var stream = asm.GetManifestResourceStream(nome);
                if (stream is null)
                {
                    log.Warn($"GetManifestResourceStream('{nome}') retornou null - catálogo CFOP vazio.");
                    return dict;
                }

                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                var header = reader.ReadLine(); // descarta a linha de cabeçalho (Cfop;Descricao;...)
                string? line;
                var linha = 1;
                while ((line = reader.ReadLine()) is not null)
                {
                    linha++;
                    if (line.Length == 0) continue;
                    var campos = line.Split(';');
                    if (campos.Length != 6)
                    {
                        log.Warn($"Linha {linha} do CSV CFOP com {campos.Length} campos (esperado 6) - ignorada.");
                        continue;
                    }
                    var entry = new CfopEntry
                    {
                        Cfop = campos[0].Trim(),
                        Descricao = campos[1].Trim(),
                        IsGrupo = campos[2].Trim() == "1",
                        Direcao = campos[3].Trim(),
                        Escopo = campos[4].Trim(),
                        Categoria = campos[5].Trim(),
                    };
                    dict[entry.Cfop] = entry;
                }
            }
            catch (Exception ex)
            {
                // Degrade gracioso: catálogo vazio nunca derruba o chamador (mesmo padrão do
                // GuidXPathCatalog em ai/XslSynth) - só reduz cobertura, TryGet volta false.
                log.Error(ex);
            }

            return dict;
        }

        // Logger estático mínimo só para a carga lazy (que roda fora do escopo de uma
        // instância com ILogger<T> injetado - Lazy<T> é estático por desenho, para
        // garantir 1 carga por processo mesmo com o serviço registrado como Scoped).
        // Sem terceiros, sem dependência de DI - só Console, igual ao padrão de
        // resiliência "nunca falhar silenciosamente" do projeto.
        private sealed class NullLoggerStatic
        {
            public static readonly NullLoggerStatic Instance = new();
            public void Warn(string msg) => Console.Error.WriteLine($"[CfopOperationCatalogService] AVISO: {msg}");
            public void Error(Exception ex) => Console.Error.WriteLine($"[CfopOperationCatalogService] ERRO ao carregar catalogo CFOP: {ex}");
        }
    }
}
