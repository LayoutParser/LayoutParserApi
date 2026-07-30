namespace LayoutParserApi.Models.Transformation
{
    /// <summary>
    /// Resultado de UMA transformação low-code candidata, produzido quando o pathway LowCode-auto
    /// (<see cref="LayoutParserApi.Services.Transformation.LowCode.LowCodeAutoTransformationService"/>)
    /// encontra N&gt;1 mapeadores genuinamente plausíveis (MapperGuid distintos) para o mesmo layoutGuid.
    /// Quando N==1 (caso comum), este tipo não é usado — o comportamento permanece o de sempre.
    /// </summary>
    public class LowCodeCandidateResult
    {
        /// <summary>Identificador do mapeador (tbMapper.MapperGuid) usado nesta transformação.</summary>
        public string MapperGuid { get; set; } = "";

        /// <summary>Nome do mapeador (tbMapper.Name), quando disponível.</summary>
        public string? MapperName { get; set; }

        /// <summary>TargetLayoutGuid do mapeador (prioriza o valor extraído do XML descriptografado).</summary>
        public string? TargetLayoutGuid { get; set; }

        /// <summary>PackageGuid do mapeador, para rastreabilidade.</summary>
        public string? PackageGuid { get; set; }

        /// <summary>
        /// Indicador de validade desta transformação. Hoje reflete apenas se o runner low-code
        /// executou sem exceção — não há validação XSD cabeada neste pathway (ver AutomatedTransformationTestService,
        /// que é usado em outro loop). Não inventamos validação nova aqui.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>XML de saída da transformação, quando bem-sucedida.</summary>
        public string? OutputXml { get; set; }

        /// <summary>Mensagem de erro, quando a transformação deste candidato específico falhou.</summary>
        public string? ErrorMessage { get; set; }
    }
}
