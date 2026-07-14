using System.Xml.Linq;

namespace XslSynth.Excel;

/// <summary>
/// Etapa B2.2: o campo proprietário `dadosAdic/bloco290` — um registro de largura
/// FIXA (~290 chars) que a `Rule_bloco290` monta concatenando ~25 campos do bloco
/// de controle (LINHA000) + a chave de acesso, cada um via `PadRight(campo, ' ', n)`.
///
/// STUB por ora: retorna null (bloco290 não emitido). O acumulador completo é o
/// próximo incremento — precisa reproduzir cada PadRight char a char vs o gabarito.
/// </summary>
public sealed class Bloco290Emitter
{
    public XElement? Emit(SpecModel spec, Action<string>? log = null)
    {
        log?.Invoke("   [B2] bloco290: acumulador de largura fixa — pendente (B2.2).");
        return null;
    }
}
