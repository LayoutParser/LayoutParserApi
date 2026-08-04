using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Responses;
using LayoutParserApi.Models.Configuration;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Parsing.Interfaces;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Learning;
using LayoutParserApi.Services.Transformation.LowCode;
using LayoutParserApi.Services.Database;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ParseController : ControllerBase
    {
        private readonly ILayoutParserService _parserService;
        private readonly ILogger<ParseController> _logger;
        private readonly ILayoutDetector _layoutDetector;
        private readonly FileStorageService _fileStorage;
        private readonly LayoutLearningService _learningService;
        private readonly IConfiguration _configuration;
        private readonly LowCodeAutoTransformationService _lowCodeAuto;
        private readonly LowCodeRunnerOptions _lowCodeOpt;

        public ParseController(
            ILayoutParserService parserService,
            ILogger<ParseController> logger,
            ILayoutDetector layoutDetector,
            FileStorageService fileStorage,
            LayoutLearningService learningService,
            IConfiguration configuration,
            LowCodeAutoTransformationService lowCodeAuto,
            IOptions<LowCodeRunnerOptions> lowCodeOptions)
        {
            _parserService = parserService;
            _logger = logger;
            _layoutDetector = layoutDetector;
            _fileStorage = fileStorage;
            _learningService = learningService;
            _configuration = configuration;
            _lowCodeAuto = lowCodeAuto;
            _lowCodeOpt = lowCodeOptions.Value;
        }

        [ServiceFilter(typeof(AuditActionFilter))]
        [HttpPost("upload")]
        public async Task<IActionResult> Upload(IFormFile layoutFile, IFormFile txtFile, [FromForm] string layoutName = null)
        {
            if (layoutFile == null || txtFile == null)
                return BadRequest("Layout XML e arquivo são obrigatórios.");

            if (Path.GetExtension(layoutFile.FileName).ToLower() != ".xml")
                return BadRequest("O arquivo de layout deve ser XML.");

            try
            {
                // Detectar tipo de arquivo pela extensão e conteúdo
                var fileExtension = Path.GetExtension(txtFile.FileName).ToLower();
                var isXmlFile = fileExtension == ".xml";

                // Ler conteúdo do arquivo para detecção de tipo
                using var txtStreamForDetection = txtFile.OpenReadStream();
                using var reader = new StreamReader(txtStreamForDetection, leaveOpen: true);
                var sample = await reader.ReadToEndAsync();
                var detectedType = _layoutDetector.DetectType(sample);

                // ✅ Overrides por contexto (extensão / layout selecionado)
                // Quando o documento MQSeries tem linha com 601 chars, o detector por conteúdo pode falhar.
                // Nesses casos, a extensão e/ou o layout selecionado são a fonte de verdade.
                if (fileExtension == ".mq_series" ||
                    (!string.IsNullOrWhiteSpace(layoutName) && layoutName.Contains("MQ", StringComparison.OrdinalIgnoreCase)))
                {
                    detectedType = "mqseries";
                }
                else if (fileExtension == ".idoc")
                {
                    detectedType = "idoc";
                }

                // Se for arquivo XML, retornar indicando que deve ser processado no front-end
                if (isXmlFile || detectedType == "xml")
                {
                    _logger.LogInformation("Arquivo XML detectado, deve ser processado no front-end");
                    return Ok(new
                    {
                        success = true,
                        fileType = "xml",
                        detectedType = "xml",
                        message = "Arquivo XML detectado. Processe no front-end com xmltools.js",
                        content = sample // Retornar conteúdo para processamento no front-end
                    });
                }

                // Salvar arquivo para aprendizado de máquina ANTES de processar
                if (!string.IsNullOrEmpty(layoutName))                
                    await SaveFileForLearningAsync(layoutName, txtFile, detectedType);
                

                // Processar arquivo
                using var layoutStream = layoutFile.OpenReadStream();
                using var txtStream = txtFile.OpenReadStream();

                var result = await _parserService.ParseAsync(layoutStream, txtStream);

                // ✅ Gate de falha de parse: ParseAsync captura a exceção internamente e devolve
                // Success=false / Layout=null, com a causa real em ErrorMessage. Sem este gate,
                // ReestruturarLayout(null) devolve null em silêncio e o NullReference só estoura
                // adiante (ao ler LayoutGuid), virando um 500 "Object reference not set..." que
                // apaga a mensagem que diria a causa real. Retornamos 422 para o front distinguir
                // "layout não parseável / erro de parse" de "ainda não processei nada".
                if (!result.Success || result.Layout == null)
                {
                    var parseErrorMessage = !string.IsNullOrWhiteSpace(result.ErrorMessage)
                        ? result.ErrorMessage
                        : "Não foi possível parsear o documento com o layout informado.";

                    _logger.LogError("Falha no parse do documento. Layout={LayoutFile}, Arquivo={DocumentFile}, Tipo={DetectedType}, Motivo={ErrorMessage}",
                        layoutFile.FileName, txtFile.FileName, detectedType, parseErrorMessage);

                    return UnprocessableEntity(new
                    {
                        success = false,
                        detectedType,
                        message = parseErrorMessage
                    });
                }

                var layoutReestruturado = _parserService.ReestruturarLayout(result.Layout);
                var layoutReordenado = _parserService.ReordenarSequences(layoutReestruturado);

                var flattenedLayout = new Layout
                {
                    LayoutGuid = layoutReordenado.LayoutGuid,
                    LayoutType = layoutReordenado.LayoutType,
                    Name = layoutReordenado.Name,
                    Description = layoutReordenado.Description,
                    LimitOfCaracters = layoutReordenado.LimitOfCaracters,
                    // ✅ Discriminador canônico de formato físico (ADR-001). Achatar o layout não pode
                    // apagar o campo: quem consumir este objeto adiante precisa saber se é IDOC
                    // (registro por linha) ou MQSeries (stream contínuo) — LayoutType não distingue.
                    WithBreakLines = layoutReordenado.WithBreakLines,
                    Elements = layoutReordenado.Elements
                };

                // ✅ SEMPRE processar documento mesmo com erros de validação
                // (result.Success sempre será true agora, mas pode ter ValidationErrors)
                
                var documentStructure = _parserService.BuildDocumentStructure(result);

                // Calcular validações e posições das linhas para o front-end
                // ✅ Resolução mesclada: LimitOfCaracters > 0 → allowlist manual → null (sem validação)
                List<LineValidationInfo>? lineValidations = null;
                var expectedLineLength = LineLengthResolver.Resolve(flattenedLayout);
                
                if (expectedLineLength.HasValue)
                    lineValidations = _parserService.CalculateLineValidations(flattenedLayout, expectedLineLength.Value);

                // ✅ Transformação low-code: entrega SÍNCRONA no response quando possível, com teto de
                // tempo (LowCode:SyncDeliveryTimeoutSeconds); se estourar, cai para processamento em
                // background (o trabalho já em andamento NÃO é perdido — a persistência em disco
                // acontece dentro da mesma chamada, independente de o controller continuar esperando).
                // Decisão de arquitetura (Aria, 2026-07-28): o parse do documento é a resposta
                // principal e NUNCA pode ser bloqueado além do teto nem falhar por causa deste pathway.
                object? transformations = null;
                var transformationsStatus = "not_applicable";
                try
                {
                    if (!string.IsNullOrWhiteSpace(flattenedLayout.LayoutGuid) &&
                        !string.IsNullOrWhiteSpace(result.RawText) &&
                        detectedType == "mqseries")
                    {
                        var syncTimeoutSeconds = _lowCodeOpt.SyncDeliveryTimeoutSeconds > 0 ? _lowCodeOpt.SyncDeliveryTimeoutSeconds : 6;

                        var transformTask = _lowCodeAuto.RunAsync(
                            flattenedLayout.LayoutGuid,
                            flattenedLayout.Name,
                            result.RawText,
                            detectedType,
                            txtFile.FileName);

                        var winner = await Task.WhenAny(transformTask, Task.Delay(TimeSpan.FromSeconds(syncTimeoutSeconds)));

                        if (winner == transformTask)
                        {
                            // Já concluiu dentro do teto — observamos o resultado (RunAsync já trata
                            // falha de candidato individual internamente, não deve lançar por isso).
                            var autoResult = await transformTask;
                            transformationsStatus = autoResult.Applicable ? "completed" : "not_applicable";
                            if (autoResult.Applicable)
                                transformations = autoResult.Candidates;
                        }
                        else
                        {
                            // Estourou o teto síncrono: resposta segue sem esperar mais, processamento
                            // continua em background (persistência em disco já ocorre dentro de
                            // RunAsync). Só observamos exceção aqui pra não gerar unobserved task.
                            transformationsStatus = "processing";
                            _ = transformTask.ContinueWith(t =>
                            {
                                if (t.IsFaulted)
                                    _logger.LogError(t.Exception, "Falha no processamento low-code em background (após estouro do teto síncrono de {SyncTimeoutSeconds}s)", syncTimeoutSeconds);
                            }, TaskScheduler.Default);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // ✅ Falha estrutural do pathway de transformação (ex.: banco fora do ar ao buscar
                    // mapeadores) NUNCA pode derrubar o parse principal — o parse TXT->estrutura já
                    // sucedeu e é a resposta que importa.
                    _logger.LogWarning(ex, "Falha ao processar transformações low-code (parse principal não afetado)");
                    transformationsStatus = "error";
                }

                return Ok(new
                {
                    success = true,
                    detectedType,
                    layout = flattenedLayout,
                    fields = result.ParsedFields,
                    text = result.RawText,
                    summary = result.Summary,
                    documentStructure = documentStructure,
                    lineValidations = lineValidations, // Validações e posições calculadas (apenas para layouts configurados)
                    validationErrors = result.ValidationErrors, // ✅ Erros de validação de tamanho de linha
                    validationWarning = !string.IsNullOrEmpty(result.ErrorMessage) ? result.ErrorMessage : null, // ✅ Aviso se houver erros
                    transformations, // array de candidatos low-code (mapper/target/xml/sucesso-ou-erro) quando concluído a tempo
                    transformationsStatus // "not_applicable" | "completed" | "processing" | "error"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante o parsing do XML");
                return StatusCode(500, $"Erro interno: {ex.Message}");
            }
        }

        /// <summary>
        /// Salva arquivo na pasta do layout para aprendizado de máquina
        /// </summary>
        private async Task SaveFileForLearningAsync(string layoutName, IFormFile txtFile, string detectedType)
        {
            try
            {
                _logger.LogInformation("Salvando arquivo para aprendizado: Layout={LayoutName}, Tipo={Type}", layoutName, detectedType);

                // Criar diretório baseado no nome do layout
                var basePath = _configuration["TransformationPipeline:ExamplesPath"] ?? @"C:\inetpub\wwwroot\layoutparser\Examples";
                var layoutDirectory = Path.Combine(basePath, layoutName);

                if (!Directory.Exists(layoutDirectory))
                {
                    Directory.CreateDirectory(layoutDirectory);
                    _logger.LogInformation("Diretório criado: {Path}", layoutDirectory);
                }

                // Salvar arquivo com timestamp para evitar sobrescrita
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var fileExtension = Path.GetExtension(txtFile.FileName);
                var fileName = $"{timestamp}_{txtFile.FileName}";
                var filePath = Path.Combine(layoutDirectory, fileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await txtFile.CopyToAsync(stream);
                }

                _logger.LogInformation("Arquivo salvo para aprendizado: {Path}", filePath);

                // Executar aprendizado de máquina em background (não bloquear resposta)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Determinar tipo de arquivo para aprendizado
                        var fileType = detectedType?.ToLower() switch
                        {
                            "xml" => "xml",
                            "idoc" => "txt",
                            "mqseries" => "txt",
                            _ => "txt"
                        };

                        // Aprender estrutura do arquivo
                        var learningResult = await _learningService.LearnFromFileAsync(filePath, fileType);
                        
                        if (learningResult.Success && learningResult.LearnedModel != null)
                        {
                            // Salvar modelo aprendido
                            learningResult.LearnedModel.FilePath = filePath;
                            await _fileStorage.SaveLearnedModelAsync(layoutDirectory, learningResult.LearnedModel);
                            
                            _logger.LogInformation("Aprendizado concluído para {LayoutName}: {Fields} campos detectados", layoutName, learningResult.LearnedModel.TotalFields);
                        }
                        else
                            _logger.LogWarning("Aprendizado falhou para {LayoutName}: {Message}", layoutName, learningResult.Message);                        
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Erro durante aprendizado de máquina para {LayoutName}", layoutName);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao salvar arquivo para aprendizado");
                // Não falhar o processamento principal se houver erro no aprendizado
            }
        }
    }
}