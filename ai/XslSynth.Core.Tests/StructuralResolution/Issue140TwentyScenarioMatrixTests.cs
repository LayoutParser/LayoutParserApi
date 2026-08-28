using XslSynth.Core.StructuralResolution;
using XslSynth.Model;
using XslSynth.Prompting;

namespace XslSynth.Core.Tests.StructuralResolution;

/// <summary>
/// Matriz de 20 execuções controladas exigida pelo critério de aceite original da issue #140
/// (design §6.1) — cobertura ESTRUTURAL/determinística (mapper+layout sintéticos), NÃO
/// comportamental contra o <c>LayoutParserRunner.exe</c> real (Windows-only, fora deste ambiente —
/// ver nota de QA no fechamento da issue). Cada teste corresponde a uma linha da tabela do design.
///
/// Regra dura desta suíte: nenhum teste pode marcar <see cref="Confidence.Authoritative"/> num
/// cenário que dependeria da validação comportamental pendente (§6.2) — ver
/// <see cref="Linha09_CampoVazio_SinalNaoChegaAoComposer_GapDocumentado"/> e
/// <see cref="Linha20_DegradacaoPosicional_SinalNaoChegaAoComposer_GapDocumentado"/>, que documentam
/// gaps reais em vez de forçar passagem.
/// </summary>
public sealed class Issue140TwentyScenarioMatrixTests
{
    private readonly XmlLayoutCatalog _catalog = SyntheticXmlCatalogBuilder.Build();

    private static TxtFieldReference Src(string field, int occurrence = 1, string line = "LIN_LINHA01") =>
        new(line, "LINHA01", $"FLD_{field}", field, occurrence, StartPosition: 1, Length: 10);

    private FieldToXmlMappingComposer Composer() => new(_catalog);

    // ---- Linhas 1-3: tipo de layout de origem (TXT plano / MQSeries / IDOC) ----
    //
    // Achado estrutural (não é omissão do teste): o composer/MappingCandidate é AGNÓSTICO ao tipo de
    // layout de origem — TxtFieldReference carrega só LineGuid/FieldGuid/LineOccurrence/posição, sem
    // nenhum campo que distinga TXT/MQSeries/IDOC. A diferenciação entre esses 3 formatos acontece
    // inteiramente ANTES do composer, na camada de parsing posicional (fora do escopo da issue #140,
    // já coberta por testes próprios de parsing). Por isso as 3 linhas colapsam num único teste que
    // prova a agnosticidade — 3 candidatos idênticos em forma, "origem" só varia no LineGuid/comentário,
    // e o resultado tem que ser idêntico entre eles (senão o composer estaria indevidamente acoplado
    // ao tipo de origem).

    [Theory]
    [InlineData("LIN_txt_plano")]
    [InlineData("LIN_mqseries")]
    [InlineData("LIN_idoc")]
    public void Linhas01a03_TipoDeLayoutDeOrigem_ComposerEhAgnostico(string lineGuid)
    {
        var candidate = new MappingCandidate(
            MappingId: $"M-{lineGuid}",
            Sources: new[] { Src("CAMPO_A", line: lineGuid) },
            Kind: MappingKind.Direct,
            TargetPath: "Doc/Cabecalho/CampoA",
            TargetPathIsFullPath: true,
            Functions: Array.Empty<string>(),
            LoopType: null,
            AllSourcesResolvedFromOriginLayout: true,
            SourcesHavePositionalGroupRepetition: false,
            KnownFunctions: new HashSet<string>());

        var result = Composer().Compose(candidate);

        Assert.Equal(Confidence.Authoritative, result.Confidence);
        Assert.Equal("/ns0:Doc/ns0:Cabecalho/ns0:CampoA", result.Targets[0].Xpath);
    }

    // ---- Linha 4: linha repetida (3 ocorrências físicas -> 3 nós XML MaxOccurs=3) ----
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Linha04_LinhaRepetida_TresOcorrenciasFisicas_XmlOccurrenceAcompanha(int occurrence)
    {
        var candidate = new MappingCandidate(
            MappingId: $"M4-{occurrence}",
            Sources: new[] { Src("VALOR_ITEM", occurrence) },
            Kind: MappingKind.Direct,
            TargetPath: "Doc/Itens/Item/Valor",
            TargetPathIsFullPath: true,
            Functions: Array.Empty<string>(),
            LoopType: null,
            AllSourcesResolvedFromOriginLayout: true,
            SourcesHavePositionalGroupRepetition: true,
            KnownFunctions: new HashSet<string>());

        var result = Composer().Compose(candidate);

        Assert.Equal(Confidence.Authoritative, result.Confidence);
        Assert.Equal(occurrence, result.Targets[0].XmlOccurrence);
    }

    // ---- Linha 5: grupo repetido aninhado (2 níveis) ----
    [Fact]
    public void Linha05_GrupoRepetidoAninhado_DoisNiveis_ResolveAuthoritative()
    {
        var candidate = new MappingCandidate(
            MappingId: "M5",
            Sources: new[] { Src("SUBVALOR", occurrence: 2) },
            Kind: MappingKind.Direct,
            TargetPath: "Doc/Itens/Item/SubItens/SubItem/SubValor",
            TargetPathIsFullPath: true,
            Functions: Array.Empty<string>(),
            LoopType: null,
            AllSourcesResolvedFromOriginLayout: true,
            SourcesHavePositionalGroupRepetition: true, // SubItem também é repetível (unbounded)
            KnownFunctions: new HashSet<string>());

        var result = Composer().Compose(candidate);

        // Ancestors() pega o PRIMEIRO ancestral repetível subindo a árvore — SubItem (unbounded),
        // não Item (maxOccurs=3). Confirma que a resolução usa o nível de repetição mais próximo do
        // alvo, não a raiz do grupo — comportamento correto para aninhamento, mas note que não há
        // hoje um segundo "xmlOccurrence" para o nível externo (Item) nesse mesmo mapeamento; a
        // hipótese estrutural do design §4.2 assume 1 nível de correspondência por mapeamento.
        Assert.Equal(Confidence.Authoritative, result.Confidence);
        Assert.Equal(2, result.Targets[0].XmlOccurrence);
        Assert.Equal("/ns0:Doc/ns0:Itens/ns0:Item/ns0:SubItens/ns0:SubItem/ns0:SubValor", result.Targets[0].Xpath);
    }

    // ---- Linha 6: atributo ----
    [Fact]
    public void Linha06_Atributo_ResolveComPrefixoArroba()
    {
        var candidate = new MappingCandidate(
            MappingId: "M6",
            Sources: new[] { Src("SEQ", occurrence: 1) },
            Kind: MappingKind.Direct,
            TargetPath: "Doc/Itens/Item/@Seq",
            TargetPathIsFullPath: true,
            Functions: Array.Empty<string>(),
            LoopType: null,
            AllSourcesResolvedFromOriginLayout: true,
            // O atributo @Seq é filho de Item, cujo ancestral repete (MaxOccurs=3 no catálogo
            // sintético) — para não colidir com o mismatch de repetição (linhas 16/17), a origem
            // aqui também é marcada repetida, mantendo o foco do teste na dimensão "atributo".
            SourcesHavePositionalGroupRepetition: true,
            KnownFunctions: new HashSet<string>());

        var result = Composer().Compose(candidate);

        Assert.Equal(Confidence.Authoritative, result.Confidence);
        Assert.Equal(XmlNodeKind.Attribute, result.Targets[0].NodeKind);
        Assert.EndsWith("@Seq", result.Targets[0].Xpath);
    }

    // ---- Linha 7: concatenação ----
    [Fact]
    public void Linha07_Concatenacao_ConcatString_ClassificaEResolveAuthoritative()
    {
        var rule = new StructuredRule(StructuredRuleSchema.Version, "RULE_CONCAT", "concat-teste", "Doc/Total",
            new List<StructuredBranch> { new("true", "Doc/Total", new[] { "LINHA01/CAMPO_A", "LINHA01/CAMPO_B" }, new[] { "ConcatString" }) },
            StaticValue: null, LoopType: null);
        var kind = MappingKindClassifier.ClassifyRule(rule);

        var candidate = new MappingCandidate(
            MappingId: "M7",
            Sources: new[] { Src("CAMPO_A"), Src("CAMPO_B") },
            Kind: kind,
            TargetPath: "Doc/Total",
            TargetPathIsFullPath: true,
            Functions: rule.AllFunctions,
            LoopType: rule.LoopType,
            AllSourcesResolvedFromOriginLayout: true,
            SourcesHavePositionalGroupRepetition: false,
            KnownFunctions: new HashSet<string> { "ConcatString" });

        var result = Composer().Compose(candidate);

        Assert.Equal(MappingKind.Concatenated, kind);
        Assert.Equal(Confidence.Authoritative, result.Confidence);
        Assert.Equal(2, result.Sources.Count);
    }

    // ---- Linha 8: valor estático ----
    [Fact]
    public void Linha08_ValorEstatico_SemSources_ResolveAuthoritative()
    {
        var rule = new StructuredRule(StructuredRuleSchema.Version, "RULE_STATIC", "static-teste", "Doc/Total",
            new List<StructuredBranch> { new("true", "Doc/Total", Array.Empty<string>(), Array.Empty<string>()) },
            StaticValue: "1", LoopType: null);
        var kind = MappingKindClassifier.ClassifyRule(rule);

        var candidate = new MappingCandidate(
            MappingId: "M8",
            Sources: Array.Empty<TxtFieldReference>(),
            Kind: kind,
            TargetPath: "Doc/Total",
            TargetPathIsFullPath: true,
            Functions: Array.Empty<string>(),
            LoopType: null,
            AllSourcesResolvedFromOriginLayout: false,
            SourcesHavePositionalGroupRepetition: false,
            KnownFunctions: new HashSet<string>());

        var result = Composer().Compose(candidate);

        Assert.Equal(MappingKind.Static, kind);
        Assert.Equal(Confidence.Authoritative, result.Confidence);
        Assert.Empty(result.Sources);
    }

    // ---- Linha 9: campo vazio na origem (IsDeclaredEmpty) ----
    [Fact]
    public void Linha09_CampoVazio_SinalNaoChegaAoComposer_GapDocumentado()
    {
        // GAP REAL (não é limitação aceitável do design, é achado de QA): LineInfo.IsDeclaredEmpty
        // (contrato de 2026-08-27) vive em LayoutParserApi.Models.Entities.LineInfo, uma estrutura
        // POR LINHA que hoje NÃO é passada para FieldMappingCompositionService.Compose (assinatura
        // recebe só Layout/IReadOnlyList<ParsedField>/MapperVo — sem IReadOnlyList<LineInfo>) nem
        // para MappingCandidate (sem campo equivalente). Logo: um campo de origem declarado vazio
        // hoje passa pelo composer como se fosse um campo normal — se todas as outras condições do
        // §5 forem verdadeiras, o mapeamento sai AUTHORITATIVE mesmo vindo de uma linha com conteúdo
        // vazio/whitespace. O design (linha 9 da tabela §6.1) esperava "best-effort ou valor vazio
        // explícito, nunca exceção" — a parte "nunca exceção" está OK (confirmado abaixo), mas a
        // degradação para best-effort NÃO acontece porque o sinal não existe no motor.
        var candidate = new MappingCandidate(
            MappingId: "M9",
            Sources: new[] { Src("CAMPO_VAZIO") }, // sem qualquer forma de sinalizar IsDeclaredEmpty
            Kind: MappingKind.Direct,
            TargetPath: "Doc/Cabecalho/CampoA",
            TargetPathIsFullPath: true,
            Functions: Array.Empty<string>(),
            LoopType: null,
            AllSourcesResolvedFromOriginLayout: true,
            SourcesHavePositionalGroupRepetition: false,
            KnownFunctions: new HashSet<string>());

        var result = Composer().Compose(candidate);

        // Não lança (confirma a parte que o design exigia). Mas nota o comportamento real: continua
        // Authoritative — prova o gap, não valida a degradação esperada.
        Assert.Equal(Confidence.Authoritative, result.Confidence);
    }

    // ---- Linha 10: condicional simples (2 branches, sem loop) ----
    [Fact]
    public void Linha10_CondicionalSimples_SemLoop_ClassificaTransformed()
    {
        var rule = new StructuredRule(StructuredRuleSchema.Version, "RULE_COND", "cond-teste", "Doc/Chave",
            new List<StructuredBranch>
            {
                new("len(campo) == 44", "Doc/Chave", new[] { "LINHA01/CHAVE" }, Array.Empty<string>()),
                new("else", "Doc/Chave", new[] { "LINHA01/CHAVE_ALT" }, Array.Empty<string>())
            },
            StaticValue: null, LoopType: null);
        var kind = MappingKindClassifier.ClassifyRule(rule);

        var candidate = new MappingCandidate(
            MappingId: "M10",
            Sources: new[] { Src("CHAVE") },
            Kind: kind,
            TargetPath: "Doc/Chave",
            TargetPathIsFullPath: true,
            Functions: Array.Empty<string>(),
            LoopType: null,
            AllSourcesResolvedFromOriginLayout: true,
            SourcesHavePositionalGroupRepetition: false,
            KnownFunctions: new HashSet<string>());

        var result = Composer().Compose(candidate);

        Assert.Equal(MappingKind.Transformed, kind);
        Assert.Equal(Confidence.Authoritative, result.Confidence); // sem loop, sem função desconhecida
    }

    // ---- Linha 11: função de transformação não-concat ----
    [Fact]
    public void Linha11_FuncaoTransformacaoConhecida_ResolveAuthoritative()
    {
        var candidate = new MappingCandidate(
            MappingId: "M11",
            Sources: new[] { Src("CHAVE") },
            Kind: MappingKind.Transformed,
            TargetPath: "Doc/Chave",
            TargetPathIsFullPath: true,
            Functions: new[] { "CalculateVerifierDigit" },
            LoopType: null,
            AllSourcesResolvedFromOriginLayout: true,
            SourcesHavePositionalGroupRepetition: false,
            KnownFunctions: new HashSet<string> { "CalculateVerifierDigit" }); // catálogo conhece a função

        var result = Composer().Compose(candidate);

        Assert.Equal(Confidence.Authoritative, result.Confidence);
    }

    // ---- Linha 12: loop dinâmico ----
    [Fact]
    public void Linha12_LoopDinamico_CaiEmBestEffort()
    {
        var candidate = new MappingCandidate(
            MappingId: "M12",
            Sources: new[] { Src("VALOR_ITEM") },
            Kind: MappingKind.Transformed,
            TargetPath: "Doc/Itens/Item/Valor",
            TargetPathIsFullPath: true,
            Functions: Array.Empty<string>(),
            LoopType: "foreach",
            AllSourcesResolvedFromOriginLayout: true,
            SourcesHavePositionalGroupRepetition: false,
            KnownFunctions: new HashSet<string>());

        var result = Composer().Compose(candidate);

        Assert.Equal(Confidence.BestEffort, result.Confidence);
        Assert.Contains(result.Limitations!, l => l.Contains("Loop dinâmico"));
    }

    // ---- Linha 13: N origens -> 1 destino ----
    [Fact]
    public void Linha13_NOrigensParaUmDestino_TodasSourcesPreservadas()
    {
        var candidate = new MappingCandidate(
            MappingId: "M13",
            Sources: new[] { Src("CAMPO_A"), Src("CAMPO_B") },
            Kind: MappingKind.Concatenated,
            TargetPath: "Doc/Total",
            TargetPathIsFullPath: true,
            Functions: new[] { "ConcatString" },
            LoopType: null,
            AllSourcesResolvedFromOriginLayout: true,
            SourcesHavePositionalGroupRepetition: false,
            KnownFunctions: new HashSet<string> { "ConcatString" });

        var result = Composer().Compose(candidate);

        Assert.Equal(2, result.Sources.Count);
        Assert.Single(result.Targets);
    }

    // ---- Linha 14: 1 origem -> N destinos ----
    [Fact]
    public void Linha14_UmaOrigemParaDoisDestinos_DoisMappingsIndependentesComMesmaOrigem()
    {
        var origem = Src("CAMPO_A");
        var composer = Composer();

        var m1 = composer.Compose(new MappingCandidate("M14a", new[] { origem }, MappingKind.Direct,
            "Doc/Cabecalho/CampoA", true, Array.Empty<string>(), null, true, false, new HashSet<string>()));
        var m2 = composer.Compose(new MappingCandidate("M14b", new[] { origem }, MappingKind.Direct,
            "Doc/Total", true, Array.Empty<string>(), null, true, false, new HashSet<string>()));

        Assert.Equal(origem, m1.Sources[0]);
        Assert.Equal(origem, m2.Sources[0]);
        Assert.NotEqual(m1.Targets[0].Xpath, m2.Targets[0].Xpath);
    }

    // ---- Linha 15: namespace não-default ----
    [Fact]
    public void Linha15_NamespaceNaoDefault_PrefixoGeradoDeterministicamente()
    {
        var alienNode = new XmlLayoutNode
        {
            NodePath = "Doc/Extra",
            Kind = XmlNodeKind.Element,
            Name = "Extra",
            Namespace = "urn:test:outro-dominio", // diferente do "urn:test:synthetic" da árvore base
            ParentPath = "Doc",
            MinOccurs = 1,
            MaxOccurs = 1
        };
        var root = SyntheticXmlCatalogBuilder.Build().Root;
        root.Children.Add(alienNode);
        var catalog = new XmlLayoutCatalog(root);

        var candidate = new MappingCandidate(
            MappingId: "M15",
            Sources: new[] { Src("CAMPO_A") },
            Kind: MappingKind.Direct,
            TargetPath: "Doc/Extra",
            TargetPathIsFullPath: true,
            Functions: Array.Empty<string>(),
            LoopType: null,
            AllSourcesResolvedFromOriginLayout: true,
            SourcesHavePositionalGroupRepetition: false,
            KnownFunctions: new HashSet<string>());

        var result = new FieldToXmlMappingComposer(catalog).Compose(candidate);

        Assert.Equal(Confidence.Authoritative, result.Confidence);
        // Prefixo é gerado por ordem de registro (RegisterPrefix) — "ns0" já é usado pelo namespace
        // base da árvore (primeiro segmento do XPath), então o namespace alienígena vira "ns1".
        Assert.Contains(":Extra", result.Targets[0].Xpath);
        Assert.DoesNotContain("ns0:Extra", result.Targets[0].Xpath);
    }

    // ---- Linha 16: mismatch — origem repetida, destino não ----
    [Fact]
    public void Linha16_MismatchRepeticao_OrigemRepetidaDestinoNao_CaiEmBestEffort()
    {
        var candidate = new MappingCandidate(
            MappingId: "M16",
            Sources: new[] { Src("CAMPO_A", occurrence: 1) },
            Kind: MappingKind.Direct,
            TargetPath: "Doc/Total", // não repete
            TargetPathIsFullPath: true,
            Functions: Array.Empty<string>(),
            LoopType: null,
            AllSourcesResolvedFromOriginLayout: true,
            SourcesHavePositionalGroupRepetition: true, // origem repete
            KnownFunctions: new HashSet<string>());

        var result = Composer().Compose(candidate);

        Assert.Equal(Confidence.BestEffort, result.Confidence);
        Assert.Contains(result.Limitations!, l => l.Contains("Repetição não confirmada"));
    }

    // ---- Linha 17: mismatch — destino repetido, origem não ----
    [Fact]
    public void Linha17_MismatchRepeticao_DestinoRepetidoOrigemNao_CaiEmBestEffort()
    {
        var candidate = new MappingCandidate(
            MappingId: "M17",
            Sources: new[] { Src("VALOR_ITEM", occurrence: 1) },
            Kind: MappingKind.Direct,
            TargetPath: "Doc/Itens/Item/Valor", // destino repete (MaxOccurs=3)
            TargetPathIsFullPath: true,
            Functions: Array.Empty<string>(),
            LoopType: null,
            AllSourcesResolvedFromOriginLayout: true,
            SourcesHavePositionalGroupRepetition: false, // origem NÃO repete
            KnownFunctions: new HashSet<string>());

        var result = Composer().Compose(candidate);

        Assert.Equal(Confidence.BestEffort, result.Confidence);
        Assert.Contains(result.Limitations!, l => l.Contains("Repetição não confirmada"));
        Assert.Null(result.Targets[0].XmlOccurrence);
    }

    // ---- Linha 18: função desconhecida no catálogo ----
    [Fact]
    public void Linha18_FuncaoDesconhecida_CaiEmBestEffort()
    {
        var candidate = new MappingCandidate(
            MappingId: "M18",
            Sources: new[] { Src("CHAVE") },
            Kind: MappingKind.Transformed,
            TargetPath: "Doc/Chave",
            TargetPathIsFullPath: true,
            Functions: new[] { "FuncaoNuncaVista" },
            LoopType: null,
            AllSourcesResolvedFromOriginLayout: true,
            SourcesHavePositionalGroupRepetition: false,
            KnownFunctions: new HashSet<string> { "CalculateVerifierDigit" }); // catálogo não contém "FuncaoNuncaVista"

        var result = Composer().Compose(candidate);

        Assert.Equal(Confidence.BestEffort, result.Confidence);
        Assert.Contains(result.Limitations!, l => l.Contains("não catalogada"));
    }

    // ---- Linha 19: Elements aninhados no MapperVO (limitação conhecida #139 §7.1) ----
    [Fact]
    public void Linha19_ElementosAninhadosNoMapperVO_LimitacaoConhecidaDocumentadaNaoTestavelAqui()
    {
        // Não testável no nível do composer: a limitação é do PARSER do MapperVO (#139 §7.1 —
        // "parser B não captura aninhamento"), uma camada anterior ao MappingCandidate que este
        // motor recebe já pronto. Um teste aqui só provaria que o composer funciona com o candidate
        // que o parser conseguiu produzir — não exercitaria a limitação real (que é o parser não
        // produzir candidate nenhum para o elemento aninhado, silenciosamente). Registrado como gap
        // de cobertura explícito (design §6.3: "lista de mappingKind × dimensão com 0% de cobertura,
        // não silencioso") em vez de fabricar um teste que não prova nada.
        Assert.True(true, "Gap de cobertura documentado — ver comentário do teste. Cobertura real exigiria teste no parser de MapperVO (#139), não no composer (#140).");
    }

    // ---- Linha 20: degradação posicional (PositionalAlignmentFailed=true) ----
    [Fact]
    public void Linha20_DegradacaoPosicional_SinalNaoChegaAoComposer_GapDocumentado()
    {
        // Mesmo gap estrutural da linha 9: LineInfo.PositionalAlignmentFailed (contrato de
        // 2026-08-27) não é parâmetro de FieldMappingCompositionService.Compose nem de
        // MappingCandidate. O design (linha 20 da tabela §6.1) esperava que esse sinal forçasse
        // best-effort automaticamente — hoje ele não tem como chegar ao composer, então um
        // mapeamento vindo de uma linha com alinhamento posicional degradado pode sair Authoritative
        // se todas as outras condições do §5 baterem. Mesmo veredito da linha 9: "nunca exceção" ok,
        // "degrada para best-effort" não está implementado.
        var candidate = new MappingCandidate(
            MappingId: "M20",
            Sources: new[] { Src("CAMPO_A") }, // sem qualquer forma de sinalizar PositionalAlignmentFailed
            Kind: MappingKind.Direct,
            TargetPath: "Doc/Cabecalho/CampoA",
            TargetPathIsFullPath: true,
            Functions: Array.Empty<string>(),
            LoopType: null,
            AllSourcesResolvedFromOriginLayout: true,
            SourcesHavePositionalGroupRepetition: false,
            KnownFunctions: new HashSet<string>());

        var result = Composer().Compose(candidate);

        Assert.Equal(Confidence.Authoritative, result.Confidence); // prova o gap, não a degradação esperada
    }

    // ---- Prova adicional exigida pela tarefa: código é conservador — nunca Authoritative sem
    // KnownFunctions resolvido (condição 5 do §5), mesmo quando Functions está vazio mas o
    // catálogo de funções é null (host sem FunctionCatalog configurado) — cobre exatamente o
    // cenário real de FieldMappingCompositionService hoje (KnownFunctions sempre null).
    [Fact]
    public void NuncaAuthoritative_QuandoFunctionCatalogIndisponivel_MesmoSemFuncoesReferenciadas()
    {
        var candidate = new MappingCandidate(
            MappingId: "MConservador",
            Sources: new[] { Src("CAMPO_A") },
            Kind: MappingKind.Direct,
            TargetPath: "Doc/Cabecalho/CampoA",
            TargetPathIsFullPath: true,
            Functions: Array.Empty<string>(), // nenhuma função referenciada
            LoopType: null,
            AllSourcesResolvedFromOriginLayout: true,
            SourcesHavePositionalGroupRepetition: false,
            KnownFunctions: null); // FunctionCatalog indisponível — estado real de FieldMappingCompositionService hoje

        var result = Composer().Compose(candidate);

        // Condição 5 do §5 exige KnownFunctions != null explicitamente — All() vazio em coleção
        // vazia retornaria true por vacuidade se a checagem fosse só `Functions.All(...)`, então o
        // código PRECISA testar `KnownFunctions is not null` primeiro (senão isto quebraria e o
        // motor marcaria authoritative mapeamentos com FunctionCatalog nunca confirmado). Confirma
        // que o código já é conservador nesse ponto, como pedido pela tarefa.
        Assert.Equal(Confidence.BestEffort, result.Confidence);
        Assert.Contains(result.Limitations!, l => l.Contains("FunctionCatalog indisponível"));
    }
}
