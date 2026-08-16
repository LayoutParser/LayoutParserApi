using LayoutParserApi.Services.Transformation.Ai;

namespace LayoutParserApi.Tests.Services.Transformation.Ai
{
    /// <summary>
    /// Circuito de proteção do fallback automático de IA (Estado A — §5 do desenho
    /// docs/architecture/design-fallback-ia-automatico-2026-08-16.md): cooldown de 4h por
    /// <c>LayoutGuid</c>, cross-usuário, em memória.
    /// </summary>
    public class AiFallbackSuppressionGateTests
    {
        [Fact]
        public void Layout_nunca_tentado_nao_esta_em_cooldown()
        {
            var gate = new AiFallbackSuppressionGate();
            Assert.False(gate.IsInCooldown(Guid.NewGuid(), out _));
        }

        [Fact]
        public void RegisterFailure_bloqueia_o_layout_dentro_da_janela_de_cooldown()
        {
            var agora = new DateTimeOffset(2026, 8, 16, 10, 0, 0, TimeSpan.Zero);
            var gate = new AiFallbackSuppressionGate(() => agora);
            var layoutGuid = Guid.NewGuid();

            gate.RegisterFailure(layoutGuid, TimeSpan.FromMinutes(240));

            var emCooldown = gate.IsInCooldown(layoutGuid, out var retryAt);
            Assert.True(emCooldown);
            Assert.Equal(agora.AddMinutes(240), retryAt);
        }

        [Fact]
        public void Cooldown_expira_apos_a_janela_configurada()
        {
            var agora = new DateTimeOffset(2026, 8, 16, 10, 0, 0, TimeSpan.Zero);
            var gate = new AiFallbackSuppressionGate(() => agora);
            var layoutGuid = Guid.NewGuid();

            gate.RegisterFailure(layoutGuid, TimeSpan.FromMinutes(240));

            // Relógio avança para depois do cooldown (4h + 1min): a supressão deve liberar sozinha,
            // sem precisar de nenhuma limpeza explícita.
            agora = agora.AddMinutes(241);
            Assert.False(gate.IsInCooldown(layoutGuid, out _));
        }

        [Fact]
        public void ClearCooldown_libera_o_layout_imediatamente_mesmo_dentro_da_janela()
        {
            var gate = new AiFallbackSuppressionGate();
            var layoutGuid = Guid.NewGuid();

            gate.RegisterFailure(layoutGuid, TimeSpan.FromMinutes(240));
            Assert.True(gate.IsInCooldown(layoutGuid, out _));

            gate.ClearCooldown(layoutGuid);
            Assert.False(gate.IsInCooldown(layoutGuid, out _));
        }

        [Fact]
        public void Cooldown_e_isolado_por_layout_nao_afeta_outros_layouts()
        {
            var gate = new AiFallbackSuppressionGate();
            var layoutA = Guid.NewGuid();
            var layoutB = Guid.NewGuid();

            gate.RegisterFailure(layoutA, TimeSpan.FromMinutes(240));

            Assert.True(gate.IsInCooldown(layoutA, out _));
            Assert.False(gate.IsInCooldown(layoutB, out _));
        }
    }
}
