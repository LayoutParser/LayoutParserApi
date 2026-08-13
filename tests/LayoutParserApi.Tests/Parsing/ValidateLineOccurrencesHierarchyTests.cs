using System.Reflection;

using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Implementations;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Parsing.Implementations;
using LayoutParserApi.Services.Validation;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Newtonsoft.Json;

namespace LayoutParserApi.Tests.Parsing
{
    /// <summary>
    /// Regressão da Issue #48: <c>ValidateLineOccurrences</c> comparava
    /// <c>ParsedField.LineName</c> (gravado SEM hierarquia, ex.: "LINHA021") contra
    /// <c>LineElement.Name</c> COM hierarquia (ex.: "LINHA020.LINHA021"). Para qualquer linha
    /// aninhada isso nunca casava, então o log sempre reportava "0 ocorrencia(s)" mesmo quando o
    /// documento tinha ocorrências físicas reais — apenas o log, não o parse em si, era afetado.
    ///
    /// O fix aplica <c>ObterLineNameSemHierarquia</c> em <c>ValidateLineOccurrences</c>, na mesma
    /// convenção já usada no resto do parser (ver linhas ~430 e ~1013 de
    /// <c>Services/Implementations/LayoutParserService .cs</c>).
    /// </summary>
    public class ValidateLineOccurrencesHierarchyTests
    {
        [Fact]
        public void Linha_aninhada_com_hierarquia_conta_ocorrencias_reais_no_log()
        {
            // Linha filha aninhada: nome COM hierarquia, como vem do XML do layout.
            var linhaFilha = new LineElement
            {
                Name = "LINHA020.LINHA021",
                Sequence = 1,
                MinimalOccurrence = 1,
                MaximumOccurrence = 5,
                Elements = new List<string>()
            };

            var linhaPai = new LineElement
            {
                Name = "LINHA020",
                Sequence = 0,
                Elements = new List<string> { JsonConvert.SerializeObject(linhaFilha) }
            };

            var layout = new Layout
            {
                Elements = new List<LineElement> { linhaPai }
            };

            // Campos como o restante do parser realmente grava: LineName SEM hierarquia
            // ("LINHA021"), 2 ocorrências físicas (Occurrence 1 e 2).
            var parsedFields = new List<ParsedField>
            {
                new ParsedField { LineName = "LINHA021", FieldName = "Campo1", Occurrence = 1 },
                new ParsedField { LineName = "LINHA021", FieldName = "Campo1", Occurrence = 2 }
            };

            var techLogger = new CapturingTechLogger();
            var service = CreateService(techLogger);

            InvokeValidateLineOccurrences(service, layout, parsedFields);

            var mensagemInfo = techLogger.Entries.SingleOrDefault(
                e => e.Level == "Info" && e.Message.StartsWith("LINHA020.LINHA021:"));

            Assert.NotNull(mensagemInfo);
            // Antes do fix: sempre "0 ocorrencia(s)" (nunca casava). Depois: reflete as 2 reais.
            Assert.Equal("LINHA020.LINHA021: 2 ocorrencia(s) (esperado: 1-5)", mensagemInfo!.Message);

            // Sem o fix, MinimalOccurrence=1 > actualOccurrences=0 dispararia um Warning falso.
            Assert.DoesNotContain(techLogger.Entries, e => e.Level == "Warn");
        }

        private static LayoutParserService CreateService(ITechLogger techLogger)
        {
            var auditLogger = new NoOpAuditLogger();

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ML:LearningDataPath"] = Path.Combine(Path.GetTempPath(), "lp-tests", "DocumentPatterns"),
                    ["ML:TrainingSamplesPath"] = Path.Combine(Path.GetTempPath(), "lp-tests", "TrainingSamples")
                })
                .Build();

            return new LayoutParserService(
                techLogger,
                auditLogger,
                new LineSplitter(techLogger),
                new LayoutValidator(techLogger),
                new LayoutNormalizer(),
                new DocumentValidationService(techLogger, NullLogger<DocumentValidationService>.Instance),
                new DocumentMLValidationService(techLogger, NullLogger<DocumentMLValidationService>.Instance, config),
                NullLogger<LayoutParserService>.Instance);
        }

        private static void InvokeValidateLineOccurrences(
            LayoutParserService service, Layout layout, List<ParsedField> parsedFields)
        {
            var metodo = typeof(LayoutParserService).GetMethod(
                "ValidateLineOccurrences", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.NotNull(metodo);
            metodo!.Invoke(service, new object[] { layout, parsedFields });
        }

        private sealed class CapturingTechLogger : ITechLogger
        {
            public List<LogEntry> Entries { get; } = new();

            public void LogTechnical(LogEntry entry) => Entries.Add(entry);
        }

        private sealed class NoOpAuditLogger : IAuditLogger
        {
            public void LogAudit(AuditLogEntry entry) { }
        }
    }
}
