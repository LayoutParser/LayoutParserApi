using LayoutParserApi.Models.XmlAnalysis;

namespace LayoutParserApi.Services.Validation
{
    /// <summary>
    /// Classificador determinístico de erros de validação XSD: separa "defeito real" de
    /// "esperado" (conhecido-e-aceito) - ex.: ausência de assinatura digital, que o XSD
    /// SEMPRE acusa em documento ainda não assinado (não é defeito).
    /// Requisito de design obrigatório (não nice-to-have): deve rodar ANTES de qualquer
    /// explicação via IA no loop de diagnóstico (Grupo 3) - senão 100% dos documentos geram
    /// o mesmo ruído repetido e qualquer diagnóstico real fica enterrado.
    /// Item 3.2 do dispatch de IA (docs/architecture/ai-roadmap-dispatch.md, 2026-07-21) e
    /// ia-fiscal-diagnosis-vision.md §3.3.
    /// </summary>
    public class XsdErrorClassifierService
    {
        private readonly ILogger<XsdErrorClassifierService> _logger;

        // Padrões conhecidos de erro "esperado" (não é defeito real). Lista extensível -
        // hoje cobre só ausência de assinatura digital, o único caso confirmado até 2026-07-21.
        //
        // ⚠️ NOTA: nenhum XSD ou log real com esse erro estava disponível nesta sessão para
        // conferir o texto exato da mensagem de validação do .NET (XmlSchemaValidationException)
        // para "Signature" ausente. Os padrões abaixo são deliberadamente amplos (baseados no
        // nome público do elemento/namespace XMLDSig exigido pelo XSD da NF-e) - conferir contra
        // uma mensagem real (via @lp-qa) antes de confiar cegamente em produção.
        private static readonly string[] _padroesErroConhecidoEAceito =
        {
            "signature",
            "assinatura",
            "xmldsig",
        };

        public XsdErrorClassifierService(ILogger<XsdErrorClassifierService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Classifica uma lista de erros XSD, separando defeitos reais de itens
        /// conhecido-e-aceitos. Não modifica a lista de entrada.
        /// </summary>
        public XsdErrorClassificationResult Classify(IEnumerable<XsdValidationError> errors)
        {
            var result = new XsdErrorClassificationResult();

            if (errors == null)
                return result;

            foreach (var error in errors)
            {
                if (error == null)
                    continue;

                try
                {
                    if (IsKnownAcceptedError(error.Message))
                    {
                        result.AcceptedIssues.Add(error);
                        _logger.LogInformation("Erro XSD classificado como esperado (nao e defeito real): {Message}", error.Message);
                    }
                    else
                        result.RealErrors.Add(error);
                }
                catch (Exception ex)
                {
                    // Degrada graciosamente: em caso de falha na classificação, trata como erro
                    // real (opção mais segura - nunca esconder um defeito genuíno por causa de
                    // uma exceção neste filtro).
                    _logger.LogError(ex, "Erro ao classificar erro XSD - tratando como defeito real por seguranca");
                    result.RealErrors.Add(error);
                }
            }

            return result;
        }

        /// <summary>
        /// Reconhece o padrão de erro conhecido-e-aceito (hoje: assinatura digital ausente).
        /// Pattern-match determinístico na mensagem do XSD - não usa IA.
        /// </summary>
        public bool IsKnownAcceptedError(string xsdErrorMessage)
        {
            if (string.IsNullOrWhiteSpace(xsdErrorMessage))
                return false;

            foreach (var padrao in _padroesErroConhecidoEAceito)
                if (xsdErrorMessage.Contains(padrao, StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }
    }
}
