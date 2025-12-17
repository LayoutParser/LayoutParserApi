using LayoutParserApi.Models.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace LayoutParserApi.Services.Validation
{
    /// <summary>
    /// Background Service para validação automática de layouts
    /// Executa validação na inicialização e diariamente no horário configurado
    /// </summary>
    public class LayoutValidationBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<LayoutValidationBackgroundService> _logger;
        private readonly IConfiguration _configuration;
        private bool _initialValidationDone = false;

        public LayoutValidationBackgroundService(
            IServiceProvider serviceProvider,
            ILogger<LayoutValidationBackgroundService> logger,
            IConfiguration configuration)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _configuration = configuration;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Aguardar um pouco para garantir que os serviços estão prontos
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            // Executar validação inicial
            if (!_initialValidationDone)
            {
                await ExecuteInitialValidationAsync(stoppingToken);
                _initialValidationDone = true;
            }

            // Executar validação diária no horário configurado
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var nextRunTime = GetNextValidationTime();
                    var delay = nextRunTime - DateTime.Now;

                    if (delay > TimeSpan.Zero)
                    {
                        _logger.LogInformation("Próxima validação agendada para: {NextRunTime} (em {Delay})", 
                            nextRunTime, delay);
                        await Task.Delay(delay, stoppingToken);
                    }

                    await ExecuteDailyValidationAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // Serviço está sendo parado
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erro na validação diária de layouts");
                    // Em caso de erro, aguardar 1 hora antes de tentar novamente
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                }
            }
        }

        /// <summary>
        /// Executa validação inicial (após startup)
        /// </summary>
        private async Task ExecuteInitialValidationAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("=== Iniciando validação inicial de layouts ===");

                using var scope = _serviceProvider.CreateScope();
                var validationService = scope.ServiceProvider.GetRequiredService<LayoutValidationService>();

                // Obter layouts específicos para validar (configurados no appsettings.json)
                var layoutGuidsToValidate = GetLayoutGuidsToValidate();

                if (layoutGuidsToValidate.Any())
                {
                    _logger.LogInformation("Validando {Count} layouts específicos na inicialização", layoutGuidsToValidate.Count);
                    var results = await validationService.ValidateLayoutsByGuidsAsync(layoutGuidsToValidate);
                    LogValidationResults(results, "inicial");
                }
                else
                {
                    // Se não houver layouts específicos configurados, validar todos
                    _logger.LogInformation("Nenhum layout específico configurado. Validando todos os layouts.");
                    var results = await validationService.ValidateAllLayoutsAsync(forceRevalidation: true);
                    LogValidationResults(results, "inicial");
                }

                _logger.LogInformation("=== Validação inicial concluída ===");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro na validação inicial de layouts");
            }
        }

        /// <summary>
        /// Executa validação diária
        /// </summary>
        private async Task ExecuteDailyValidationAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("=== Iniciando validação diária de layouts ===");

                using var scope = _serviceProvider.CreateScope();
                var validationService = scope.ServiceProvider.GetRequiredService<LayoutValidationService>();

                // Validar todos os layouts
                var results = await validationService.ValidateAllLayoutsAsync(forceRevalidation: true);
                LogValidationResults(results, "diária");

                _logger.LogInformation("=== Validação diária concluída ===");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro na validação diária de layouts");
                throw; // Re-throw para que o loop aguarde antes de tentar novamente
            }
        }

        /// <summary>
        /// Calcula o próximo horário de validação baseado na configuração
        /// </summary>
        private DateTime GetNextValidationTime()
        {
            // Obter horário configurado (formato HH:mm)
            var validationTime = _configuration["LayoutValidation:DailyValidationTime"] ?? "02:00";
            
            if (!TimeSpan.TryParse(validationTime, out var timeSpan))
            {
                // Valor padrão: 02:00
                timeSpan = new TimeSpan(2, 0, 0);
                _logger.LogWarning("Horário de validação inválido no appsettings.json. Usando padrão: 02:00");
            }

            var now = DateTime.Now;
            var nextRun = now.Date.Add(timeSpan);

            // Se o horário já passou hoje, agendar para amanhã
            if (nextRun <= now)
            {
                nextRun = nextRun.AddDays(1);
            }

            return nextRun;
        }

        /// <summary>
        /// Obtém lista de GUIDs de layouts para validar na inicialização
        /// </summary>
        private List<string> GetLayoutGuidsToValidate()
        {
            var guids = new List<string>();

            // Ler do appsettings.json - seção LayoutValidation:InitialValidationLayouts
            var layoutGuidsSection = _configuration.GetSection("LayoutValidation:InitialValidationLayouts");
            
            if (layoutGuidsSection.Exists())
            {
                // Pode ser um array de strings ou uma string separada por vírgula
                var guidsArray = layoutGuidsSection.Get<string[]>();
                if (guidsArray != null && guidsArray.Length > 0)
                {
                    guids.AddRange(guidsArray);
                }
                else
                {
                    // Tentar como string única separada por vírgula
                    var guidsString = _configuration["LayoutValidation:InitialValidationLayouts"];
                    if (!string.IsNullOrEmpty(guidsString))
                    {
                        guids.AddRange(guidsString.Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(g => g.Trim()));
                    }
                }
            }

            // Remover prefixo LAY_ se houver
            return guids.Select(g => g.StartsWith("LAY_", StringComparison.OrdinalIgnoreCase) 
                ? g.Substring(4) 
                : g).ToList();
        }

        /// <summary>
        /// Loga resultados da validação
        /// </summary>
        private void LogValidationResults(List<LayoutValidationResult> results, string validationType)
        {
            var total = results.Count;
            var withErrors = results.Count(r => !r.IsValid);
            var valid = results.Count(r => r.IsValid);

            _logger.LogInformation("Validação {Type}: {Total} layouts processados, {Valid} válidos, {Errors} com erros",
                validationType, total, valid, withErrors);

            if (withErrors > 0)
            {
                _logger.LogWarning("=== LAYOUTS COM ERROS ===");
                foreach (var result in results.Where(r => !r.IsValid))
                {
                    _logger.LogWarning("Layout: {LayoutName} (GUID: {Guid})", result.LayoutName, result.LayoutGuid);
                    _logger.LogWarning("  Erros encontrados: {ErrorCount}", result.Errors.Count);
                    
                    foreach (var error in result.Errors)
                    {
                        _logger.LogWarning("  - {LineName}: {Message}", error.LineName, error.ErrorMessage);
                    }
                }
                _logger.LogWarning("========================");
            }
        }
    }
}

