using System.Xml;
using System.Xml.Schema;

namespace XslSynth.Core;

/// <summary>Resultado da validação contra XSD.</summary>
public sealed record XsdResult(bool IsValid, IReadOnlyList<string> Errors);

/// <summary>
/// Passo 5 do loop (código): valida o XML de saída contra o XSD.
/// No demo, um XSD pequeno embutido. Em produção, os XSDs de NF-e/CT-e/NFCom/MDFe
/// já configurados (XsdValidationService / seção XsdValidation).
/// </summary>
public sealed class XsdValidator
{
    public XsdResult Validate(string xml, string xsdPath)
    {
        var errors = new List<string>();

        try
        {
            var schemas = new XmlSchemaSet();
            // targetNamespace nulo: XSD do demo não tem namespace (como o gabarito).
            schemas.Add(targetNamespace: null, schemaUri: xsdPath);

            var settings = new XmlReaderSettings
            {
                ValidationType = ValidationType.Schema,
                Schemas = schemas
            };
            settings.ValidationEventHandler += (_, e) =>
                errors.Add($"{e.Severity}: {e.Message}");

            using var reader = XmlReader.Create(new StringReader(xml), settings);
            while (reader.Read()) { /* dispara os eventos de validação */ }
        }
        catch (XmlException ex)
        {
            errors.Add($"XML malformado: {ex.Message}");
        }
        catch (XmlSchemaException ex)
        {
            errors.Add($"XSD inválido: {ex.Message}");
        }

        return new XsdResult(errors.Count == 0, errors);
    }
}
