using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Generation.Implementations;

using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Services.Generation
{
    /// <summary>
    /// Cobre a correção do bug encontrado pelo `@lp-architect` (ADR
    /// docs/architecture/adr-geracao-documento-exemplo-2026-09-09.md):
    /// <see cref="SyntheticDataGeneratorService"/> comentava gerar CPF/CNPJ "válido", mas só
    /// preenchia dígitos aleatórios — sem dígito verificador real (módulo 11). Este teste roda
    /// a geração várias vezes e valida o dígito verificador de verdade, pra não ser coincidência.
    /// </summary>
    public class SyntheticDataGeneratorServiceCpfCnpjTests
    {
        private const int Iterations = 100;

        private static SyntheticDataGeneratorService CriarServico()
            => new(NullLogger<SyntheticDataGeneratorService>.Instance,
                   new TypedValueGenerator(NullLogger<TypedValueGenerator>.Instance));

        [Fact]
        public async Task GenerateFieldValueAsync_cpf_gera_sempre_digito_verificador_valido()
        {
            var service = CriarServico();
            var field = new FieldElement { Name = "CPF_CLIENTE", LengthField = 11 };

            for (var i = 0; i < Iterations; i++)
            {
                var cpf = await service.GenerateFieldValueAsync(field, $"Record {i}", "cpf");

                Assert.Equal(11, cpf.Length);
                Assert.True(IsCpfValido(cpf), $"CPF gerado inválido: {cpf}");
            }
        }

        [Fact]
        public async Task GenerateFieldValueAsync_cnpj_gera_sempre_digito_verificador_valido()
        {
            var service = CriarServico();
            var field = new FieldElement { Name = "CNPJ_EMPRESA", LengthField = 14 };

            for (var i = 0; i < Iterations; i++)
            {
                var cnpj = await service.GenerateFieldValueAsync(field, $"Record {i}", "cnpj");

                Assert.Equal(14, cnpj.Length);
                Assert.True(IsCnpjValido(cnpj), $"CNPJ gerado inválido: {cnpj}");
            }
        }

        /// <summary>Validação independente de CPF por módulo 11 (não reaproveita a lógica testada).</summary>
        private static bool IsCpfValido(string cpf)
        {
            if (cpf.Length != 11 || !cpf.All(char.IsDigit)) return false;

            var dv1 = CalcularDigito(cpf[..9], pesoInicial: 10);
            var dv2 = CalcularDigito(cpf[..9] + dv1, pesoInicial: 11);

            return cpf == cpf[..9] + dv1 + dv2;
        }

        /// <summary>Validação independente de CNPJ por módulo 11 (não reaproveita a lógica testada).</summary>
        private static bool IsCnpjValido(string cnpj)
        {
            if (cnpj.Length != 14 || !cnpj.All(char.IsDigit)) return false;

            var dv1 = CalcularDigitoCnpj(cnpj[..12]);
            var dv2 = CalcularDigitoCnpj(cnpj[..12] + dv1);

            return cnpj == cnpj[..12] + dv1 + dv2;
        }

        private static int CalcularDigito(string digits, int pesoInicial)
        {
            var sum = 0;
            var weight = pesoInicial;

            foreach (var c in digits)
            {
                sum += (c - '0') * weight;
                weight--;
            }

            var remainder = sum % 11;
            return remainder < 2 ? 0 : 11 - remainder;
        }

        private static int CalcularDigitoCnpj(string digits)
        {
            var sum = 0;
            var weight = 2;

            for (var i = digits.Length - 1; i >= 0; i--)
            {
                sum += (digits[i] - '0') * weight;
                weight = weight == 9 ? 2 : weight + 1;
            }

            var remainder = sum % 11;
            return remainder < 2 ? 0 : 11 - remainder;
        }
    }
}
