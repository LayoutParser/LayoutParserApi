using LayoutParserApi.Models.Transformation;
using LayoutParserApi.Services.Transformation.LowCode;

namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>
    /// Decisão pura de "disparar ou não o pathway IA" (Issue #40) — extraída do controller para ser
    /// testável sem precisar instanciar <c>TransformationExecutionController</c> (que depende de
    /// vários serviços concretos pesados). Não faz nenhuma chamada externa: só decide, a partir dos
    /// candidatos já calculados, se há gabarito sysmiddle disponível (§2.1/§3.2 do desenho) e monta
    /// os parâmetros que <see cref="IAiTransformationCandidateService.EnqueueAsync"/> precisa.
    /// </summary>
    public static class AiCandidateDispatchPlan
    {
        /// <summary>
        /// Retorna null quando o pathway IA não deve ser disparado: entrada é XML (sysmiddle só roda
        /// sobre TXT), não há candidato sysmiddle bem-sucedido (sem gabarito), ou não foi possível
        /// resolver um LayoutGuid válido para compor o ticket.
        /// </summary>
        public static AiCandidateDispatchParams? TryBuild(
            string? requestLayoutGuid,
            Guid catalogLayoutGuid,
            string inputContent,
            bool isXmlInput,
            IReadOnlyList<TransformationCandidate> candidates)
        {
            if (isXmlInput)
                return null;

            var groundTruth = candidates.FirstOrDefault(c => c.Pathway == "sysmiddle" && !string.IsNullOrEmpty(c.TransformedXml));
            if (groundTruth == null)
                return null;

            var resolvedLayoutGuidText = LowCodeLayoutGuidResolver.Resolve(requestLayoutGuid, catalogLayoutGuid);
            if (resolvedLayoutGuidText == null || !Guid.TryParse(resolvedLayoutGuidText, out var resolvedLayoutGuid))
                return null;

            var ticket = LowCodeTransformationStore.BuildTicketFromContent(inputContent, resolvedLayoutGuidText);
            if (ticket == null)
                return null;

            var mapperGuid = groundTruth.CandidateId.StartsWith("sysmiddle-", StringComparison.Ordinal)
                ? groundTruth.CandidateId["sysmiddle-".Length..]
                : groundTruth.CandidateId;

            return new AiCandidateDispatchParams(ticket, resolvedLayoutGuid, mapperGuid, groundTruth.TransformedXml);
        }
    }

    /// <summary>Parâmetros já resolvidos para chamar <see cref="IAiTransformationCandidateService.EnqueueAsync"/>.</summary>
    public record AiCandidateDispatchParams(string Ticket, Guid LayoutGuid, string MapperGuid, string GroundTruthXml);
}
