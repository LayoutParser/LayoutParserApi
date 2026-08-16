using System.Collections.Concurrent;

namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>
    /// Implementação em memória do <see cref="IAiFallbackSuppressionGate"/> (§5 do desenho). Estado
    /// compartilhado do processo — <c>Singleton</c>, mesmo padrão de <c>LowCodeTransformationService</c>/
    /// <c>IConnectionMultiplexer</c> (dotnet-standards.md). Deliberadamente NÃO persistido: reinício da
    /// API reseta o cooldown, o que é aceitável (pior caso é uma tentativa extra logo após um deploy).
    /// </summary>
    public class AiFallbackSuppressionGate : IAiFallbackSuppressionGate
    {
        private readonly ConcurrentDictionary<Guid, DateTimeOffset> _cooldowns = new();
        private readonly Func<DateTimeOffset> _clock;

        public AiFallbackSuppressionGate() : this(() => DateTimeOffset.UtcNow)
        {
        }

        // Construtor interno para testes determinísticos (injeta um clock fixo/controlável).
        internal AiFallbackSuppressionGate(Func<DateTimeOffset> clock)
        {
            _clock = clock;
        }

        public bool IsInCooldown(Guid layoutGuid, out DateTimeOffset retryAt)
        {
            if (_cooldowns.TryGetValue(layoutGuid, out var until) && until > _clock())
            {
                retryAt = until;
                return true;
            }

            retryAt = default;
            return false;
        }

        public void RegisterFailure(Guid layoutGuid, TimeSpan cooldown)
        {
            _cooldowns[layoutGuid] = _clock() + cooldown;
        }

        public void ClearCooldown(Guid layoutGuid)
        {
            _cooldowns.TryRemove(layoutGuid, out _);
        }
    }
}
