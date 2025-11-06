using LayoutParserApi.Services.Generation.Interfaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace LayoutParserApi.Services.Generation.Implementations.FieldGenerators
{
    /// <summary>
    /// Gerador para campos comuns (HEADER, Data, Hora, CNPJ, CPF, etc.)
    /// </summary>
    public class CommonFieldGenerator : IFieldGenerator
    {
        private readonly ILogger<CommonFieldGenerator> _logger;
        private static readonly Random _random = new Random();

        public CommonFieldGenerator(ILogger<CommonFieldGenerator> logger)
        {
            _logger = logger;
        }

        public bool CanGenerate(string fieldName, int length, string dataType = null)
        {
            if (string.IsNullOrWhiteSpace(fieldName))
                return false;

            var normalizedName = fieldName.ToUpperInvariant().Trim();

            // Campos que podemos gerar (genéricos para todos os tipos de layout)
            return normalizedName.Contains("HEADER") ||
                   normalizedName.Contains("DATA") ||
                   normalizedName.Contains("HORA") ||
                   normalizedName.Contains("HOUR") ||
                   normalizedName.Contains("DATE") ||
                   normalizedName.Contains("EMISSAO") ||
                   normalizedName.Contains("EMISSION") ||
                   normalizedName.Contains("CNPJ") ||
                   normalizedName.Contains("CPF") ||
                   normalizedName.Contains("SEQUENCIA") ||
                   normalizedName.Contains("SEQUENCE") ||
                   normalizedName.Contains("SEQUENCIAL") ||
                   normalizedName.Contains("NUMERO") ||
                   normalizedName.Contains("NUMBER") ||
                   normalizedName.Contains("CODIGO") ||
                   normalizedName.Contains("CODE") ||
                   normalizedName == "FILLER" ||
                   normalizedName.Contains("FILLER") ||
                   // Campos específicos de IDOC/SAP
                   normalizedName == "CUF" ||
                   normalizedName == "CNF" ||
                   normalizedName == "MOD" ||
                   normalizedName == "SERIE" ||
                   normalizedName == "NNF" ||
                   normalizedName == "INDPAG" ||
                   normalizedName == "NATOP" ||
                   // Campos XML comuns
                   normalizedName == "VERSION" ||
                   normalizedName == "ENCODING" ||
                   normalizedName == "STANDALONE" ||
                   normalizedName.Contains("XMLNS") ||
                   normalizedName == "CONTENT";
        }

        public string Generate(string fieldName, int length, string alignment, int recordIndex = 0, Dictionary<string, object> context = null)
        {
            if (string.IsNullOrWhiteSpace(fieldName))
                return new string(' ', length);

            var normalizedName = fieldName.ToUpperInvariant().Trim();
            var result = "";

            // HEADER - geralmente é um valor fixo ou inicial
            if (normalizedName.Contains("HEADER"))
            {
                result = GenerateHeader(length, alignment, context);
            }
            // FILLER - campo de preenchimento com espaços (NUNCA preencher com texto)
            else if (normalizedName == "FILLER" || normalizedName.Contains("FILLER"))
            {
                // FILLER sempre deve ser apenas espaços, nunca texto
                result = new string(' ', length);
                _logger.LogDebug("Campo FILLER gerado: {Length} espaços", length);
            }
            // DATA / DATE / EMISSAO
            else if (normalizedName.Contains("DATA") || normalizedName.Contains("DATE") || 
                     normalizedName.Contains("EMISSAO") || normalizedName.Contains("EMISSION"))
            {
                result = GenerateDate(length, alignment, context);
            }
            // HORA / HOUR
            else if (normalizedName.Contains("HORA") || normalizedName.Contains("HOUR"))
            {
                result = GenerateTime(length, alignment, context);
            }
            // CNPJ (qualquer variação: CNPJEmissorNF, CNPJEmitenteNF, etc.)
            else if (normalizedName.Contains("CNPJ"))
            {
                result = GenerateCNPJ(length, alignment);
            }
            // CPF
            else if (normalizedName.Contains("CPF"))
            {
                result = GenerateCPF(length, alignment);
            }
            // SEQUENCIA / SEQUENCE / SEQUENCIAL
            else if (normalizedName.Contains("SEQUENCIA") || normalizedName.Contains("SEQUENCE") ||
                     normalizedName.Contains("SEQUENCIAL"))
            {
                result = GenerateSequence(recordIndex, length, alignment);
            }
            // NUMERO / NUMBER / CODIGO / CODE
            else if (normalizedName.Contains("NUMERO") || normalizedName.Contains("NUMBER") ||
                     normalizedName.Contains("CODIGO") || normalizedName.Contains("CODE"))
            {
                result = GenerateNumber(length, alignment, recordIndex);
            }
            // Campos específicos de IDOC/SAP
            else if (normalizedName == "CUF")
            {
                result = GenerateCUF(length, alignment); // Código da UF (2 dígitos)
            }
            else if (normalizedName == "CNF")
            {
                result = GenerateCNF(length, alignment); // Código numérico da NF (8 dígitos)
            }
            else if (normalizedName == "MOD")
            {
                result = GenerateMOD(length, alignment); // Modelo da NF (2 dígitos, geralmente "55")
            }
            else if (normalizedName == "SERIE" || normalizedName.Contains("SERIE"))
            {
                result = GenerateSerie(length, alignment); // Série da NF (3 dígitos)
            }
            else if (normalizedName == "NNF" || normalizedName.Contains("NNF"))
            {
                result = GenerateNNF(length, alignment, recordIndex); // Número da NF (9 dígitos)
            }
            else if (normalizedName == "INDPAG")
            {
                result = GenerateIndPag(length, alignment); // Indicador de pagamento (1 dígito: 0, 1, 2)
            }
            else if (normalizedName == "NATOP")
            {
                result = GenerateNatOp(length, alignment); // Natureza da operação (texto)
            }
            // Campos XML comuns
            else if (normalizedName == "VERSION")
            {
                result = FormatField("1.0", length, alignment);
            }
            else if (normalizedName == "ENCODING")
            {
                result = FormatField("UTF-8", length, alignment);
            }
            else if (normalizedName == "STANDALONE")
            {
                result = FormatField("no", length, alignment);
            }
            else if (normalizedName.Contains("XMLNS"))
            {
                // Namespace XML - usar valor estático se disponível no contexto
                if (context != null && context.ContainsKey("StaticValue"))
                {
                    result = FormatField(context["StaticValue"]?.ToString() ?? "", length, alignment);
                }
                else
                {
                    result = FormatField("http://www.portalfiscal.inf.br/nfe", length, alignment);
                }
            }
            else if (normalizedName == "CONTENT")
            {
                // Campo content geralmente é preenchido pela IA
                result = new string(' ', length);
            }
            else
            {
                // Campo não reconhecido, retornar espaços
                result = new string(' ', length);
            }

            // Aplicar alinhamento e ajustar tamanho
            return FormatField(result, length, alignment);
        }

        private string GenerateHeader(int length, string alignment, Dictionary<string, object> context)
        {
            // HEADER geralmente é fixo ou vem do InitialValue
            if (context != null && context.ContainsKey("InitialValue"))
            {
                var initialValue = context["InitialValue"]?.ToString() ?? "";
                return FormatField(initialValue, length, alignment);
            }

            // Se não houver InitialValue, gerar um HEADER padrão
            var header = "HEADER";
            return FormatField(header, length, alignment);
        }

        private string GenerateDate(int length, string alignment, Dictionary<string, object> context)
        {
            var now = DateTime.Now;
            var dateStr = "";

            // Formatos comuns de data
            if (length >= 8)
            {
                // DDMMYYYY
                dateStr = now.ToString("ddMMyyyy");
            }
            else if (length >= 6)
            {
                // DDMMYY
                dateStr = now.ToString("ddMMyy");
            }
            else if (length >= 4)
            {
                // MMYY
                dateStr = now.ToString("MMyy");
            }
            else
            {
                // Formato mínimo
                dateStr = now.ToString("dd");
            }

            // Adicionar variação aleatória (dias anteriores)
            if (context != null && context.ContainsKey("RecordIndex"))
            {
                var recordIndex = Convert.ToInt32(context["RecordIndex"] ?? 0);
                var daysAgo = _random.Next(0, 30); // Últimos 30 dias
                var date = now.AddDays(-daysAgo);
                
                if (length >= 8)
                    dateStr = date.ToString("ddMMyyyy");
                else if (length >= 6)
                    dateStr = date.ToString("ddMMyy");
            }

            return FormatField(dateStr, length, alignment);
        }

        private string GenerateTime(int length, string alignment, Dictionary<string, object> context)
        {
            var now = DateTime.Now;
            var timeStr = "";

            // Formatos comuns de hora
            if (length >= 9)
            {
                // HHMMSSMMM (com milissegundos) ou HH:MM:SS
                timeStr = now.ToString("HHmmss") + _random.Next(100, 999).ToString();
            }
            else if (length >= 8)
            {
                // HHMMSSMM (com centésimos de segundo)
                timeStr = now.ToString("HHmmss") + _random.Next(10, 99).ToString();
            }
            else if (length >= 6)
            {
                // HHMMSS
                timeStr = now.ToString("HHmmss");
            }
            else if (length >= 4)
            {
                // HHMM
                timeStr = now.ToString("HHmm");
            }
            else
            {
                // HH
                timeStr = now.ToString("HH");
            }

            // Adicionar variação aleatória
            if (context != null && context.ContainsKey("RecordIndex"))
            {
                var recordIndex = Convert.ToInt32(context["RecordIndex"] ?? 0);
                var minutesOffset = _random.Next(0, 1440); // 0 a 24 horas em minutos
                var time = now.AddMinutes(minutesOffset);
                
                if (length >= 9)
                    timeStr = time.ToString("HHmmss") + _random.Next(100, 999).ToString();
                else if (length >= 8)
                    timeStr = time.ToString("HHmmss") + _random.Next(10, 99).ToString();
                else if (length >= 6)
                    timeStr = time.ToString("HHmmss");
                else if (length >= 4)
                    timeStr = time.ToString("HHmm");
            }

            return FormatField(timeStr, length, alignment);
        }

        private string GenerateCNPJ(int length, string alignment)
        {
            // Gerar CNPJ válido (14 dígitos)
            var cnpj = GenerateCNPJDigits();
            
            // Se o campo for maior que 14, preencher com zeros à esquerda ou espaços
            if (length > 14)
            {
                if (alignment == "Right")
                    cnpj = cnpj.PadLeft(length, '0');
                else
                    cnpj = cnpj.PadRight(length, ' ');
            }
            else if (length < 14)
            {
                // Truncar se necessário
                cnpj = cnpj.Substring(0, length);
            }

            return FormatField(cnpj, length, alignment);
        }

        private string GenerateCPF(int length, string alignment)
        {
            // Gerar CPF válido (11 dígitos)
            var cpf = GenerateCPFDigits();
            
            if (length > 11)
            {
                if (alignment == "Right")
                    cpf = cpf.PadLeft(length, '0');
                else
                    cpf = cpf.PadRight(length, ' ');
            }
            else if (length < 11)
            {
                cpf = cpf.Substring(0, length);
            }

            return FormatField(cpf, length, alignment);
        }

        private string GenerateSequence(int recordIndex, int length, string alignment)
        {
            // Gerar sequência baseada no índice do registro
            var sequence = (recordIndex + 1).ToString();
            
            // Preencher com zeros à esquerda se necessário
            if (length > sequence.Length)
            {
                sequence = sequence.PadLeft(length, '0');
            }
            else if (length < sequence.Length)
            {
                // Se o número for maior que o campo, usar módulo
                var maxValue = (int)Math.Pow(10, length) - 1;
                sequence = ((recordIndex + 1) % maxValue).ToString().PadLeft(length, '0');
            }

            return FormatField(sequence, length, alignment);
        }

        private string GenerateNumber(int length, string alignment, int recordIndex)
        {
            // Gerar número aleatório ou sequencial
            var number = _random.Next(1, (int)Math.Pow(10, Math.Min(length, 9)));
            var numberStr = number.ToString();
            
            if (length > numberStr.Length)
            {
                if (alignment == "Right")
                    numberStr = numberStr.PadLeft(length, '0');
                else
                    numberStr = numberStr.PadRight(length, ' ');
            }

            return FormatField(numberStr, length, alignment);
        }

        private string FormatField(string value, int length, string alignment)
        {
            if (string.IsNullOrEmpty(value))
                value = "";

            // Truncar se necessário
            if (value.Length > length)
            {
                value = value.Substring(0, length);
            }

            // Aplicar alinhamento
            if (alignment == "Right")
            {
                value = value.PadLeft(length, ' ');
            }
            else if (alignment == "Center")
            {
                var padding = length - value.Length;
                var leftPad = padding / 2;
                var rightPad = padding - leftPad;
                value = new string(' ', leftPad) + value + new string(' ', rightPad);
            }
            else // Left (padrão)
            {
                value = value.PadRight(length, ' ');
            }

            return value;
        }

        private string GenerateCNPJDigits()
        {
            // Gerar CNPJ válido (sem formatação, apenas dígitos)
            var n1 = _random.Next(0, 10);
            var n2 = _random.Next(0, 10);
            var n3 = _random.Next(0, 10);
            var n4 = _random.Next(0, 10);
            var n5 = _random.Next(0, 10);
            var n6 = _random.Next(0, 10);
            var n7 = _random.Next(0, 10);
            var n8 = _random.Next(0, 10);
            var n9 = _random.Next(0, 10);
            var n10 = _random.Next(0, 10);
            var n11 = _random.Next(0, 10);
            var n12 = _random.Next(0, 10);

            // Calcular dígitos verificadores (simplificado)
            var d1 = CalculateCNPJDigit1(n1, n2, n3, n4, n5, n6, n7, n8, n9, n10, n11, n12);
            var d2 = CalculateCNPJDigit2(n1, n2, n3, n4, n5, n6, n7, n8, n9, n10, n11, n12, d1);

            return $"{n1}{n2}{n3}{n4}{n5}{n6}{n7}{n8}{n9}{n10}{n11}{n12}{d1}{d2}";
        }

        private string GenerateCPFDigits()
        {
            // Gerar CPF válido (sem formatação, apenas dígitos)
            var n1 = _random.Next(0, 10);
            var n2 = _random.Next(0, 10);
            var n3 = _random.Next(0, 10);
            var n4 = _random.Next(0, 10);
            var n5 = _random.Next(0, 10);
            var n6 = _random.Next(0, 10);
            var n7 = _random.Next(0, 10);
            var n8 = _random.Next(0, 10);
            var n9 = _random.Next(0, 10);

            // Calcular dígitos verificadores
            var d1 = CalculateCPFDigit1(n1, n2, n3, n4, n5, n6, n7, n8, n9);
            var d2 = CalculateCPFDigit2(n1, n2, n3, n4, n5, n6, n7, n8, n9, d1);

            return $"{n1}{n2}{n3}{n4}{n5}{n6}{n7}{n8}{n9}{d1}{d2}";
        }

        private int CalculateCNPJDigit1(int n1, int n2, int n3, int n4, int n5, int n6, int n7, int n8, int n9, int n10, int n11, int n12)
        {
            var sum = n1 * 5 + n2 * 4 + n3 * 3 + n4 * 2 + n5 * 9 + n6 * 8 + n7 * 7 + n8 * 6 + n9 * 5 + n10 * 4 + n11 * 3 + n12 * 2;
            var remainder = sum % 11;
            return remainder < 2 ? 0 : 11 - remainder;
        }

        private int CalculateCNPJDigit2(int n1, int n2, int n3, int n4, int n5, int n6, int n7, int n8, int n9, int n10, int n11, int n12, int d1)
        {
            var sum = n1 * 6 + n2 * 5 + n3 * 4 + n4 * 3 + n5 * 2 + n6 * 9 + n7 * 8 + n8 * 7 + n9 * 6 + n10 * 5 + n11 * 4 + n12 * 3 + d1 * 2;
            var remainder = sum % 11;
            return remainder < 2 ? 0 : 11 - remainder;
        }

        private int CalculateCPFDigit1(int n1, int n2, int n3, int n4, int n5, int n6, int n7, int n8, int n9)
        {
            var sum = n1 * 10 + n2 * 9 + n3 * 8 + n4 * 7 + n5 * 6 + n6 * 5 + n7 * 4 + n8 * 3 + n9 * 2;
            var remainder = sum % 11;
            return remainder < 2 ? 0 : 11 - remainder;
        }

        private int CalculateCPFDigit2(int n1, int n2, int n3, int n4, int n5, int n6, int n7, int n8, int n9, int d1)
        {
            var sum = n1 * 11 + n2 * 10 + n3 * 9 + n4 * 8 + n5 * 7 + n6 * 6 + n7 * 5 + n8 * 4 + n9 * 3 + d1 * 2;
            var remainder = sum % 11;
            return remainder < 2 ? 0 : 11 - remainder;
        }

        // Geradores específicos para campos IDOC/SAP
        private string GenerateCUF(int length, string alignment)
        {
            // Código da UF (2 dígitos) - valores comuns: 35 (SP), 33 (RJ), etc.
            var ufCodes = new[] { 35, 33, 31, 53, 21, 41, 43, 51, 11, 12, 13, 14, 15, 16, 17, 18, 19, 22, 23, 24, 25, 26, 27, 28, 29, 32, 42, 52 };
            var cuf = ufCodes[_random.Next(ufCodes.Length)].ToString().PadLeft(length, '0');
            return FormatField(cuf, length, alignment);
        }

        private string GenerateCNF(int length, string alignment)
        {
            // Código numérico da NF (8 dígitos aleatórios)
            var cnf = _random.Next(10000000, 99999999).ToString();
            return FormatField(cnf, length, alignment);
        }

        private string GenerateMOD(int length, string alignment)
        {
            // Modelo da NF (geralmente "55" para NF-e)
            var mod = "55";
            return FormatField(mod, length, alignment);
        }

        private string GenerateSerie(int length, string alignment)
        {
            // Série da NF (3 dígitos, geralmente entre 1 e 999)
            var serie = _random.Next(1, 999).ToString().PadLeft(length, '0');
            return FormatField(serie, length, alignment);
        }

        private string GenerateNNF(int length, string alignment, int recordIndex)
        {
            // Número da NF (9 dígitos) - sequencial baseado no registro
            var nnf = ((recordIndex + 1) * 1000 + _random.Next(1, 999)).ToString().PadLeft(length, '0');
            return FormatField(nnf, length, alignment);
        }

        private string GenerateIndPag(int length, string alignment)
        {
            // Indicador de pagamento: 0=À vista, 1=A prazo, 2=Outros
            var indPag = _random.Next(0, 3).ToString();
            return FormatField(indPag, length, alignment);
        }

        private string GenerateNatOp(int length, string alignment)
        {
            // Natureza da operação - exemplos comuns
            var natOps = new[]
            {
                "VENDA",
                "VENDA DE MERCADORIA",
                "VENDA DE PRODUCAO DO ESTABELECIMENTO",
                "VENDA DE PRODUTO ADQUIRIDO OU RECEBIDO",
                "DEVOLUCAO",
                "REMESSA PARA CONSERTO",
                "REMESSA EM BONIFICACAO"
            };
            var natOp = natOps[_random.Next(natOps.Length)];
            return FormatField(natOp, length, alignment);
        }
    }
}

