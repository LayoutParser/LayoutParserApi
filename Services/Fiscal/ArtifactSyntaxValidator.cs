using System.Xml;
using System.Xml.Xsl;

namespace LayoutParserApi.Services.Fiscal
{
    /// <summary>
    /// Validação de sintaxe do artefato editado à mão (issue #381 sub-fase 5c, ADR
    /// <c>adr-edicao-manual-artefato-versionamento-2026-09-10.md</c> §2.6) — gate do
    /// <c>PATCH .../mapping-drafts/{draftId}/artifacts/{engine}</c>. Não valida SEMÂNTICA (se o
    /// XSLT/TCL produz o XML esperado — isso é papel do Fiscal Test Lab), só SINTAXE: o texto é
    /// estruturalmente aceitável para o motor.
    /// </summary>
    public static class ArtifactSyntaxValidator
    {
        /// <summary><c>true</c> se <paramref name="content"/> é sintaticamente aceitável para <paramref name="engine"/>; senão <c>false</c> com <paramref name="error"/> em PT-BR/mensagem técnica do parser.</summary>
        public static bool TryValidate(string engine, string content, out string? error)
        {
            return engine switch
            {
                "xslt" => TryValidateXslt(content, out error),
                "tcl" => TryValidateTcl(content, out error),
                _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Motor não suportado pela validação de sintaxe."),
            };
        }

        /// <summary>
        /// Compila de fato via <see cref="XslCompiledTransform"/> — cobre bem-formação XML E as regras
        /// próprias do XSLT (templates, match, etc.), não só "é XML válido".
        /// </summary>
        private static bool TryValidateXslt(string content, out string? error)
        {
            try
            {
                using var stringReader = new StringReader(content);
                using var xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
                var transform = new XslCompiledTransform();
                // ✅ XsltSettings.Default desabilita script/document() embutido — mesma postura de
                // segurança recomendada pela Microsoft para XSLT de origem não totalmente confiável
                // (aqui: editado por um humano do workspace, ainda assim defesa em profundidade).
                transform.Load(xmlReader, XsltSettings.Default, stylesheetResolver: null);
                error = null;
                return true;
            }
            catch (XsltException ex)
            {
                error = $"XSLT inválido: {ex.Message}";
                return false;
            }
            catch (XmlException ex)
            {
                error = $"XML malformado: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// <b>Heurística, não parser real</b> — não existe parser TCL disponível no .NET. Cobre só:
        /// balanceamento de <c>{}</c>/<c>[]</c> (respeitando aspas e o escape <c>\</c> do TCL) e
        /// ausência de caracteres de controle inválidos (fora tab/CR/LF). Um TCL com chaves balanceadas
        /// ainda pode ser semanticamente inválido — isso não é pego aqui, só no Fiscal Test Lab (que,
        /// para <c>engine=tcl</c>, já não tem runner determinístico — ver <c>MappingCompilationController</c>).
        /// </summary>
        private static bool TryValidateTcl(string content, out string? error)
        {
            foreach (var ch in content)
            {
                if (char.IsControl(ch) && ch is not ('\t' or '\r' or '\n'))
                {
                    error = $"Caractere de controle inválido (0x{(int)ch:X2}) no conteúdo TCL.";
                    return false;
                }
            }

            var braceDepth = 0;
            var bracketDepth = 0;
            var inQuotes = false;
            var escaped = false;

            foreach (var ch in content)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                switch (ch)
                {
                    case '\\':
                        escaped = true;
                        break;
                    case '"':
                        inQuotes = !inQuotes;
                        break;
                    case '{' when !inQuotes:
                        braceDepth++;
                        break;
                    case '}' when !inQuotes:
                        braceDepth--;
                        if (braceDepth < 0)
                        {
                            error = "TCL com \"}\" sem \"{\" correspondente.";
                            return false;
                        }
                        break;
                    case '[' when !inQuotes:
                        bracketDepth++;
                        break;
                    case ']' when !inQuotes:
                        bracketDepth--;
                        if (bracketDepth < 0)
                        {
                            error = "TCL com \"]\" sem \"[\" correspondente.";
                            return false;
                        }
                        break;
                }
            }

            if (braceDepth != 0)
            {
                error = $"TCL com chaves desbalanceadas ({braceDepth} \"{{\" sem fechamento).";
                return false;
            }

            if (bracketDepth != 0)
            {
                error = $"TCL com colchetes desbalanceados ({bracketDepth} \"[\" sem fechamento).";
                return false;
            }

            if (inQuotes)
            {
                error = "TCL com aspas não fechadas.";
                return false;
            }

            error = null;
            return true;
        }
    }
}
