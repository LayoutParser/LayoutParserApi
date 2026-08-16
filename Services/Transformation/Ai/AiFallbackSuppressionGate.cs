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

        /// <param name="clock">Relógio injetável para testes determinísticos. Em produção fica
        /// nulo e cai em <see cref="DateTimeOffset.UtcNow"/>.</param>
        public AiFallbackSuppressionGate(Func<DateTimeOffset>? clock = null)
        {
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
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
