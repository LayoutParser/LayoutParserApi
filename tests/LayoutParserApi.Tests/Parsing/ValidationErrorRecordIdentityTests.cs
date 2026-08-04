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
    /// Identidade de REGISTRO no erro de validação (spec §5.1).
    ///
    /// <para>O erro nasce no <c>DocumentValidationService</c>, que só conhece (texto, tamanho de
    /// linha) e devolve um intervalo de bytes. Intervalo de bytes <b>não generaliza</b>: noutro
    /// documento o mesmo segmento está em outra posição. O <c>recordGuid</c> generaliza, porque o
    /// segmento é estável no layout — é o que transforma o erro em rótulo aproveitável.</para>
    ///
    /// <para>Os testes rodam o parser REAL de ponta a ponta (layout XML + documento), não um
    /// mapeamento simulado: a identidade tem que sair do mesmo matcher que o parser usa para casar
    /// linha ↔ registro, senão o erro apontaria um segmento e o campo parseado outro.</para>
    /// </summary>
    public class ValidationErrorRecordIdentityTests
    {
        [Fact]
        public async Task Erro_de_enquadramento_carrega_o_registro_do_layout()
        {
            // Linha 1 completa (20 chars) + linha 2 truncada (12) → erro de última linha curta.
            var resultado = await ParseAsync(LayoutMqDe20Chars, "000001000AAAAAAAAAAA" + "000002000BBB");

            Assert.True(resultado.Success, resultado.ErrorMessage);

            var erro = Assert.Single(resultado.ValidationErrors);
            Assert.Equal("LINHA000", erro.RecordName);
            Assert.Equal("ELM_LINHA000", erro.RecordGuid);
        }

        /// <summary>
        /// O rótulo de campo continua NULO mesmo quando o de registro resolve. Não é esquecimento:
        /// preencher <c>fieldGuid</c> com o GUID do registro seria dado mal rotulado, e ensinaria à
        /// IA que a granularidade da atribuição é o segmento.
        /// </summary>
        [Fact]
        public async Task Identidade_de_campo_continua_nula_mesmo_com_registro_resolvido()
        {
            var resultado = await ParseAsync(LayoutMqDe20Chars, "000001000AAAAAAAAAAA" + "000002000BBB");

            var erro = Assert.Single(resultado.ValidationErrors);

            Assert.NotNull(erro.RecordGuid);
            Assert.Null(erro.FieldName);
            Assert.Null(erro.FieldGuid);
            Assert.Null(erro.TargetXPath);
            // E o GUID do registro NÃO vazou para o campo de campo.
            Assert.NotEqual(erro.RecordGuid, erro.FieldGuid);
        }

        /// <summary>
        /// Linha cujo prefixo nem é uma sequência não casa com registro nenhum. O erro fica sem
        /// identidade — e é assim que tem que ser: identidade ausente é preferível a identidade
        /// errada, porque o consumidor deste campo é o dataset de aprendizado.
        /// </summary>
        [Fact]
        public async Task Linha_que_nao_casa_com_registro_fica_sem_identidade()
        {
            var resultado = await ParseAsync(LayoutMqDe20Chars, "ABCDEF000AAAAAAAAAAA");

            Assert.True(resultado.Success, resultado.ErrorMessage);

            var erro = Assert.Single(resultado.ValidationErrors);
            Assert.Contains("Sequência inválida", erro.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Null(erro.RecordName);
            Assert.Null(erro.RecordGuid);
        }

        [Fact]
        public async Task Documento_sem_defeito_nao_produz_erro_de_validacao()
        {
            var resultado = await ParseAsync(LayoutMqDe20Chars, "000001000AAAAAAAAAAA");

            Assert.True(resultado.Success, resultado.ErrorMessage);
            Assert.Empty(resultado.ValidationErrors);
            Assert.Equal(DocumentHealth.Clean, DocumentHealth.Resolve(resultado.ValidationErrors));
        }

        // ─────────────────────────────── infraestrutura do teste ───────────────────────────────

        /// <summary>
        /// Layout MQSeries mínimo de 20 caracteres por linha: <c>Sequencia</c>(6) +
        /// <c>InitialValue</c>(3) + <c>DADO</c>(11).
        ///
        /// <para>A declaração <c>&lt;?xml?&gt;</c> é obrigatória: <c>LoadLayoutAsync</c> descarta o
        /// primeiro token <c>&lt;...&gt;</c> assumindo que é a declaração, então um layout sem ela
        /// perde o elemento RAIZ silenciosamente. Todo layout real tem.</para>
        ///
        /// <para><c>LayoutType=MQSeries</c> não é decoração: a validação de tamanho de linha (a
        /// única que produz <c>ValidationErrors</c> hoje) só roda para layouts MQSeries.</para>
        /// </summary>
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
