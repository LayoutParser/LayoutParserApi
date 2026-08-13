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
        private readonly ConcurrentDictionary<string, AiCandidateStatus> _memory = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true
        };

        public AiCandidateStore(ILogger<AiCandidateStore> logger, IOptions<AiTransformationCandidateOptions> options)
        {
            _logger = logger;
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

        public void Set(string ticket, AiCandidateStatus status)
        {
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
                    File.WriteAllText(path, JsonSerializer.Serialize(status, JsonOptions), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                // Degrade: mesmo sem durabilidade em disco, ainda publicamos em memória — perder o
                // job inteiro por uma falha de I/O transitória seria pior que perder durabilidade
                // entre restarts (mesmo espírito da falha de Directory.CreateDirectory no ctor).
                _logger.LogWarning(ex, "Falha ao persistir em disco o status do pathway IA (ticket={Ticket})", ticket);
            }

            _memory[ticket] = status;
        }

        public AiCandidateStatus? Get(string ticket)
        {
            if (_memory.TryGetValue(ticket, out var cached))
                return cached;

            try
            {
                var path = ResolvePath(ticket);
                if (path == null || !File.Exists(path))
                    return null;

                var json = File.ReadAllText(path, Encoding.UTF8);
                var status = JsonSerializer.Deserialize<AiCandidateStatus>(json, JsonOptions);
                if (status != null)
                    _memory[ticket] = status;
                return status;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao ler do disco o status do pathway IA (ticket={Ticket})", ticket);
                return null;
            }
        }

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
}
