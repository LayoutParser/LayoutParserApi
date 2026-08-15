using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>
    /// Persistência mínima do job do pathway IA por ticket (docs/architecture/pathway-ia-execute-candidates.md
    /// §4.2 — "não repetir o mesmo buraco" do Job 1 do pipeline de métricas, que não persistia nada).
    /// Cache em memória (rápido para <c>GetStatusAsync</c> em polling) com fonte de verdade em disco,
    /// mesmo espírito do <see cref="LowCode.LowCodeTransformationStore"/> mas sem a complexidade de
    /// índice/Redis — o volume de jobs IA é muito menor.
    /// </summary>
    public class AiCandidateStore
    {
        private readonly ILogger<AiCandidateStore> _logger;
        private readonly string _storePath;
        private readonly TimeSpan _ttl;
        private readonly Func<DateTimeOffset> _clock;
        private readonly ConcurrentDictionary<string, StoredEntry> _memory = new();

        /// <summary>Ticket em memória + carimbo de quando foi escrito (base do TTL da issue #51).</summary>
        private readonly record struct StoredEntry(AiCandidateStatus Status, DateTime StoredAtUtc);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true
        };

        /// <param name="clock">
        /// Relógio injetável (issue #51). Em produção fica nulo e cai em <c>DateTimeOffset.UtcNow</c>;
        /// nos testes permite envelhecer tickets sem <c>Task.Delay</c> real. É opcional de propósito
        /// para o registro no DI continuar sendo o <c>AddSingleton&lt;AiCandidateStore&gt;()</c> simples.
        /// </param>
        public AiCandidateStore(
            ILogger<AiCandidateStore> logger,
            IOptions<AiTransformationCandidateOptions> options,
            Func<DateTimeOffset>? clock = null)
        {
            _logger = logger;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            var ttlHoras = options.Value.TicketTtlHours;
            _ttl = TimeSpan.FromHours(ttlHoras > 0 ? ttlHoras : AiTransformationCandidateOptions.DefaultTicketTtlHours);
            _storePath = options.Value.StorePath
                ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MLData", "AiTransformationCandidates");

            try
            {
                Directory.CreateDirectory(_storePath);
            }
            catch (Exception ex)
            {
                // Degrade: sem disco o job ainda funciona via memória (perde durabilidade entre restarts).
                _logger.LogWarning(ex, "Falha ao criar diretório da store do pathway IA em {StorePath}", _storePath);
            }
        }

        /// <summary>Janela de retenção efetiva do ticket (<c>AiTransformationCandidate:TicketTtlHours</c>).</summary>
        public TimeSpan Ttl => _ttl;

        public void Set(string ticket, AiCandidateStatus status)
        {
            var agoraUtc = _clock().UtcDateTime;

            // Grava em disco ANTES de publicar em memória: quem consulta GetStatusAsync via
            // polling só pode ver "pronto" depois que o arquivo já está completo e fechado.
            // Antes desta correção a ordem era invertida (memória primeiro) e sob contenção de
            // I/O um segundo Set() concorrente no mesmo ticket colidia no WriteAllText enquanto
            // o primeiro ainda escrevia — reproduzido pelo @lp-qa como IOException intermitente
            // ("being used by another process") ao rodar a suíte 2x seguidas.
            try
            {
                var path = ResolvePath(ticket);
                if (path != null)
                {
                    File.WriteAllText(path, JsonSerializer.Serialize(status, JsonOptions), Encoding.UTF8);

                    // Idade do ticket em disco = LastWriteTime do arquivo. Carimbar explicitamente
                    // (em vez de deixar o do sistema) mantém disco e memória no MESMO relógio —
                    // inclusive o injetado nos testes — e evita ter que sujar o DTO de status, que
                    // é o corpo da resposta do endpoint, com um campo de controle interno.
                    // Try próprio: se só o carimbo falhar (antivírus/indexador segurando o handle),
                    // a persistência em si deu certo — logar "falha ao persistir" seria mentira, e
                    // o arquivo fica com o LastWriteTime do sistema, que em produção é o mesmo instante.
                    try
                    {
                        File.SetLastWriteTimeUtc(path, agoraUtc);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Falha ao carimbar a data do ticket do pathway IA (ticket={Ticket})", ticket);
                    }
                }
            }
            catch (Exception ex)
            {
                // Degrade: mesmo sem durabilidade em disco, ainda publicamos em memória — perder o
                // job inteiro por uma falha de I/O transitória seria pior que perder durabilidade
                // entre restarts (mesmo espírito da falha de Directory.CreateDirectory no ctor).
                _logger.LogWarning(ex, "Falha ao persistir em disco o status do pathway IA (ticket={Ticket})", ticket);
            }

            _memory[ticket] = new StoredEntry(status, agoraUtc);
        }

        public AiCandidateStatus? Get(string ticket)
        {
            var limiteUtc = ExpiracaoUtc();

            if (_memory.TryGetValue(ticket, out var cached))
            {
                if (cached.StoredAtUtc > limiteUtc)
                    return cached.Status;

                // Ticket vencido: sai da memória na hora, custo O(1). Nada de varrer diretório
                // aqui — a limpeza em disco é do BackgroundService, o caminho de request (polling
                // de GetStatusAsync) não pode pagar por I/O de manutenção.
                _memory.TryRemove(ticket, out _);
                return null;
            }

            try
            {
                var path = ResolvePath(ticket);
                if (path == null || !File.Exists(path))
                    return null;

                // Ticket lido do disco (ex.: depois de um restart da API) não ganha vida nova: o
                // TTL é absoluto a partir da última escrita, não deslizante por leitura.
                var escritoEmUtc = File.GetLastWriteTimeUtc(path);
                if (escritoEmUtc <= limiteUtc)
                    return null;

                var json = File.ReadAllText(path, Encoding.UTF8);
                var status = JsonSerializer.Deserialize<AiCandidateStatus>(json, JsonOptions);
                if (status != null)
                    _memory[ticket] = new StoredEntry(status, escritoEmUtc);
                return status;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao ler do disco o status do pathway IA (ticket={Ticket})", ticket);
                return null;
            }
        }

        /// <summary>
        /// Remove tickets vencidos das DUAS camadas — memória e disco (issue #51). Chamado pelo
        /// <see cref="AiCandidateStoreCleanupBackgroundService"/>, fora do caminho de request.
        /// Best-effort ponta a ponta: nenhuma falha de I/O aborta a varredura nem sobe para o host.
        /// </summary>
        /// <remarks>
        /// Síncrono de propósito: é I/O de arquivo (<c>EnumerateFiles</c>/<c>Delete</c>), que não
        /// tem contrapartida async real no BCL — envolver em <c>Task.Run</c> só criaria indireção.
        /// Roda numa thread de background, nunca numa request.
        /// </remarks>
        public AiCandidateStoreCleanupResult RemoveExpired()
        {
            var limiteUtc = ExpiracaoUtc();
            var memoriaRemovida = 0;
            var arquivosRemovidos = 0;

            foreach (var entrada in _memory)
            {
                if (entrada.Value.StoredAtUtc > limiteUtc)
                    continue;

                // Remoção condicional (par chave+valor): se um Set() concorrente republicou o
                // ticket entre a leitura e a remoção, a entrada nova não é derrubada por engano.
                if (_memory.TryRemove(entrada))
                    memoriaRemovida++;
            }

            try
            {
                if (Directory.Exists(_storePath))
                {
                    foreach (var arquivo in Directory.EnumerateFiles(_storePath, "*.json"))
                    {
                        try
                        {
                            if (File.GetLastWriteTimeUtc(arquivo) > limiteUtc)
                                continue;

                            File.Delete(arquivo);
                            arquivosRemovidos++;
                        }
                        catch (Exception ex)
                        {
                            // Best-effort por arquivo (mesmo espírito do CleanupOldRuns do Job 1 de
                            // métricas): um arquivo travado/em uso não pode abortar a varredura toda.
                            _logger.LogDebug(ex, "Falha ao remover ticket vencido do pathway IA: {Arquivo}", arquivo);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao varrer o diretório da store do pathway IA em {StorePath}", _storePath);
            }

            return new AiCandidateStoreCleanupResult(memoriaRemovida, arquivosRemovidos);
        }

        /// <summary>Instante-limite: o que foi escrito até aqui está vencido.</summary>
        private DateTime ExpiracaoUtc() => _clock().UtcDateTime - _ttl;

        // Ticket já vem validado por LowCodeTransformationStore.TryParseTicket no controller — aqui
        // só canonicalizamos e conferimos o prefixo, mesma defesa em profundidade do store low-code.
        private string? ResolvePath(string ticket)
        {
            try
            {
                var safeName = string.Concat(ticket.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_'));
                if (string.IsNullOrWhiteSpace(safeName))
                    return null;

                var root = Path.GetFullPath(_storePath);
                if (!root.EndsWith(Path.DirectorySeparatorChar))
                    root += Path.DirectorySeparatorChar;

                var full = Path.GetFullPath(Path.Combine(root, $"{safeName}.json"));
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Resultado de uma varredura de limpeza da store do pathway IA (issue #51).</summary>
    /// <param name="TicketsEmMemoria">Tickets vencidos removidos do cache em memória.</param>
    /// <param name="ArquivosEmDisco">Arquivos de ticket vencidos removidos do disco.</param>
    public record AiCandidateStoreCleanupResult(int TicketsEmMemoria, int ArquivosEmDisco)
    {
        public int Total => TicketsEmMemoria + ArquivosEmDisco;
    }
}
