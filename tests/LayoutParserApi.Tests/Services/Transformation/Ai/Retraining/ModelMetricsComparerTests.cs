using LayoutParserApi.Services.Transformation.Ai.Retraining;

namespace LayoutParserApi.Tests.Services.Transformation.Ai.Retraining
{
    /// <summary>Validação pós-treino antes de promover o modelo (F4.3, issue #351, ADR §6.3).</summary>
    public class ModelMetricsComparerTests
    {
        private static ModelMetricsSnapshot Snap(double conv, double xsd, double diff, int n = 54, string model = "m")
            => new(model, n, conv, xsd, diff);

        [Fact]
        public void Promove_quando_nenhuma_metrica_regride()
        {
            var atual = Snap(0.40, 0.60, 0.30);
            var novo = Snap(0.45, 0.60, 0.35);

            var decision = ModelMetricsComparer.Decide(atual, novo);

            Assert.True(decision.Promote);
        }

        [Fact]
        public void Bloqueia_quando_convergencia_regride()
        {
            var atual = Snap(0.40, 0.60, 0.30);
            var novo = Snap(0.35, 0.60, 0.30);

            var decision = ModelMetricsComparer.Decide(atual, novo);

            Assert.False(decision.Promote);
            Assert.Contains(decision.Reasons, r => r.Contains("REGRESSÃO") && r.Contains("convergência"));
        }

        [Fact]
        public void Bloqueia_quando_candidato_nao_tem_amostras()
        {
            var decision = ModelMetricsComparer.Decide(Snap(0.4, 0.6, 0.3), Snap(0.9, 0.9, 0.9, n: 0));

            Assert.False(decision.Promote);
        }

        [Fact]
        public void Tolerancia_absorve_queda_pequena()
        {
            var atual = Snap(0.40, 0.60, 0.30);
            var novo = Snap(0.395, 0.60, 0.30);

            Assert.False(ModelMetricsComparer.Decide(atual, novo, tolerance: 0.0).Promote);
            Assert.True(ModelMetricsComparer.Decide(atual, novo, tolerance: 0.01).Promote);
        }
    }
}
