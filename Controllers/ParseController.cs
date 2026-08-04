using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Enums;
using LayoutParserApi.Models.Parsing;
using LayoutParserApi.Models.Responses;
using LayoutParserApi.Models.Configuration;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Parsing.Interfaces;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Learning;
using LayoutParserApi.Services.Logging;
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

                var isXmlInput = isXmlFile || detectedType == "xml";

                // Se for arquivo XML, retornar indicando que deve ser processado no front-end
                if (isXmlInput)
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

                // ✅ Documento sem conteúdo: irrecuperável, não há o que renderizar (spec §2.2).
                // Sem este gate o parse "sucede" com zero campos e o payload sairia com
                // documentHealth="clean" — uma mentira: documento vazio não é documento limpo.
                // Fica ANTES do aprendizado de máquina de propósito: arquivo vazio não é amostra.
                if (string.IsNullOrEmpty(sample))
                {
                    return ParseFailureResult(
                        ParseFailureCause.DocumentMalformed,
                        ParseFailure.EmptyDocumentMessage,
                        detectedType,
                        layoutFile.FileName,
                        txtFile.FileName);
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
                // apaga a mensagem que diria a causa real.
                //
                // O gate não sumiu — foi RECLASSIFICADO (spec-taxonomia-de-falha-do-parse.md §3).
                // Antes toda falha virava 422, o que culpa o arquivo do usuário até quando a culpa
                // é nossa. Agora a causa sai do tipo da exceção: entrada ruim → 422; qualquer outra
                // → 500, porque exceção não catalogada é defeito nosso até prova em contrário.
                if (!result.Success || result.Layout == null)
                {
                    // FailureCause nulo aqui = falhou sem exceção catalogada (ex.: Layout nulo com
                    // Success=true). O default culpa a NÓS, não o usuário.
                    var causa = result.FailureCause ?? ParseFailureCause.ParserDefect;

                    return ParseFailureResult(
                        causa,
                        result.ErrorMessage,
                        detectedType,
                        layoutFile.FileName,
                        txtFile.FileName);
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
                var positionalMetadata = LowCodePositionalMetadata.Resolve(
                    result.Layout,
                    result.RawText,
                    expectedLineLength ?? LineLengthResolver.LegacyDefaultLineLength);
                
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
                var eligibility = LowCodeTransformationEligibility.Evaluate(
                    result.Success,
                    flattenedLayout.LayoutGuid,
                    result.RawText,
                    detectedType,
                    isXmlInput);
                string? transformationsReason = eligibility.Reason;
                try
                {
                    if (eligibility.IsEligible)
                    {
                        var syncTimeoutSeconds = _lowCodeOpt.SyncDeliveryTimeoutSeconds > 0 ? _lowCodeOpt.SyncDeliveryTimeoutSeconds : 6;

                        var transformTask = _lowCodeAuto.RunAsync(
                            flattenedLayout.LayoutGuid,
                            flattenedLayout.Name,
                            result.RawText,
                            detectedType,
                            txtFile.FileName,
                            positionalMetadata);

                        var winner = await Task.WhenAny(transformTask, Task.Delay(TimeSpan.FromSeconds(syncTimeoutSeconds)));

                        if (winner == transformTask)
                        {
                            // Já concluiu dentro do teto — observamos o resultado (RunAsync já trata
                            // falha de candidato individual internamente, não deve lançar por isso).
                            var autoResult = await transformTask;
                            transformationsStatus = autoResult.Applicable ? "completed" : "not_applicable";
                            if (autoResult.Applicable)
                            {
                                transformations = autoResult.Candidates;
                                transformationsReason = null;
                            }
                            else
                            {
                                transformationsReason = LowCodeTransformationEligibility.NoMapperReason;
                            }
                        }
                        else
                        {
                            // Estourou o teto síncrono: resposta segue sem esperar mais, processamento
                            // continua em background (persistência em disco já ocorre dentro de
                            // RunAsync). Só observamos exceção aqui pra não gerar unobserved task.
                            transformationsStatus = "processing";
                            transformationsReason = LowCodeTransformationEligibility.TimeoutSyncReason;
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
                    transformationsReason = LowCodeTransformationEligibility.StructuralErrorReason;
                }

                return Ok(new
                {
                    success = true,
                    detectedType,
                    // ✅ Defeito localizável NÃO é 422: o documento parseou e é renderizável, só
                    // vai anotado. A UI decide o modo de exibição por este campo (spec §2.1).
                    documentHealth = DocumentHealth.Resolve(result.ValidationErrors),
                    layout = flattenedLayout,
                    fields = result.ParsedFields,
                    text = result.RawText,
                    summary = result.Summary,
                    documentStructure = documentStructure,
                    lineValidations = lineValidations, // Validações e posições calculadas (apenas para layouts configurados)
                    validationErrors = result.ValidationErrors, // ✅ Erros de validação de tamanho de linha
                    validationWarning = !string.IsNullOrEmpty(result.ErrorMessage) ? result.ErrorMessage : null, // ✅ Aviso se houver erros
                    transformations, // array de candidatos low-code (mapper/target/xml/sucesso-ou-erro) quando concluído a tempo
                    transformationsStatus, // "not_applicable" | "completed" | "processing" | "error"
                    transformationsReason // opcional: no_mapper | type_not_positional | empty_input | timeout_sync | structural_error
                });
            }
            catch (Exception ex)
            {
                // Exceção fora do ParseAsync (montagem da resposta, detecção, I/O do upload).
                // Mesma taxonomia: exceção não catalogada é defeito NOSSO → 500 com mensagem
                // segura. Antes daqui saía `"Erro interno: {ex.Message}"` — uma string crua, sem
                // failureCause nem correlationId, vazando texto de exceção pro cliente.
                _logger.LogError(ex, "Erro durante o parsing do XML. TipoExcecao={ExceptionType}", ex.GetType().FullName);

                return ParseFailureResult(
                    ParseFailure.Classify(ex),
                    ex.Message,
                    "unknown",
                    layoutFile.FileName,
                    txtFile.FileName);
            }
        }

        /// <summary>
        /// Monta a resposta de falha do parse conforme a taxonomia (spec §2.2 e §2.3): 422 quando
        /// a entrada é ruim, 500 quando o defeito é nosso.
        ///
        /// <para><b>Nunca vaze detalhe interno no 500.</b> O <c>message</c> do <c>parser_defect</c>
        /// é um literal fixo; o motivo real (que carrega texto de exceção) fica só no log
        /// estruturado, alcançável pelo <c>correlationId</c> que devolvemos ao cliente.</para>
        /// </summary>
        private IActionResult ParseFailureResult(
            ParseFailureCause causa,
            string? motivoInterno,
            string detectedType,
            string layoutFileName,
            string documentFileName)
        {
            var statusCode = ParseFailure.ToHttpStatusCode(causa);
            var failureCause = ParseFailure.ToWireCode(causa);
            var correlationId = CorrelationContext.CurrentId ?? HttpContext.TraceIdentifier;

            _logger.LogError(
                "Falha no parse do documento. Causa={FailureCause}, Status={StatusCode}, Layout={LayoutFile}, Arquivo={DocumentFile}, Tipo={DetectedType}, Motivo={ErrorMessage}",
                failureCause, statusCode, layoutFileName, documentFileName, detectedType,
                string.IsNullOrWhiteSpace(motivoInterno) ? "(sem motivo registrado)" : motivoInterno);

            return StatusCode(statusCode, new
            {
                success = false,
                failureCause,
                detectedType = string.IsNullOrWhiteSpace(detectedType) ? "unknown" : detectedType,
                message = ParseFailure.ResolveClientMessage(causa, motivoInterno),
                correlationId
            });
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
