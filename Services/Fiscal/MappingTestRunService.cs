using System.Collections.Concurrent;
using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;

using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.XmlAnalysis;

using XslSynth.Core;

namespace LayoutParserApi.Services.Fiscal
{
    /// <summary>
    /// Implementação de <see cref="IMappingTestRunService"/> — Slice 5 (issue #231) + runner
    /// determinístico de TCL (issue #421). Executa <c>engine=xslt</c> via <see cref="XsltApplier"/>
    /// (compila e roda o XSLT de verdade) e <c>engine=tcl</c> via <see cref="TclRuleApplier"/>
    /// (interpreta diretamente as regras estruturadas aceitas/editadas do draft, sem depender de um
    /// interpretador Tcl real — não existe nenhum neste repositório; o runner Sysmiddle está fora de
    /// alcance por bloqueio de licença). Os dois caminhos compartilham diff canônico, validação XSD
    /// best-effort e provenance por regra — mesmo contrato de <see cref="MappingTestRunSummary"/>.
    /// </summary>
    public sealed class MappingTestRunService : IMappingTestRunService
    {
        private readonly ILogger<MappingTestRunService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private static readonly ConcurrentDictionary<Guid, TestRunJobState> Jobs = new();

        public MappingTestRunService(ILogger<MappingTestRunService> logger, IServiceScopeFactory scopeFactory)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
        }

        public async Task<Guid> EnqueueAsync(
            Guid workspaceId,
            Guid draftId,
            Guid releaseId,
            Guid userId,
            string inputXml,
            string expectedXml,
            string? xsdVersion,
            string correlationId,
            CancellationToken cancellationToken)
        {
            MappingReleaseDetail? release;
            MappingDraftDetail? draft;
            using (var scope = _scopeFactory.CreateScope())
            {
                var releaseStore = scope.ServiceProvider.GetRequiredService<IMappingReleaseStore>();
                release = await releaseStore.GetReleaseIfMemberAsync(releaseId, userId, cancellationToken);

                var draftStore = scope.ServiceProvider.GetRequiredService<IMappingDraftStore>();
                draft = await draftStore.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
            }

            if (release == null || release.WorkspaceId != workspaceId || release.DraftId != draftId)
                throw new InvalidOperationException("Release não encontrada, não compilada ou fora do workspace.");

            if (draft == null)
                throw new InvalidOperationException("Draft não encontrado.");

            var jobId = Guid.NewGuid();
            var state = new TestRunJobState { JobId = jobId, Status = TestRunJobStatus.Queued, ReleaseId = releaseId };
            Jobs[jobId] = state;

            var rulesById = draft.Rules.ToDictionary(r => r.RuleId);

            // ✅ Fire-and-forget real (dotnet-standards.md §Background work): nunca propaga exceção
            // para o chamador do POST .../test-runs, que já retornou 202 antes deste ponto.
            _ = Task.Run(async () =>
            {
                state.Status = TestRunJobStatus.Running;
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    var summary = release.Engine.Equals("tcl", StringComparison.OrdinalIgnoreCase)
                        ? await RunTclTestAsync(release, draft, inputXml, expectedXml, xsdVersion, rulesById, cancellationToken)
                        : await RunXsltTestAsync(release, inputXml, expectedXml, xsdVersion, rulesById, cancellationToken);

                    using var scope = _scopeFactory.CreateScope();
                    var releaseStore = scope.ServiceProvider.GetRequiredService<IMappingReleaseStore>();
                    var updated = await releaseStore.ApplyTestRunResultAsync(releaseId, summary, cancellationToken);

                    state.RequiredGatesPassed = summary.RequiredGatesPassed;
                    state.Status = TestRunJobStatus.Completed;
                    _logger.LogInformation(
                        "Test-run concluído (release={ReleaseId}, requiredGatesPassed={RequiredGatesPassed}, divergências={DivergenceCount}, correlationId={CorrelationId}).",
                        releaseId, summary.RequiredGatesPassed, summary.Divergences.Count, correlationId);

                    if (updated == null)
                        _logger.LogWarning("Release {ReleaseId} sumiu entre o enqueue e a aplicação do resultado do test-run.", releaseId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Falha não tratada no job de test-run {JobId} (release={ReleaseId}, correlationId={CorrelationId}).", jobId, releaseId, correlationId);
                    state.Status = TestRunJobStatus.Failed;
                    state.Error = "Falha interna ao executar o Fiscal Test Lab";
                }
                finally
                {
                    stopwatch.Stop();
                    state.DurationMs = stopwatch.Elapsed.TotalMilliseconds;
                }
            }, CancellationToken.None);

            return jobId;
        }

        public Task<TestRunJobState?> GetStatusAsync(Guid jobId, CancellationToken cancellationToken)
            => Task.FromResult(Jobs.TryGetValue(jobId, out var state) ? state : null);

        private async Task<MappingTestRunSummary> RunXsltTestAsync(
            MappingReleaseDetail release,
            string inputXml,
            string expectedXml,
            string? xsdVersion,
            IReadOnlyDictionary<Guid, MappingDraftRuleDetail> rulesById,
            CancellationToken cancellationToken)
        {
            var xsltArtifact = release.Artifacts.FirstOrDefault(a => a.Kind == "xslt");
            if (xsltArtifact == null)
            {
                return new MappingTestRunSummary(0, 1, 0, false, false,
                    new[] { "Release sem artefato XSLT compilado." }, Array.Empty<MappingTestRunDivergence>());
            }

            string actualXml;
            try
            {
                // xsltArtifact.Content é gerado pelo transpilador interno (confiável) — só o
                // inputXml vem do corpo HTTP (fixture do Test Lab, não confiável). Parse XXE-safe
                // no lado do input, como já é padrão em MultipartUploadValidator/XsdValidationService.
                var applier = new XsltApplier();
                actualXml = applier.Apply(XDocument.Parse(xsltArtifact.Content), ParseXmlSafe(inputXml));
            }
            catch (Exception ex)
            {
                // Degrada graciosamente (dotnet-standards.md §Resiliência): XSLT malformado/input
                // inválido/inseguro não derruba o job — vira falha de teste reportada, não exceção.
                _logger.LogWarning(ex, "Falha ao aplicar o XSLT compilado no XML de entrada do test-run.");
                return new MappingTestRunSummary(0, 1, 0, false, false,
                    new[] { $"Falha ao aplicar o XSLT: {ex.Message}" }, Array.Empty<MappingTestRunDivergence>());
            }

            return await EvaluateAsync(actualXml, expectedXml, xsdVersion, rulesById, cancellationToken);
        }

        /// <summary>
        /// Runner determinístico de <c>engine=tcl</c> (issue #421) — interpreta diretamente as regras
        /// aceitas/editadas do draft (<see cref="TclRuleApplier"/>) em vez de reexecutar o texto
        /// <c>&lt;MAP&gt;&lt;LINE&gt;&lt;FIELD&gt;</c> compilado (esse dialeto é lossy para
        /// <c>conditional</c>, ver comentário em <see cref="TclRuleApplier"/>). Mesmo nome de raiz
        /// usado por <see cref="MappingCompileService"/> na compilação (<c>root{'{'}PackageId:N{'}'}</c>),
        /// mesmo subconjunto de regras (<see cref="MappingReleaseDetail.SourceRuleIds"/>, ordenado por
        /// <c>RuleId</c> — paridade com <see cref="MappingDraftRuleTranspiler.ToTcl"/>).
        /// </summary>
        private async Task<MappingTestRunSummary> RunTclTestAsync(
            MappingReleaseDetail release,
            MappingDraftDetail draft,
            string inputXml,
            string expectedXml,
            string? xsdVersion,
            IReadOnlyDictionary<Guid, MappingDraftRuleDetail> rulesById,
            CancellationToken cancellationToken)
        {
            var processableRules = release.SourceRuleIds
                .Select(id => rulesById.TryGetValue(id, out var r) ? r : null)
                .Where(r => r != null && r.Status is MappingDraftRuleStatus.Accepted or MappingDraftRuleStatus.Edited)
                .Select(r => ToEntity(r!))
                .OrderBy(r => r.RuleId)
                .ToList();

            var targetRootName = $"root{draft.PackageId:N}";

            string actualXml;
            try
            {
                // inputXml vem do corpo HTTP (fixture do Test Lab, não confiável) — mesma defesa XXE
                // usada no caminho XSLT.
                var output = TclRuleApplier.Apply(processableRules, targetRootName, ParseXmlSafe(inputXml));
                actualXml = output.ToString(SaveOptions.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao interpretar as regras TCL contra o XML de entrada do test-run.");
                return new MappingTestRunSummary(0, 1, 0, false, false,
                    new[] { $"Falha ao interpretar o mapeamento TCL: {ex.Message}" }, Array.Empty<MappingTestRunDivergence>());
            }

            return await EvaluateAsync(actualXml, expectedXml, xsdVersion, rulesById, cancellationToken);
        }

        /// <summary>Conversão mínima DTO→entidade — mesmos campos de <c>MappingCompileService.ToEntity</c> (não reaproveitado por ser <c>private</c> naquele serviço).</summary>
        private static MappingDraftRule ToEntity(MappingDraftRuleDetail detail) => new()
        {
            RuleId = detail.RuleId,
            DraftId = detail.DraftId,
            SourceRefs = detail.SourceRefs,
            TargetRefs = detail.TargetRefs,
            Operation = detail.Operation,
            ConditionsJson = detail.ConditionsJson,
            TransformationsJson = detail.TransformationsJson,
            Cardinality = detail.Cardinality,
            Evidence = detail.Evidence,
            Confidence = detail.Confidence,
            Status = detail.Status,
            OpenQuestions = detail.OpenQuestions,
            CreatedAt = detail.CreatedAt,
        };

        /// <summary>
        /// Núcleo compartilhado entre XSLT e TCL (issue #421): diff canônico + provenance por regra +
        /// validação XSD best-effort + cálculo de cobertura. As duas engines só diferem em como
        /// <c>actualXml</c> é produzido — a partir daqui o contrato de <see cref="MappingTestRunSummary"/>
        /// é idêntico.
        /// </summary>
        private async Task<MappingTestRunSummary> EvaluateAsync(
            string actualXml,
            string expectedXml,
            string? xsdVersion,
            IReadOnlyDictionary<Guid, MappingDraftRuleDetail> rulesById,
            CancellationToken cancellationToken)
        {
            // expectedXml também vem do corpo HTTP (gabarito ad-hoc do Test Lab) — mesma defesa XXE.
            // Sanitiza aqui (parse seguro + reserialização) antes de repassar pro CanonicalDiffer, que
            // faz o próprio XDocument.Parse internamente sem hardening (biblioteca compartilhada
            // XslSynth.Core, usada também fora do contexto HTTP — o hardening fica na fronteira aqui).
            string safeExpectedXml;
            try
            {
                safeExpectedXml = ParseXmlSafe(expectedXml).ToString(SaveOptions.DisableFormatting);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "XML esperado (gabarito) do test-run rejeitado por não ser seguro/bem formado.");
                return new MappingTestRunSummary(0, 1, 0, false, false,
                    new[] { $"XML esperado inválido ou inseguro: {ex.Message}" }, Array.Empty<MappingTestRunDivergence>());
            }

            // Diff canônico node-a-node — cada divergência vira provenance rastreada até a regra.
            // O atributo lp:ruleId (embutido pelo transpilador para RASTREABILIDADE, spec §11) NUNCA
            // deve poluir o diff contra o gabarito real — o gabarito não conhece esse atributo. Ele é
            // removido só para efeito de comparação; a provenance em si já é resolvida por nome de
            // elemento (ToDivergenceWithProvenance), não depende do atributo sobreviver ao diff.
            var differ = new CanonicalDiffer();
            var rawDiffs = differ.Diff(safeExpectedXml, StripProvenanceAttributes(actualXml));
            var divergences = rawDiffs.Select(d => ToDivergenceWithProvenance(d, rulesById)).ToList();

            // Validação XSD é best-effort: degrada (não derruba o job) se o serviço não conseguir
            // detectar/validar o tipo de documento — o diff canônico continua sendo o gate principal.
            bool xsdValid;
            var xsdErrors = new List<string>();
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var xsdValidationService = scope.ServiceProvider.GetRequiredService<XsdValidationService>();
                var xsdResult = await xsdValidationService.ValidateXmlAgainstXsdAsync(actualXml, xsdVersion, null);
                if (string.IsNullOrEmpty(xsdResult.DocumentType))
                {
                    // Tipo de documento fiscal não detectado (fixture fora dos 4 tipos suportados pelo
                    // XsdValidationService — NFe/CTe/NFCom/MDFe): validação XSD é informacional aqui,
                    // não bloqueia o gate — o diff canônico continua sendo o critério principal.
                    xsdValid = true;
                    xsdErrors.AddRange(xsdResult.Errors.Select(e => e.Message));
                }
                else
                {
                    xsdValid = xsdResult.IsValid;
                    xsdErrors.AddRange(xsdResult.Errors.Select(e => e.Message));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Validação XSD indisponível no test-run — seguindo só com o diff canônico.");
                xsdValid = true; // Não bloqueia o gate por indisponibilidade de infraestrutura de validação.
                xsdErrors.Add("Validação XSD indisponível — não considerada no gate.");
            }

            var passed = divergences.Count == 0 && xsdValid;
            var coverage = rulesById.Count == 0 ? 0 : (double)rulesById.Values.Count(r => r.Status is MappingDraftRuleStatus.Accepted or MappingDraftRuleStatus.Edited) / rulesById.Count * 100;

            return new MappingTestRunSummary(
                Passed: passed ? 1 : 0,
                Failed: passed ? 0 : 1,
                CoveragePercent: coverage,
                RequiredGatesPassed: passed,
                XsdValid: xsdValid,
                XsdErrors: xsdErrors,
                Divergences: divergences,
                // Issue #380: guarda o XML real produzido e o gabarito sanitizado — combustível do
                // diff release×release (CanonicalDiffer reaplicado entre releases, não só contra o gabarito).
                ActualXml: actualXml,
                ExpectedXml: safeExpectedXml);
        }

        /// <summary>
        /// Provenance por nó (spec §11): XML de saída → <c>MappingDraftRule.RuleId</c> (via o último
        /// segmento do XPath, que corresponde ao nome do elemento emitido pelo transpilador com o
        /// mesmo nome de <c>TargetRefs</c>) → evidência da regra (Slice 3) → campo/posição de origem.
        /// </summary>
        private static MappingTestRunDivergence ToDivergenceWithProvenance(NodeDiff diff, IReadOnlyDictionary<Guid, MappingDraftRuleDetail> rulesById)
        {
            var elementName = LastSegment(diff.XPath);
            var rule = rulesById.Values.FirstOrDefault(r =>
                r.Status is MappingDraftRuleStatus.Accepted or MappingDraftRuleStatus.Edited &&
                r.TargetRefs.Count > 0 &&
                LastSegment(r.TargetRefs[0]) == elementName);

            return new MappingTestRunDivergence(
                diff.Kind, diff.XPath, diff.Expected, diff.Actual,
                rule?.RuleId, rule?.SourceRefs, rule?.Evidence);
        }

        /// <summary>Remove o atributo <c>lp:ruleId</c> (namespace <see cref="MappingDraftRuleTranspiler.ProvenanceNamespace"/>) antes do diff — ver comentário acima.</summary>
        private static string StripProvenanceAttributes(string xml)
        {
            var doc = XDocument.Parse(xml);
            XNamespace lp = MappingDraftRuleTranspiler.ProvenanceNamespace;
            foreach (var element in doc.Descendants())
                element.Attribute(lp + "ruleId")?.Remove();

            return doc.ToString(SaveOptions.None);
        }

        /// <summary>
        /// Defesa XXE clássica (mesmo padrão de <c>MultipartUploadValidator.ValidateXmlIsXxeSafe</c>):
        /// <c>XmlResolver=null</c> + <c>DtdProcessing=Prohibit</c>. XML declarando DOCTYPE/entidade
        /// externa faz o parser lançar — tratado pelo chamador como rejeição do fixture, nunca deixa
        /// a exceção subir com conteúdo do documento. Usado apenas para XML vindo do corpo HTTP
        /// (inputXml/expectedXml do Test Lab); artefatos gerados internamente pelo transpilador
        /// continuam usando XDocument.Parse direto — são confiáveis.
        /// </summary>
        private static XDocument ParseXmlSafe(string xml)
        {
            var settings = new XmlReaderSettings
            {
                XmlResolver = null,
                DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersFromEntities = 1024,
            };

            using var stringReader = new StringReader(xml);
            using var reader = XmlReader.Create(stringReader, settings);
            return XDocument.Load(reader);
        }

        private static string LastSegment(string reference)
        {
            var withoutAttr = reference.Split('@').Last();
            var withoutIndex = withoutAttr.Split('[').First();
            var trimmed = withoutIndex.TrimEnd('/');
            var idx = trimmed.LastIndexOfAny(new[] { '/', ':' });
            return idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
        }

    }
}
