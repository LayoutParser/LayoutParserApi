namespace LayoutParserApi.Services.Validation
{
    /// <summary>
    /// Validação determinística de campo de input: tamanho/formato/checksum contra o
    /// <c>LengthField</c> já declarado no Layout XML - sem IA nenhuma.
    /// Complementa o loop de diagnóstico via Ollama (Grupo 3 do roadmap de IA): a ideia é
    /// rodar este validador ANTES de acionar qualquer LLM - se ele já explica o defeito
    /// (ex.: CHAVEACESSO com 43 dígitos em vez de 44), não há necessidade de "julgamento"
    /// de um modelo de linguagem para um problema de tamanho/checksum.
    /// Item 3.1 do dispatch de IA (docs/architecture/ai-roadmap-dispatch.md, 2026-07-21) e
    /// ia-fiscal-diagnosis-vision.md §3.2.
    /// </summary>
    public class FieldContentValidationService
    {
        private readonly ILogger<FieldContentValidationService> _logger;

        public FieldContentValidationService(ILogger<FieldContentValidationService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Valida um campo isolado contra o <c>LengthField</c> declarado no layout: compara o
        /// tamanho do conteúdo "lógico" (aparado de espaços) com o tamanho esperado.
        /// Diferente da extração posicional (que sempre recorta exatamente N caracteres de
        /// largura fixa), este método pega o caso em que o conteúdo em si é mais curto que o
        /// campo declarado (ex.: número enviado sem zeros à esquerda, preenchido com espaço).
        /// </summary>
        public FieldContentValidationResult ValidateLength(string fieldName, string value, int expectedLength)
        {
            var result = new FieldContentValidationResult
            {
                FieldName = fieldName,
                Value = value ?? "",
                ExpectedLength = expectedLength
            };

            try
            {
                var conteudoLogico = (value ?? "").Trim();
                result.ActualLength = conteudoLogico.Length;

                if (expectedLength > 0 && conteudoLogico.Length != expectedLength)
                {
                    result.IsValid = false;
                    result.Issues.Add($"Campo '{fieldName}': tamanho de conteúdo {conteudoLogico.Length} difere do esperado ({expectedLength}).");
                    _logger.LogWarning("Validação determinística: campo {FieldName} com tamanho {ActualLength}, esperado {ExpectedLength}",
                        fieldName, conteudoLogico.Length, expectedLength);
                }

                return result;
            }
            catch (Exception ex)
            {
                // Degrada graciosamente: falha na validação não pode derrubar o fluxo principal,
                // apenas deixa de validar este campo específico (fica como IsValid = true por padrão).
                _logger.LogError(ex, "Erro ao validar tamanho do campo {FieldName}", fieldName);
                return result;
            }
        }

        /// <summary>
        /// Valida a Chave de Acesso da NF-e/CT-e (44 dígitos): tamanho, se é 100% numérica e
        /// recálculo do dígito verificador (cDV) pelo algoritmo módulo-11 público da NF-e.
        /// Estrutura por blocos (decomposição documentada na memória da Lia, confirmada contra
        /// gabarito real): cUF(1-2) AAMM(3-6) CNPJ(7-20) mod(21-22) serie(23-25) nNF(26-34)
        /// tpEmis(35) cNF(36-43) cDV(44). Não depende de IA.
        /// Ver ia-fiscal-diagnosis-vision.md §3.2 (exemplo real: chave com 43 em vez de 44 dígitos).
        /// </summary>
        public FieldContentValidationResult ValidateChaveAcessoNFe(string chaveAcesso, string fieldName = "CHAVEACESSO")
        {
            const int tamanhoEsperado = 44;
            var result = new FieldContentValidationResult
            {
                FieldName = fieldName,
                Value = chaveAcesso ?? "",
                ExpectedLength = tamanhoEsperado
            };

            try
            {
                var valor = (chaveAcesso ?? "").Trim();
                result.ActualLength = valor.Length;

                if (valor.Length != tamanhoEsperado)
                {
                    result.IsValid = false;
                    result.Issues.Add($"Chave de acesso com {valor.Length} dígito(s) (esperado {tamanhoEsperado}).");
                    _logger.LogWarning("Chave de acesso NF-e com tamanho invalido: {Length} (esperado {Expected})", valor.Length, tamanhoEsperado);
                    return result; // Sem os 44 dígitos não há como recalcular o DV com segurança.
                }

                if (!valor.All(char.IsDigit))
                {
                    result.IsValid = false;
                    result.Issues.Add("Chave de acesso contém caractere(s) não numérico(s).");
                    _logger.LogWarning("Chave de acesso NF-e com caracteres nao numericos: {Valor}", valor);
                    return result;
                }

                // cDV é o último dígito; os 43 anteriores alimentam o cálculo módulo-11.
                var corpo = valor.Substring(0, 43);
                var cDVInformado = valor[43] - '0';
                var cDVCalculado = CalcularDigitoVerificadorModulo11(corpo);

                if (cDVInformado != cDVCalculado)
                {
                    result.IsValid = false;
                    result.Issues.Add($"Dígito verificador (cDV) não confere: informado {cDVInformado}, calculado {cDVCalculado}.");
                    _logger.LogWarning("Chave de acesso NF-e com DV invalido: informado {Informado}, calculado {Calculado}", cDVInformado, cDVCalculado);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao validar chave de acesso NF-e");
                return result;
            }
        }

        /// <summary>
        /// Algoritmo público módulo-11 do dígito verificador da chave de acesso NF-e/CT-e:
        /// pesos cíclicos 2..9 aplicados da direita para a esquerda sobre os 43 dígitos.
        /// </summary>
        private static int CalcularDigitoVerificadorModulo11(string corpo43Digitos)
        {
            var soma = 0;
            var peso = 2;

            for (var i = corpo43Digitos.Length - 1; i >= 0; i--)
            {
                soma += (corpo43Digitos[i] - '0') * peso;
                peso = peso == 9 ? 2 : peso + 1;
            }

            var resto = soma % 11;
            return (resto == 0 || resto == 1) ? 0 : 11 - resto;
        }
    }
}
