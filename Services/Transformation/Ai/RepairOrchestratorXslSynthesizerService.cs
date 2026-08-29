using System.Xml.Linq;

using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.Extensions.Options;

using XslSynth.Core;
using XslSynth.Synthesis;

namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>
    /// Implementação real de <see cref="IXslSynthesizerService"/>: instancia o
    /// <see cref="RepairOrchestrator"/> de <c>ai/XslSynth.Core</c> (in-process — ver design doc
    /// no header de <see cref="IXslSynthesizerService"/>) e delega o loop
    /// gerar → validar (diff canônico + XSD) → corrigir, usando o Ollama local.
    ///
    /// <para><b>Limitação conhecida (documentada, não corrigida nesta iteração):</b> a resolução
    /// do caminho do XSD reaproveita a MESMA convenção de <c>XsdValidationService.FindXsdFile</c>
    /// (pasta <c>XsdValidation:BasePath/{versão}</c>, maior <c>*.xsd</c>), mas duplicada aqui —
    /// aquele método é privado e não há um método público equivalente. Se a convenção mudar lá,
    /// precisa mudar aqui também.</para>
    /// </summary>
    public class RepairOrchestratorXslSynthesizerService : IXslSynthesizerService
    {
        private readonly ILogger<RepairOrchestratorXslSynthesizerService> _logger;
        private readonly ICachedMapperService _mapperService;
        private readonly XmlDocumentTypeDetector _documentTypeDetector;
        private readonly OllamaOptions _ollamaOptions;
        private readonly string _xsdBasePath;
        private readonly string _xslBasePath;
        private readonly RepairOrchestrator _orchestrator = new();
        private readonly MapperExtractor _sampleExtractor = new();
        private readonly RealMapperParser _realParser = new();

        public RepairOrchestratorXslSynthesizerService(
            ILogger<RepairOrchestratorXslSynthesizerService> logger,
            ICachedMapperService mapperService,
            XmlDocumentTypeDetector documentTypeDetector,
            IOptions<OllamaOptions> ollamaOptions,
            IConfiguration configuration)
        {
            _logger = logger;
            _mapperService = mapperService;
            _documentTypeDetector = documentTypeDetector;
            _ollamaOptions = ollamaOptions.Value;
            _xsdBasePath = configuration["XsdValidation:BasePath"] ?? @"C:\inetpub\wwwroot\layoutparser\xsd";
            // Mesma convenção de TransformationPipelineService/AutoTransformationGeneratorService —
            // {mapperName}_{layoutName}.xsl (issue #55) — pra persistir o XSLT sintetizado no lugar
            // que o pathway tcl-xsl já sabe ler.
            _xslBasePath = configuration["TransformationPipeline:XslPath"] ?? @"C:\inetpub\wwwroot\layoutparser\XSL";
        }

        public async Task<XslSynthesisResult> SynthesizeAsync(
            string mapperGuid,
            string inputXml,
            string groundTruthXml,
            int maxIterations,
            string? layoutName,
            CancellationToken cancellationToken)
        {
            try
            {
                var mapper = await ResolveMapperAsync(mapperGuid, cancellationToken);
                if (mapper is null)
                {
                    return Failed($"Mapeador '{mapperGuid}' não encontrado (cache/banco) ou sem conteúdo descriptografado.");
                }

                var mapperVo = ParseMapperVo(mapper.DecryptedContent, mapperGuid);
                if (mapperVo is null)
                {
                    return Failed($"MapeadorVO do mapper '{mapperGuid}' não é XML bem-formado.");
                }

                XDocument input;
                try
                {
                    input = XDocument.Parse(inputXml);
                }
                catch (Exception ex)
                {
                    // O RepairOrchestrator aplica XSLT sobre XML — TXT posicional cru não serve
                    // aqui. É responsabilidade do chamador ter passado pelo parsing/low-code antes.
                    _logger.LogWarning(ex, "Entrada não é XML bem-formado — RepairOrchestrator exige o low-code intermediário, não o TXT cru (mapperGuid={MapperGuid})", mapperGuid);
                    return Failed("Entrada não é XML válido — RepairOrchestrator exige o documento já convertido (low-code), não o TXT posicional cru.");
                }

                var xsdPath = ResolveXsdPath(groundTruthXml);
                if (xsdPath is null)
                {
                    _logger.LogWarning("XSD não resolvido para o gabarito (mapperGuid={MapperGuid}) — validação XSD do loop sempre reportará inválido", mapperGuid);
                }

                var synthesizer = CreateOllamaSynthesizer();

                var report = await _orchestrator.RunAsync(
                    mapperVo,
                    input,
                    groundTruthXml,
                    xsdPath ?? string.Empty,
                    synthesizer,
                    log => _logger.LogInformation("[RepairOrchestrator] {Message}", log),
                    maxIterations,
                    cancellationToken);

                if (report.Converged && !string.IsNullOrWhiteSpace(layoutName))
                    TryPersistXslt(mapper.Name, layoutName!, report.FinalXslt);

                return new XslSynthesisResult
                {
                    Success = true,
                    Converged = report.Converged,
                    GeneratedXslt = report.FinalXslt,
                    FinalOutputXml = report.FinalOutput,
                    IterationsUsed = report.Iterations,
                    XsdValid = report.FinalXsd.IsValid,
                    ValidationErrors = report.FinalDiffs.Select(d => d.ToString())
                        .Concat(report.FinalXsd.Errors)
                        .ToList()
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Resiliência (dotnet-standards.md): Ollama/RepairOrchestrator podem falhar —
                // nunca propaga exceção não tratada pro chamador (fire-and-forget).
                _logger.LogError(ex, "Falha não tratada na síntese de XSLT via RepairOrchestrator (mapperGuid={MapperGuid})", mapperGuid);
                return Failed($"Falha interna na síntese de XSLT: {ex.Message}");
            }
        }

        /// <summary>
        /// Resolve o mapper real via <see cref="ICachedMapperService"/> — não há método de busca
        /// direta por MapperGuid, filtra sobre <c>GetAllMappersAsync</c> (mesmo padrão já usado
        /// noutros pontos do domínio).
        /// </summary>
        private async Task<LayoutParserApi.Models.Entities.Mapper?> ResolveMapperAsync(string mapperGuid, CancellationToken cancellationToken)
        {
            var allMappers = await _mapperService.GetAllMappersAsync();
            var mapper = allMappers.FirstOrDefault(m =>
                string.Equals(m.MapperGuid, mapperGuid, StringComparison.OrdinalIgnoreCase));

            return mapper is null || string.IsNullOrWhiteSpace(mapper.DecryptedContent) ? null : mapper;
        }

        /// <summary>Parseia o conteúdo descriptografado com <see cref="RealMapperParser"/> (formato
        /// real Sysmiddle); cai para <see cref="MapperExtractor"/> (formato MVP/sample) se o
        /// parser real não reconhecer a estrutura.</summary>
        private XslSynth.Model.MapperVo? ParseMapperVo(string decryptedContent, string mapperGuid)
        {
            try
            {
                var doc = XDocument.Parse(decryptedContent);
                try
                {
                    return _realParser.Parse(doc);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "RealMapperParser não reconheceu o MapeadorVO — tentando MapperExtractor (formato sample)");
                    return _sampleExtractor.Extract(doc);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MapeadorVO do mapper {MapperGuid} não é XML bem-formado", mapperGuid);
                return null;
            }
        }

        /// <summary>Best-effort: falha na gravação nunca derruba a síntese (o candidato já foi
        /// devolvido convergido ao chamador via <see cref="XslSynthesisResult"/>).</summary>
        private void TryPersistXslt(string? mapperName, string layoutName, string xslt)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(mapperName))
                {
                    _logger.LogWarning("Não foi possível persistir o XSLT sintetizado — mapper sem Name (layout={LayoutName})", layoutName);
                    return;
                }

                Directory.CreateDirectory(_xslBasePath);
                var path = Path.Combine(_xslBasePath, $"{mapperName}_{layoutName}.xsl");
                File.WriteAllText(path, xslt);
                _logger.LogInformation("XSLT sintetizado via RepairOrchestrator persistido em {Path}", path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao persistir o XSLT sintetizado (layout={LayoutName})", layoutName);
            }
        }

        /// <summary>Mesma convenção de <c>XsdValidationService.FindXsdFile</c> — ver limitação no
        /// header da classe.</summary>
        private string? ResolveXsdPath(string groundTruthXml)
        {
            try
            {
                var docType = _documentTypeDetector.DetectDocumentType(groundTruthXml);
                if (string.IsNullOrEmpty(docType?.XsdVersion))
                    return null;

                var versionPath = Path.Combine(_xsdBasePath, docType.XsdVersion);
                if (!Directory.Exists(versionPath))
                    return null;

                return Directory.GetFiles(versionPath, "*.xsd", SearchOption.AllDirectories)
                    .OrderByDescending(f => new FileInfo(f).Length)
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Falha ao resolver caminho do XSD para o gabarito");
                return null;
            }
        }

        /// <summary>
        /// <see cref="OllamaXslSynthesizer"/> lê configuração via variável de ambiente
        /// (<c>OLLAMA_URL</c>/<c>OLLAMA_MODEL</c>), não via <c>IOptions</c> — é o mesmo componente
        /// usado pelo CLI standalone, sem DI. Propagamos a config já validada da API (seção
        /// <c>Ollama</c> de <c>appsettings.json</c>) só se a env var ainda não tiver sido setada
        /// externamente, pra não sobrescrever uma configuração de processo intencional.
        /// </summary>
        private OllamaXslSynthesizer CreateOllamaSynthesizer()
        {
            if (Environment.GetEnvironmentVariable("OLLAMA_URL") is null && !string.IsNullOrWhiteSpace(_ollamaOptions.Url))
                Environment.SetEnvironmentVariable("OLLAMA_URL", _ollamaOptions.Url);

            if (Environment.GetEnvironmentVariable("OLLAMA_MODEL") is null && !string.IsNullOrWhiteSpace(_ollamaOptions.Model))
                Environment.SetEnvironmentVariable("OLLAMA_MODEL", _ollamaOptions.Model);

            return new OllamaXslSynthesizer(message => _logger.LogDebug("[Ollama] {Message}", message));
        }

        private static XslSynthesisResult Failed(string error) => new()
        {
            Success = false,
            Converged = false,
            Error = error
        };
    }
}
