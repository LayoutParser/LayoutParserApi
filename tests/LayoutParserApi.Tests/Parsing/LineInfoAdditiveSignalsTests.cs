using System.Text;

using LayoutParserApi.Models.Logging;
using LayoutParserApi.Models.Parsing;
using LayoutParserApi.Services.Implementations;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Parsing.Implementations;
using LayoutParserApi.Services.Validation;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Parsing
{
    /// <summary>
    /// Contrato aditivo 2026-08-27: <see cref="LayoutParserApi.Models.Entities.LineInfo.IsDeclaredEmpty"/>
    /// e <see cref="LayoutParserApi.Models.Entities.LineInfo.PositionalAlignmentFailed"/>
    /// (PR #198, <c>docs/architecture/contrato-linha-vazia-progresso-e-degradacao-posicional-2026-08-27.md</c>).
    ///
    /// <para>Sem cobertura no PR original — este arquivo fecha o gap apontado no QA gate.</para>
    /// </summary>
    public class LineInfoAdditiveSignalsTests
    {
        // ── IsDeclaredEmpty ──────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Linha_identificada_com_conteudo_reporta_IsDeclaredEmpty_false()
        {
            var resultado = await ParseAsync(LayoutMqDe20Chars, "000001000AAAAAAAAAAA");

            Assert.True(resultado.Success, resultado.ErrorMessage);
            var linha = Assert.Single(resultado.LineInfos);
            Assert.Equal("LINHA000", linha.LineName);
            Assert.False(linha.IsDeclaredEmpty);
        }

        /// <summary>
        /// CORRIGIDO (era ACHADO DE QA): <c>IsDeclaredEmpty</c> agora é calculado sobre o(s)
        /// CAMPO(S) DE DADO da linha (via <c>allDataFieldsBlank</c>, populado em
        /// <c>ParseLineFields</c>), não mais sobre <c>currentLine</c> — a linha física inteira
        /// (Sequencia + InitialValue + campos). Antes, uma linha cujo único campo de dado
        /// (<c>DADO</c>) era inteiramente espaços em branco ainda carregava "000001" (Sequencia) +
        /// "000" (InitialValue) não-espaços no início, então <c>IsNullOrWhiteSpace(currentLine)</c>
        /// nunca era true — tornando o sinal inalcançável (ver spec §1: a intenção é sinalizar
        /// "campo de dado vazio", não "linha bruta vazia"). Este é o caso real que motivou o pedido
        /// do usuário.
        /// </summary>
        [Fact]
        public async Task Dado_totalmente_em_branco_no_campo_liga_IsDeclaredEmpty()
        {
            var resultado = await ParseAsync(LayoutMqDe20Chars, "000001000" + new string(' ', 11));

            Assert.True(resultado.Success, resultado.ErrorMessage);
            var linha = Assert.Single(resultado.LineInfos);

            string? dado = resultado.ParsedFields.SingleOrDefault(f => f.FieldName == "DADO")?.Value;
            Assert.True(string.IsNullOrWhiteSpace(dado)); // o dado real está vazio...
            Assert.True(linha.IsDeclaredEmpty);            // ...e agora o sinal aditivo percebe.
        }

        /// <summary>
        /// ACHADO DE QA (mesma raiz do teste acima, ângulo complementar): uma linha FISICAMENTE
        /// 100% em branco no documento não passa nem pelo enriquecimento de <see cref="LineInfo"/> —
        /// nenhum matcher em <c>IsLineValidForConfig</c> casa uma linha em branco com uma config de
        /// layout (todos os caminhos exigem prefixo não-espaço: sequência numérica, "HEADER",
        /// "EDI_"/"ZRSDM_", ou "999999"). A linha cai no ramo de "não identificada"
        /// (<c>unidentifiedLines</c>), e <c>lineInfos</c> fica vazio para ela. Ou seja, pelo desenho
        /// atual do matcher, <c>IsDeclaredEmpty=true</c> é, na prática, inalcançável para qualquer
        /// formato hoje suportado (MQSeries/IDOC) — não é regressão deste PR (o matcher já era
        /// assim), mas é uma lacuna de design que esvazia o valor do sinal novo.
        /// </summary>
        [Fact]
        public async Task ACHADO_linha_totalmente_em_branco_nao_gera_LineInfo_nenhum()
        {
            var resultado = await ParseAsync(LayoutMqDe20Chars, new string(' ', 20));

            Assert.True(resultado.Success, resultado.ErrorMessage);
            Assert.Empty(resultado.LineInfos);
            Assert.Empty(resultado.ParsedFields);
        }

        // ── PositionalAlignmentFailed ────────────────────────────────────────────────────────────

        [Fact]
        public async Task Linha_saudavel_com_campos_bem_alinhados_nao_dispara_o_sinal()
        {
            var resultado = await ParseAsync(LayoutMqDe20Chars, "000001000AAAAAAAAAAA");

            Assert.True(resultado.Success, resultado.ErrorMessage);
            var linha = Assert.Single(resultado.LineInfos);
            Assert.False(linha.PositionalAlignmentFailed);
        }

        /// <summary>
        /// Reprodução sintética do colapso posicional (não depende da amostra real LINHA006/
        /// correlationId pendente): dois campos declarados com <c>LengthField=0</c> entre a
        /// Sequencia e o campo de dado. Como <c>endPosition = fieldStart + LengthField - 1</c>,
        /// um campo de comprimento 0 devolve <c>currentPosition</c> inalterado — o próximo campo
        /// nasce exatamente no mesmo <c>Start</c>. É o mesmo sintoma observável do LINHA006
        /// (LengthField mal resolvido cascateando), só que provocado deliberadamente aqui.
        /// </summary>
        [Fact]
        public async Task Dois_campos_consecutivos_com_LengthField_zero_disparam_o_sinal()
        {
            var resultado = await ParseAsync(LayoutMqComColisaoPosicional, "000001000AAAAAAAAAAA");

            Assert.True(resultado.Success, resultado.ErrorMessage);
            var linha = Assert.Single(resultado.LineInfos);
            Assert.True(linha.PositionalAlignmentFailed);

            var campoA = resultado.ParsedFields.Single(f => f.FieldName == "CampoColapsadoA");
            var campoB = resultado.ParsedFields.Single(f => f.FieldName == "CampoColapsadoB");
            Assert.Equal(campoA.Start, campoB.Start);
        }

        // ─────────────────────────────── infraestrutura do teste ───────────────────────────────

        /// <summary>Mesmo layout mínimo de <c>ValidationErrorRecordIdentityTests</c> (20 chars/linha).</summary>
        private const string LayoutMqDe20Chars = """
            <?xml version="1.0" encoding="utf-8"?>
            <LayoutVO>
              <LayoutGuid>LAY_TESTE_MQ</LayoutGuid>
              <LayoutType>MQSeries</LayoutType>
              <Name>LAY_TESTE_MQ</Name>
              <LimitOfCaracters>20</LimitOfCaracters>
              <WithBreakLines>false</WithBreakLines>
              <Elements>
                <Element type="LineElementVO">
                  <ElementGuid>ELM_LINHA000</ElementGuid>
                  <Name>LINHA000</Name>
                  <Sequence>1</Sequence>
                  <InitialValue>000</InitialValue>
                  <Elements>
                    <Element type="FieldElementVO">
                      <ElementGuid>FLD_SEQUENCIA</ElementGuid>
                      <Name>Sequencia</Name>
                      <Sequence>1</Sequence>
                      <LengthField>6</LengthField>
                    </Element>
                    <Element type="FieldElementVO">
                      <ElementGuid>FLD_DADO</ElementGuid>
                      <Name>DADO</Name>
                      <Sequence>2</Sequence>
                      <LengthField>11</LengthField>
                    </Element>
                  </Elements>
                </Element>
              </Elements>
            </LayoutVO>
            """;

        /// <summary>
        /// Mesmo layout de controle, com dois campos <c>LengthField=0</c> inseridos entre a
        /// Sequencia e o DADO — provoca colisão de <c>Start</c> sem alterar o tamanho físico da
        /// linha (0 chars cada, então o texto de 20 chars continua válido).
        /// </summary>
        private const string LayoutMqComColisaoPosicional = """
            <?xml version="1.0" encoding="utf-8"?>
            <LayoutVO>
              <LayoutGuid>LAY_TESTE_MQ_COLISAO</LayoutGuid>
              <LayoutType>MQSeries</LayoutType>
              <Name>LAY_TESTE_MQ_COLISAO</Name>
              <LimitOfCaracters>20</LimitOfCaracters>
              <WithBreakLines>false</WithBreakLines>
              <Elements>
                <Element type="LineElementVO">
                  <ElementGuid>ELM_LINHA000</ElementGuid>
                  <Name>LINHA000</Name>
                  <Sequence>1</Sequence>
                  <InitialValue>000</InitialValue>
                  <Elements>
                    <Element type="FieldElementVO">
                      <ElementGuid>FLD_SEQUENCIA</ElementGuid>
                      <Name>Sequencia</Name>
                      <Sequence>1</Sequence>
                      <LengthField>6</LengthField>
                    </Element>
                    <Element type="FieldElementVO">
                      <ElementGuid>FLD_COLAPSO_A</ElementGuid>
                      <Name>CampoColapsadoA</Name>
                      <Sequence>2</Sequence>
                      <LengthField>0</LengthField>
                    </Element>
                    <Element type="FieldElementVO">
                      <ElementGuid>FLD_COLAPSO_B</ElementGuid>
                      <Name>CampoColapsadoB</Name>
                      <Sequence>3</Sequence>
                      <LengthField>0</LengthField>
                    </Element>
                    <Element type="FieldElementVO">
                      <ElementGuid>FLD_DADO</ElementGuid>
                      <Name>DADO</Name>
                      <Sequence>4</Sequence>
                      <LengthField>11</LengthField>
                    </Element>
                  </Elements>
                </Element>
              </Elements>
            </LayoutVO>
            """;

        private static async Task<ParsingResult> ParseAsync(string layoutXml, string documento)
        {
            var techLogger = new NoOpTechLogger();

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Mantém o aprendizado ML (fire-and-forget do ParseAsync) fora do repo.
                    ["ML:LearningDataPath"] = Path.Combine(Path.GetTempPath(), "lp-tests", "DocumentPatterns"),
                    ["ML:TrainingSamplesPath"] = Path.Combine(Path.GetTempPath(), "lp-tests", "TrainingSamples")
                })
                .Build();

            var service = new LayoutParserService(
                techLogger,
                new NoOpAuditLogger(),
                new LineSplitter(techLogger),
                new LayoutValidator(techLogger),
                new LayoutNormalizer(),
                new DocumentValidationService(techLogger, NullLogger<DocumentValidationService>.Instance),
                new DocumentMLValidationService(techLogger, NullLogger<DocumentMLValidationService>.Instance, config),
                NullLogger<LayoutParserService>.Instance);

            using var layoutStream = new MemoryStream(Encoding.UTF8.GetBytes(layoutXml));
            using var txtStream = new MemoryStream(Encoding.UTF8.GetBytes(documento));

            return await service.ParseAsync(layoutStream, txtStream);
        }

        private sealed class NoOpTechLogger : ITechLogger
        {
            public void LogTechnical(LogEntry entry) { }
        }

        private sealed class NoOpAuditLogger : IAuditLogger
        {
            public void LogAudit(AuditLogEntry entry) { }
        }
    }
}
