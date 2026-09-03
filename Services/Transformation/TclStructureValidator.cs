using System.Xml.Linq;

using LayoutParserApi.Services.Transformation.Models;

namespace LayoutParserApi.Services.Transformation
{
    /// <summary>
    /// Validação estrutural do TCL (MAP/LINE/FIELD) — extraída do que já existia em
    /// <see cref="ImprovedTclGeneratorService"/> (era privada e específica daquela classe) para ser
    /// reaproveitada também por <see cref="TransformationValidatorService"/> (issue #173), sem
    /// duplicar a lógica nem puxar as dependências pesadas do gerador (TclGeneratorService,
    /// TransformationLearningService, PatternComparisonService) para dentro do validador.
    /// </summary>
    public static class TclStructureValidator
    {
        /// <summary>
        /// Valida a estrutura básica do TCL: elemento raiz MAP, ao menos uma LINE, e cada LINE com
        /// ao menos um FIELD. Também sinaliza FIELDs sem os atributos mínimos (name/start/length)
        /// necessários para o parser posicional, o que antes passava silenciosamente.
        /// </summary>
        public static TransformationCheckResult Validate(string tclContent)
        {
            var result = new TransformationCheckResult { Success = true, Errors = new List<string>() };

            try
            {
                var doc = XDocument.Parse(tclContent);
                var mapElement = doc.Descendants("MAP").FirstOrDefault();

                if (mapElement == null)
                {
                    result.Success = false;
                    result.Errors.Add("Elemento MAP não encontrado");
                    return result;
                }

                var lines = mapElement.Elements("LINE").ToList();
                if (!lines.Any())
                {
                    result.Success = false;
                    result.Errors.Add("Nenhuma linha (LINE) encontrada no TCL");
                    return result;
                }

                // Validar que cada linha tem campos, e que cada campo tem os atributos mínimos
                // exigidos pelo parser posicional (issue #173 — antes só existia a checagem de
                // "tem FIELD?", sem checar os atributos que o parser realmente lê).
                foreach (var line in lines)
                {
                    var lineName = line.Attribute("name")?.Value ?? "(sem nome)";
                    var fields = line.Elements("FIELD").ToList();

                    if (!fields.Any())
                    {
                        result.Success = false;
                        result.Errors.Add($"Linha '{lineName}' não tem campos (FIELD)");
                        continue;
                    }

                    foreach (var field in fields)
                    {
                        var fieldName = field.Attribute("name")?.Value;
                        if (string.IsNullOrWhiteSpace(fieldName))
                        {
                            result.Success = false;
                            result.Errors.Add($"Linha '{lineName}' tem um FIELD sem atributo 'name'");
                        }

                        // "length" é o único atributo posicional exigido pelo parser real
                        // (TransformationPipelineService/TransformationLearningService leem só
                        // "name"+"length" — a posição é cumulativa dentro da LINE, não há atributo
                        // "start"/"offset" no formato real; confirmado contra o fixture de produção
                        // em TransformationPipelineServiceMapFileTests). Sem "length" o parser não
                        // consegue fatiar o TXT de entrada para este campo.
                        if (field.Attribute("length") == null)
                        {
                            result.Success = false;
                            result.Errors.Add($"Campo '{fieldName ?? "(sem nome)"}' na linha '{lineName}' não tem atributo 'length'");
                        }
                    }
                }

                if (result.Success)
                {
                    result.Message = "Estrutura do TCL válida (MAP/LINE/FIELD)";
                    result.Details = $"{lines.Count} linha(s), {lines.Sum(l => l.Elements("FIELD").Count())} campo(s) no total";
                }
                else
                {
                    result.Message = "Estrutura do TCL inválida";
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Erro ao validar estrutura do TCL: {ex.Message}");
                result.Message = "Erro ao validar estrutura do TCL";
            }

            return result;
        }
    }
}
