using System.Collections.Concurrent;

namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>
    /// Fallback mínimo da issue #98 (prompt customizado complementar): guarda, por usuário
    /// (mesma partição da issue #92 — <c>userId</c> resolvido pelo <c>ICurrentUser</c> no
    /// controller), a instrução adicional que o usuário quer anexar ao prompt padrão do
    /// pathway IA. Não é o agregado de sessão completo da issue #6 — é o degrau mais simples
    /// que já resolve o requisito de produto (docs/architecture/escopo-generico-txt-xml-e-
    /// acesso-por-papel-2026-08-14.md §8) sem esperar a sessão amadurecer.
    /// </summary>
    /// <remarks>
    /// Só em memória (<c>Singleton</c>, mesmo espírito de <see cref="IAiFallbackSuppressionGate"/>):
    /// perder a instrução customizada num restart da API degrada para "sem prompt adicional",
    /// nunca quebra o pathway — o prompt padrão sozinho já é funcional.
    /// </remarks>
    public class AiUserInstructionStore
    {
        /// <summary>Teto de tamanho da instrução — mesmo espírito de <c>Truncate(inputContent, 4000)</c>
        /// já usado no serviço, para não estourar custo/tempo de Ollama (CPU-only, recurso compartilhado).</summary>
        public const int MaxLength = 2000;

        private const string AnonymousBucket = "_anonimo";
        private readonly ConcurrentDictionary<string, string> _instructions = new();

        public void Set(string userId, string? instruction)
        {
            var key = SafeUserBucket(userId);

            if (string.IsNullOrWhiteSpace(instruction))
            {
                _instructions.TryRemove(key, out _);
                return;
            }

            var truncated = instruction.Length > MaxLength ? instruction.Substring(0, MaxLength) : instruction;
            _instructions[key] = truncated;
        }

        public string? Get(string userId)
            => _instructions.TryGetValue(SafeUserBucket(userId), out var instruction) ? instruction : null;

        private static string SafeUserBucket(string? userId)
            => string.IsNullOrWhiteSpace(userId) ? AnonymousBucket : userId;
    }
}
