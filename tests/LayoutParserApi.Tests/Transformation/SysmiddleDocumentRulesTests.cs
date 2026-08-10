using LayoutParserLowCodeRunner;

namespace LayoutParserApi.Tests.Transformation
{
    /// <summary>
    /// Trava as regras de documento do caminho VIVO do runner low-code.
    ///
    /// <para>Contexto (docs/architecture/decisao-remover-dependencia-appconnector.md §3): o
    /// <c>MappersHelper</c> do <c>appConnector</c> tem DUAS funções de execução que parecem a mesma
    /// e não são. O port original do runner replicou a <c>ExecuteMappingDocument</c>; o caminho que
    /// de fato produziu o gabarito real de <c>.claude/tmp/exemplos/</c> é a
    /// <c>ExecuteMappingDocumentById</c>. Elas divergem em três pontos, e dois deles são puros
    /// (string/XML) — portanto testáveis aqui:</para>
    ///
    /// <list type="number">
    ///   <item><description><c>InsertDeclaration</c> só quando o documento <b>não</b> começa com
    ///   <c>&lt;?xml</c>.</description></item>
    ///   <item><description>Pós-processamento NF-e <b>desligado</b>.</description></item>
    /// </list>
    ///
    /// <para>O terceiro (a chamada a <c>ExecuteParser("", document)</c> com resultado descartado)
    /// depende do SDK Sysmiddle x86 e vive em <c>SysmiddleMapperExecutor</c>; é coberto pela
    /// verificação de equivalência byte a byte contra o gabarito, não por esta suíte.</para>
    /// </summary>
    public class SysmiddleDocumentRulesTests
    {
        // ───────────────────── divergência 1: a exclusão do "<?xml" ─────────────────────

        /// <summary>
        /// O ponto EXATO da divergência. A <c>ExecuteMappingDocument</c> (função errada, que o port
        /// original replicava) reescreveria a declaração já existente para
        /// <c>encoding="utf-8"</c> — mudando o que entra no mapeador. A <c>ById</c> deixa como está.
        /// </summary>
        [Fact]
        public void Documento_que_ja_tem_declaracao_xml_nao_e_tocado()
        {
            const string doc = "<?xml version=\"1.0\" encoding=\"iso-8859-1\"?><nfeProc><a/></nfeProc>";

            Assert.False(SysmiddleDocumentRules.DeveInserirDeclaracao(doc));
            Assert.Equal(doc, SysmiddleDocumentRules.AplicarDeclaracaoXmlSeNecessario(doc));
        }

        /// <summary>
        /// Mutação da guarda acima: se alguém remover a exclusão do <c>&lt;?xml</c>, o documento
        /// perderia o <c>encoding="iso-8859-1"</c>. Este teste falha nesse caso — é o que impede a
        /// regressão de voltar como "simplificação".
        /// </summary>
        [Fact]
        public void Insercao_forcada_de_declaracao_destruiria_o_encoding_original()
        {
            const string doc = "<?xml version=\"1.0\" encoding=\"iso-8859-1\"?><nfeProc><a/></nfeProc>";

            // Comportamento da ExecuteMappingDocument (a função ERRADA), invocado direto:
            var comoSeFosseAOutraFuncao = SysmiddleDocumentRules.InsertDeclaration(doc);

            Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", comoSeFosseAOutraFuncao);
            Assert.DoesNotContain("iso-8859-1", comoSeFosseAOutraFuncao);

            // ...e é justamente por isso que o caminho vivo NÃO passa por lá.
            Assert.NotEqual(comoSeFosseAOutraFuncao, SysmiddleDocumentRules.AplicarDeclaracaoXmlSeNecessario(doc));
        }

        [Fact]
        public void Documento_xml_sem_declaracao_recebe_a_declaracao()
        {
            const string doc = "<nfeProc><a/></nfeProc>";

            Assert.True(SysmiddleDocumentRules.DeveInserirDeclaracao(doc));
            Assert.Equal("<?xml version=\"1.0\" encoding=\"utf-8\"?><nfeProc><a/></nfeProc>",
                SysmiddleDocumentRules.AplicarDeclaracaoXmlSeNecessario(doc));
        }

        /// <summary>
        /// O caso REAL deste projeto: o documento de entrada é posicional (MQSeries/TXT), não XML.
        /// Nada pode ser prefixado — prefixar corromperia o input do parser posicional.
        /// </summary>
        [Theory]
        [InlineData("0001NOTA FISCAL   000123")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("<sem fechamento")]
        [InlineData("sem abertura>")]
        public void Documento_que_nao_parece_xml_passa_intacto(string doc)
        {
            Assert.False(SysmiddleDocumentRules.DeveInserirDeclaracao(doc));
            Assert.Equal(doc, SysmiddleDocumentRules.AplicarDeclaracaoXmlSeNecessario(doc));
        }

        /// <summary>
        /// Guarda defensiva: no original um documento nulo estouraria NullReference dentro do
        /// try/catch e viraria resultado vazio silencioso. O runner nunca passa nulo (vem de
        /// <c>File.ReadAllText</c>), mas a guarda garante que a regra não tem esse buraco.
        /// </summary>
        [Fact]
        public void Documento_nulo_nao_estoura()
        {
            Assert.False(SysmiddleDocumentRules.DeveInserirDeclaracao(null));
            Assert.Null(SysmiddleDocumentRules.AplicarDeclaracaoXmlSeNecessario(null));
        }

        // ───────────────── divergência 3: pós-processamento NF-e desligado ─────────────────

        /// <summary>
        /// Com a flag desligada (o default), o documento sai IDÊNTICO — nem sequer passa pelo
        /// round-trip de <c>XmlDocument</c>. É isso que preserva a equivalência byte a byte com o
        /// gabarito, porque o round-trip por si só reserializa o XML.
        /// </summary>
        [Fact]
        public void Pos_processamento_desligado_nao_toca_no_documento()
        {
            const string doc = "<?xml  version=\"1.0\"?><enviNFe versao=\"4.00\"><idLote>00001</idLote></enviNFe>";

            Assert.Same(doc, SysmiddleDocumentRules.AplicarPosProcessamentoNFe(doc, ativo: false));
        }

        /// <summary>
        /// Mutação do teste acima: prova que o default DESLIGADO é carga útil, não enfeite.
        ///
        /// <para>Ligado, o round-trip de <c>XmlDocument</c> normaliza o espaço duplo de
        /// <c>&lt;?xml  version=</c> — exatamente o byte que separa a saída do runner (4246) do
        /// gabarito (4245). Se este teste passar a acusar igualdade, é porque alguém neutralizou a
        /// flag e o teste acima virou vacuidade.</para>
        /// </summary>
        [Fact]
        public void Pos_processamento_ligado_reserializa_e_muda_bytes()
        {
            const string doc = "<?xml  version=\"1.0\"?><enviNFe versao=\"4.00\"><idLote>00001</idLote></enviNFe>";

            var ligado = SysmiddleDocumentRules.AplicarPosProcessamentoNFe(doc, ativo: true);

            Assert.NotEqual(doc, ligado);
            Assert.Contains("<?xml version=\"1.0\"", ligado);   // espaço simples: foi reserializado
            Assert.Contains("<idLote>00001</idLote>", ligado);  // ...mas o conteúdo continua lá
        }

        /// <summary>
        /// Degradação graciosa herdada do original: documento que não é XML válido (o resultado de
        /// um mapeamento que falhou, por exemplo) atravessa sem exceção.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData("nao é xml")]
        [InlineData("<aberto>")]
        public void Pos_processamento_ligado_degrada_em_documento_invalido(string doc)
        {
            Assert.Equal(doc, SysmiddleDocumentRules.AplicarPosProcessamentoNFe(doc, ativo: true));
        }
    }
}
