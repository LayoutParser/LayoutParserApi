namespace XslSynth.Prompting;

/// <summary>
/// Fase A da issue #151 (spike aprovado pelo dono em 2026-09-07, design em
/// docs/architecture/design-reconstrucao-reversa-xml-txt-2026-09-03.md §4.1): curadoria MANUAL
/// de reversibilidade por função <c>F.*</c>, chaveada pelo nome de CLASSE real (ex.:
/// <c>"ConcatFunction"</c>) — não pelo nome-palpite exposto em <c>FunctionCatalogEntry.Name</c>,
/// que pode divergir do nome real usado na DSL (ver limitação de ofuscação em
/// <see cref="FunctionCatalog"/>). O nome de classe É extraível com segurança via reflection
/// metadata, então é a chave estável disponível sem executar código.
///
/// Por que curadoria manual e não inferência automática: a DLL Sysmiddle real usa control-flow
/// flattening + strings criptografadas em runtime — não dá pra confirmar por reflection se uma
/// função é bijetora (ex.: <c>CalculateVerifierDigitFunction</c> perde o valor original) ou
/// tem efeito colateral (I/O, banco, e-mail) sem ler o nome da classe e julgar a semântica
/// provável. Cada entrada abaixo foi revisada individualmente contra a lista real de 167 classes
/// <c>*Function</c> extraídas de <c>tools/LowCodeRunner/Functions/SysMiddle.ConnectUs.Functions.dll</c>
/// (mesma DLL usada pelo runner LowCode em produção, versionada no repo).
///
/// Critério de bijetividade usado (design §3): "dado o resultado, dá pra recuperar exatamente a
/// entrada original sem informação adicional?". Funções com efeito colateral (I/O, banco, e-mail,
/// ambiente de execução) são marcadas <c>false</c> por natureza — não são transformação pura de
/// campo, então "reversão" nem se aplica; sinalizadas como tal no motivo, não confundidas com
/// perda de dado real.
/// </summary>
public static class FunctionReversibilityCatalog
{
    /// <summary>Motivo padrão para qualquer classe *Function real que exista na DLL mas não tenha
    /// entrada curada aqui (não deveria acontecer para as 167 conhecidas nesta revisão — sinaliza
    /// função nova/desconhecida introduzida em versão futura da DLL).</summary>
    public const string NaoCuradaReason = "Função sem curadoria manual de reversibilidade (Fase A da issue #151).";

    private static readonly Dictionary<string, (bool Reversible, string? Reason)> ByClassName =
        new(StringComparer.Ordinal)
        {
            // ---- Reversível = true: bijetoras / sem perda de informação -----------------------
            ["ConvertToCharFunction"] = (true, "Extrai 1 caractere de string — bijeção trivial sem perda."),
            ["TrueFunction"] = (true, "Função constante, sem origem de dado — não há perda a reverter (fora do escopo de reversão de campo)."),
            ["FalseFunction"] = (true, "Função constante, sem origem de dado — não há perda a reverter (fora do escopo de reversão de campo)."),
            ["PadLeftFunction"] = (true, "Padding com caractere/largura fixos conhecidos do layout — removível de forma determinística (mesmo exemplo citado no design §3)."),
            ["PadRightFunction"] = (true, "Padding com caractere/largura fixos conhecidos do layout — removível de forma determinística (mesmo exemplo citado no design §3)."),
            ["TrimFunction"] = (true, "Remove espaços de padding — recuperável se a largura fixa original do campo for conhecida pelo layout (mesma ressalva de PadLeft/PadRight)."),
            ["TrimStartFunction"] = (true, "Remove espaços de padding à esquerda — recuperável se a largura fixa original do campo for conhecida pelo layout."),
            ["TrimEndFunction"] = (true, "Remove espaços de padding à direita — recuperável se a largura fixa original do campo for conhecida pelo layout."),
            ["SplitValueByPositionFunction"] = (true, "Corte posicional por índice/largura conhecidos — mesma lógica estrutural do parsing por posição fixa (OccurrenceResolver), desde que a largura total seja conhecida."),
            ["GetSplittedValueByPositionFunction"] = (true, "Corte posicional por índice/largura conhecidos — mesma lógica estrutural do parsing por posição fixa, desde que a largura total seja conhecida."),
            ["GetBrazilianStateCodeByInitialsFunction"] = (true, "Mapeamento tabular bijetor (UF ↔ código IBGE) — requer tabela de lookup reversa, mas sem perda de informação."),
            ["GetBrazilianStateCodeByNameFunction"] = (true, "Mapeamento tabular bijetor (nome do estado ↔ código IBGE) — requer tabela de lookup reversa, mas sem perda de informação."),
            ["GetBrazilianCityCodeFunction"] = (true, "Mapeamento tabular bijetor (município+UF ↔ código IBGE) — requer tabela de lookup reversa, mas sem perda de informação."),
            ["ConvertToBase64StringFunction"] = (true, "Codificação Base64 é bijetora e sem perda."),
            ["ConvertToBase64BytesFunction"] = (true, "Codificação Base64 é bijetora e sem perda."),
            ["ConvertFromBase64StringFunction"] = (true, "Decodificação Base64 é bijetora e sem perda (inversa de ConvertToBase64String)."),
            ["ConvertFromBase64BytesFunction"] = (true, "Decodificação Base64 é bijetora e sem perda (inversa de ConvertToBase64Bytes)."),
            ["UriEscapeFunction"] = (true, "Percent-encoding de URI é bijetor — round-trip determinístico com UriUnescape."),
            ["UriUnescapeFunction"] = (true, "Percent-encoding de URI é bijetor — round-trip determinístico com UriEscape."),
            ["CDataStringFunction"] = (true, "Envolve o valor em CDATA sem alterar o conteúdo interno."),
            ["DecompressBytesFunction"] = (true, "Compressão sem perda (lossless) é bijetora, desde que o mesmo algoritmo seja usado no sentido inverso."),
            ["GetBytesFunction"] = (true, "Conversão string→bytes por encoding conhecido (ex.: UTF-8) é bijetora para texto bem-formado (round-trip com GetStringFromBytes)."),
            ["GetStringFromBytesFunction"] = (true, "Conversão bytes→string por encoding conhecido é bijetora para texto bem-formado (round-trip com GetBytes)."),

            // ---- Reversível = false: perda de informação, ambiguidade ou efeito colateral -----
            ["CalculateVerifierDigitFunction"] = (false, "Dígito verificador — função com perda clássica, não recupera o valor original (citada no critério de aceite da issue #151)."),
            ["ConcatFunction"] = (false, "Concatenação perde a fronteira entre segmentos sem delimitador/tamanho conhecido (N:1 ambíguo — ver MappingKind.Concatenated)."),
            ["CreateNFeAcessKeyFunction"] = (false, "Chave de acesso combina múltiplos campos + dígito verificador — não reversível campo a campo sem heurística adicional."),
            ["CreateCTeAcessKeyFunction"] = (false, "Chave de acesso combina múltiplos campos + dígito verificador — não reversível campo a campo sem heurística adicional."),
            ["ConvertToBooleanFunction"] = (false, "Conversão de tipo/formato colapsa múltiplas representações de entrada ('Sim'/'1'/'true') em 2 saídas — não bijetora."),
            ["ConvertToDateFormatFunction"] = (false, "Conversão de formato de data pode perder século/timezone/formato original sem garantia de round-trip exato."),
            ["ConvertToDateTimeFunction"] = (false, "Conversão de tipo perde a formatação/string original (não garante round-trip byte-a-byte)."),
            ["ConvertToDateTimeSpecificFormatFunction"] = (false, "Conversão de formato de data pode perder século/timezone/formato original sem garantia de round-trip exato."),
            ["ConvertDateTimeToTimeZoneFunction"] = (false, "Conversão de fuso horário pode perder o offset original sem metadado adicional."),
            ["ConvertDateTimeToUtcFunction"] = (false, "Conversão para UTC perde o fuso horário original."),
            ["ConvertToDecimalFunction"] = (false, "Conversão de tipo perde a formatação original (zeros à esquerda/à direita, separador) sem garantia de round-trip exato."),
            ["ConvertToInt16Function"] = (false, "Conversão de tipo perde zeros à esquerda e formatação original da string de origem."),
            ["ConvertToInt32Function"] = (false, "Conversão de tipo perde zeros à esquerda e formatação original da string de origem."),
            ["ConvertToInt64Function"] = (false, "Conversão de tipo perde zeros à esquerda e formatação original da string de origem."),
            ["ConvertToStringFunction"] = (false, "Conversão de tipo para string pode não preservar a formatação original do valor de origem (casas decimais, zeros)."),
            ["ToLowerFunction"] = (false, "Perde o caso (maiúsculas/minúsculas) original das letras."),
            ["ToUpperFunction"] = (false, "Perde o caso (maiúsculas/minúsculas) original das letras."),
            ["RemoveNonAlphanumericCharactersFunction"] = (false, "Remove caracteres do valor original — não recuperável sem saber exatamente quais/quantos foram removidos."),
            ["RemoveSpecialCharactersFunction"] = (false, "Remove caracteres do valor original — não recuperável sem saber exatamente quais/quantos foram removidos."),
            ["RemoveZerosLeftFunction"] = (false, "Remove zeros à esquerda — não recuperável sem saber a largura fixa original do campo."),
            ["SubstringFunction"] = (false, "Extrai parte da string e descarta o restante — não recuperável isoladamente."),
            ["SplitFunction"] = (false, "Divide por delimitador — delimitador pode não ser único/preservado na saída, ambíguo de reverter."),
            ["JoinFunction"] = (false, "Concatenação com delimitador — mesmo problema de ambiguidade N:1 de ConcatString se o delimitador aparecer dentro de um segmento."),
            ["RegexFunction"] = (false, "Extração/transformação via regex geralmente descarta parte do texto original — não bijetora em geral."),
            ["RegexUnescapeFunction"] = (false, "Unescape de regex pode não ser estritamente bijetor sem confirmar o padrão de escape usado."),
            ["AdjustJsonEscapeCharactersFunction"] = (false, "Escaping ad-hoc — não confirmado como estritamente bijetor sem revisão do algoritmo real."),
            ["XMLFormatFunction"] = (false, "Formatação para XML pode normalizar espaços/entidades, alterando a representação textual original."),
            ["DecryptRijndaelFunction"] = (false, "Operação criptográfica — reversão exige a mesma chave/IV usada na cifragem, fora do escopo de metadado estrutural."),
            ["GetHashCodeFunction"] = (false, "Hash é função de mão única por definição — nunca reversível."),
            ["GetMD5HashCodeFunction"] = (false, "Hash é função de mão única por definição — nunca reversível."),
            ["GetMD5HashCodeByFileFunction"] = (false, "Hash é função de mão única por definição — nunca reversível."),
            ["GetSignatureFunction"] = (false, "Assinatura digital depende de chave privada — não reversível sem ela."),
            ["NewGuidFunction"] = (false, "Gera valor aleatório novo — não deriva de nenhuma origem, não há o que reverter."),
            ["StringFormatFunction"] = (false, "Formatação pode adicionar/remover caracteres (separadores, zeros) sem preservar o formato de entrada original."),
            ["FormaterNumberFunction"] = (false, "Formatação numérica pode adicionar/remover caracteres (separadores, zeros) sem preservar o formato original."),
            ["DecimalFormaterFunction"] = (false, "Formatação decimal pode adicionar/remover caracteres (separadores, zeros) sem preservar o formato original."),
            ["DivisionFunction"] = (false, "Operação aritmética com possível perda de precisão/arredondamento — não recupera os operandos originais (N:1)."),
            ["MultFunction"] = (false, "Operação aritmética — não recupera os operandos originais (N:1)."),
            ["SubtractFunction"] = (false, "Operação aritmética — não recupera os operandos originais (N:1)."),
            ["SumFunction"] = (false, "Operação aritmética — não recupera os operandos originais (N:1)."),
            ["RoundFunction"] = (false, "Arredondamento descarta as casas decimais originais — perda de precisão."),
            ["GetDateFunction"] = (false, "Extrai um componente da data — descarta os demais componentes (N:1, não bijetor)."),
            ["GetDayFunction"] = (false, "Extrai um componente da data — descarta os demais componentes (N:1, não bijetor)."),
            ["GetHourFunction"] = (false, "Extrai um componente da hora — descarta os demais componentes (N:1, não bijetor)."),
            ["GetMinuteFunction"] = (false, "Extrai um componente da hora — descarta os demais componentes (N:1, não bijetor)."),
            ["GetMonthFunction"] = (false, "Extrai um componente da data — descarta os demais componentes (N:1, não bijetor)."),
            ["GetSecondFunction"] = (false, "Extrai um componente da hora — descarta os demais componentes (N:1, não bijetor)."),
            ["GetYearFunction"] = (false, "Extrai um componente da data — descarta os demais componentes (N:1, não bijetor)."),
            ["GetDateFormatFunction"] = (false, "Extrai/reformata data — não garante round-trip exato do valor original."),
            ["GetDateTimeFunction"] = (false, "Extrai/reformata data e hora — não garante round-trip exato do valor original."),
            ["GetDateTimeFormatFunction"] = (false, "Extrai/reformata data e hora — não garante round-trip exato do valor original."),
            ["GetTimeZoneDifferenceFunction"] = (false, "Deriva uma diferença de fuso — não recupera os dois valores de origem (N:1)."),
            ["GetUnixTimeStampFunction"] = (false, "Conversão para timestamp Unix pode perder timezone/formato original da string de data."),
            ["IsNullOrEmptyFunction"] = (false, "Retorna booleano de teste — colapsa infinitos valores de entrada em 2 saídas, não bijetor."),
            ["IsNullOrWhiteSpaceFunction"] = (false, "Retorna booleano de teste — colapsa infinitos valores de entrada em 2 saídas, não bijetor."),
            ["StartsWithFunction"] = (false, "Retorna booleano de teste — colapsa infinitos valores de entrada em 2 saídas, não bijetor."),
            ["EndsWithFunction"] = (false, "Retorna booleano de teste — colapsa infinitos valores de entrada em 2 saídas, não bijetor."),
            ["EqualsFunction"] = (false, "Retorna booleano de comparação — colapsa infinitos pares de entrada em 2 saídas, não bijetor."),
            ["CoalesceFunction"] = (false, "Retorna o primeiro valor não-nulo entre alternativas — não é possível saber qual delas 'venceu' a partir só do resultado."),
            ["LastIndexOfFunction"] = (false, "Retorna metadado derivado (índice), não o valor em si — não recupera a string original."),
            ["GetLengthFunction"] = (false, "Retorna metadado derivado (tamanho), não o valor em si — não recupera a string original."),
            ["ListContainsFunction"] = (false, "Retorna booleano de teste sobre uma coleção — não bijetor."),
            ["DictionaryContainsKeyFunction"] = (false, "Retorna booleano de teste sobre uma coleção — não bijetor."),
            ["DictionaryContainsValueFunction"] = (false, "Retorna booleano de teste sobre uma coleção — não bijetor."),
            ["JObjectFindValuesFunction"] = (false, "Busca em JSON pode retornar múltiplos valores/perder estrutura — não garante round-trip."),
            ["JObjectGetValueFunction"] = (false, "Extrai um valor de dentro de uma estrutura JSON maior — descarta o restante (N:1)."),
            ["JObjectParseFunction"] = (false, "Parse de JSON pode reordenar chaves/normalizar formatação — não garante round-trip byte-a-byte."),
            ["JObjectUpdateValueFunction"] = (false, "Atualiza um valor dentro de uma estrutura maior — resultado depende de estado externo (o objeto sendo atualizado)."),
            ["JSONDeserializeDataTableFunction"] = (false, "Deserialização pode não preservar a formatação textual original do JSON."),
            ["JSONNullFunction"] = (false, "Retorna constante JSON null — não deriva de um campo de origem."),
            ["JSONSerializeFunction"] = (false, "Serialização JSON pode reordenar chaves/normalizar formatação — não garante round-trip byte-a-byte."),
            ["NewLineFunction"] = (false, "Retorna constante — não deriva de um campo de origem."),
            ["NewStringBuilderFunction"] = (false, "Cria estrutura nova — não deriva de um campo de origem existente."),
            ["NewDictionaryFunction"] = (false, "Cria estrutura nova — não deriva de um campo de origem existente."),
            ["NewListFunction"] = (false, "Cria estrutura nova — não deriva de um campo de origem existente."),
            ["CreateFunction"] = (false, "Cria estrutura nova — não deriva de um campo de origem existente."),
            ["CreateDataTableFunction"] = (false, "Cria estrutura nova — não deriva de um campo de origem existente."),
            ["AddDateTimeValueFunction"] = (false, "Soma um intervalo a uma data — não determinística de reverter sem saber o operando somado (N:1)."),
            ["AddDataTableColumnFunction"] = (false, "Efeito de estrutura de tabela em memória — não é uma transformação pura de campo."),
            ["DataTableColumnLastValueFunction"] = (false, "Depende de estado acumulado em uma tabela em memória — não deriva de um único campo de origem."),
            ["DictionaryAddUpdateFunction"] = (false, "Efeito sobre estrutura em memória (dicionário) — não é transformação pura de campo."),
            ["DictionaryClearFunction"] = (false, "Efeito sobre estrutura em memória — não é transformação pura de campo."),
            ["DictionaryGetValueFunction"] = (false, "Extrai um valor de uma estrutura maior — descarta o restante (N:1)."),
            ["DictionaryRemoveFunction"] = (false, "Efeito sobre estrutura em memória — não é transformação pura de campo."),
            ["ListAddFunction"] = (false, "Efeito sobre estrutura em memória (lista) — não é transformação pura de campo."),
            ["ListClearFunction"] = (false, "Efeito sobre estrutura em memória — não é transformação pura de campo."),
            ["ListGetItemFunction"] = (false, "Extrai um item de uma coleção maior — descarta o restante (N:1)."),
            ["ListIndexOfFunction"] = (false, "Retorna metadado derivado (índice) — não recupera o valor original."),
            ["ListRemoveFunction"] = (false, "Efeito sobre estrutura em memória — não é transformação pura de campo."),
            ["ListRemoveAtFunction"] = (false, "Efeito sobre estrutura em memória — não é transformação pura de campo."),
            ["ListUpdateFunction"] = (false, "Efeito sobre estrutura em memória — não é transformação pura de campo."),
            ["StringBuilderAppendFunction"] = (false, "Efeito acumulado sobre estrutura mutável — resultado depende de estado externo (o builder)."),
            ["StringBuilderAppendFormatFunction"] = (false, "Efeito acumulado sobre estrutura mutável — resultado depende de estado externo (o builder)."),
            ["StringBuilderAppendLineFunction"] = (false, "Efeito acumulado sobre estrutura mutável — resultado depende de estado externo (o builder)."),
            ["StringBuilderClearFunction"] = (false, "Efeito sobre estrutura mutável — não é transformação pura de campo."),
            ["StringBuilderInsertFunction"] = (false, "Efeito acumulado sobre estrutura mutável — resultado depende de estado externo (o builder)."),
            ["StringBuilderLengthFunction"] = (false, "Retorna metadado derivado (tamanho) — não recupera o valor original."),
            ["StringBuilderRemoveFunction"] = (false, "Efeito sobre estrutura mutável — não é transformação pura de campo."),
            ["StringBuilderReplaceFunction"] = (false, "Substituição de texto perde a distinção entre valor original e substituído sem conhecer o padrão exato."),
            ["LoadDataTableFromCSVFunction"] = (false, "Depende de arquivo externo — resultado não é derivável do campo de origem isoladamente."),
            ["PostgreSQLBulkCopyFunction"] = (false, "Acesso a banco externo — efeito colateral, não transformação pura de campo."),
            ["SQLServerBulkCopyFunction"] = (false, "Acesso a banco externo — efeito colateral, não transformação pura de campo."),
            ["ExecuteCommandFunction"] = (false, "Executa comando externo — efeito colateral, não transformação pura de campo."),
            ["ExecuteDictionarySelectFunction"] = (false, "Acessa banco externo — resultado depende de estado externo, não derivável do campo de origem isoladamente."),
            ["ExecuteInsertUpdateOrDeleteFunction"] = (false, "Efeito colateral em banco externo — não é transformação pura de campo."),
            ["ExecuteListDictionarySelectFunction"] = (false, "Acessa banco externo — resultado depende de estado externo, não derivável do campo de origem isoladamente."),
            ["ExecuteMapperFunction"] = (false, "Delega a outro mapeador — reversão dependeria de reverter o mapeador delegado (fora de escopo desta curadoria)."),
            ["ExecuteSelectFunction"] = (false, "Acessa banco externo — resultado depende de estado externo, não derivável do campo de origem isoladamente."),
            ["ExecuteSelectDataTableFunction"] = (false, "Acessa banco externo — resultado depende de estado externo, não derivável do campo de origem isoladamente."),
            ["ExecuteSelectReaderFunction"] = (false, "Acessa banco externo — resultado depende de estado externo, não derivável do campo de origem isoladamente."),
            ["GetValueFromDBFunction"] = (false, "Acessa banco externo — resultado depende de estado externo, não derivável do campo de origem isoladamente."),
            ["GetDBInstanceFunction"] = (false, "Retorna referência de infraestrutura — não é transformação de campo."),
            ["GetValueByXPathFunction"] = (false, "Extrai valor de um documento XML maior — depende do documento inteiro, não só do campo de origem (N:1)."),
            ["ReadFileFunction"] = (false, "I/O de arquivo — não é transformação pura de campo."),
            ["ReadFileBytesFunction"] = (false, "I/O de arquivo — não é transformação pura de campo."),
            ["ReadFileLineFunction"] = (false, "I/O de arquivo — não é transformação pura de campo."),
            ["WriteFileFunction"] = (false, "I/O de arquivo — efeito colateral, não transformação pura de campo."),
            ["AppendFileFunction"] = (false, "I/O de arquivo — efeito colateral, não transformação pura de campo."),
            ["CopyFileFunction"] = (false, "I/O de arquivo — efeito colateral, não transformação pura de campo."),
            ["DeleteFileFunction"] = (false, "I/O de arquivo — efeito colateral, não transformação pura de campo."),
            ["GetFilesFunction"] = (false, "I/O de arquivo — depende do estado do sistema de arquivos, não do campo de origem."),
            ["GetFileNameFunction"] = (false, "Extrai parte de um caminho de arquivo — descarta o restante (N:1)."),
            ["GetFileSizeFunction"] = (false, "Retorna metadado derivado (tamanho) — não recupera o conteúdo do arquivo."),
            ["GetFileLinesNumberFunction"] = (false, "Retorna metadado derivado (contagem) — não recupera o conteúdo do arquivo."),
            ["GetParentPathFunction"] = (false, "Extrai parte de um caminho — descarta o restante (N:1)."),
            ["WebRequestFunction"] = (false, "Chamada de rede — não determinística/dependente de estado externo."),
            ["WebRequestResponseBytesFunction"] = (false, "Chamada de rede — não determinística/dependente de estado externo."),
            ["SendEmailFunction"] = (false, "Efeito colateral (envio de e-mail) — não produz um campo de dado a reverter."),
            ["SendEmailByConfigurationFunction"] = (false, "Efeito colateral (envio de e-mail) — não produz um campo de dado a reverter."),
            ["SetErrorMessageFunction"] = (false, "Efeito colateral (mensagem de erro) — não produz um campo de dado a reverter."),
            ["LogFunction"] = (false, "Efeito colateral (log) — não produz um campo de dado a reverter."),
            ["SleepFunction"] = (false, "Efeito de temporização — não produz um campo de dado a reverter."),
            ["KillFunction"] = (false, "Efeito colateral (encerra processo) — não produz um campo de dado a reverter."),
            ["StartProcessFunction"] = (false, "Efeito colateral (inicia processo) — não produz um campo de dado a reverter."),
            ["RestartServiceFunction"] = (false, "Efeito colateral (reinicia serviço) — não produz um campo de dado a reverter."),
            ["GetCurrentExecutionFunction"] = (false, "Consulta de ambiente de execução — não é transformação de campo."),
            ["GetServerEmailFunction"] = (false, "Consulta de configuração de ambiente — não deriva de um campo de origem."),
            ["GetAppSettingFunction"] = (false, "Consulta de configuração de ambiente — não deriva de um campo de origem."),
            ["WriteAppSettingFunction"] = (false, "Efeito colateral (grava configuração) — não produz um campo de dado a reverter."),
            ["ConnectionStringNameFunction"] = (false, "Consulta de configuração de ambiente — não deriva de um campo de origem."),
            ["WriteConnectionStringFunction"] = (false, "Efeito colateral (grava configuração) — não produz um campo de dado a reverter."),
            ["GetWindowsServiceNameFunction"] = (false, "Consulta de ambiente de execução — não deriva de um campo de origem."),
            ["GetMachineNameFunction"] = (false, "Consulta de ambiente de execução — não deriva de um campo de origem."),
            ["GetProcessNameFunction"] = (false, "Consulta de ambiente de execução — não deriva de um campo de origem."),

            // ---- Complemento de curadoria (2026-09-07): 8 classes reais encontradas só pela
            // extração reflection-only (RemoveFunction/ReplaceFunction/InsertFunction/IndexOfFunction/
            // ContainsFunction/NullFunction/SendEmail/ConnectionStringFunction têm nome de classe
            // diferente do padrão "<Verbo><Coisa>Function" ou ficam em namespaces menos óbvios —
            // não apareceram na varredura textual inicial por strings, só na extração real via DLL. ----
            ["InsertFunction"] = (false, "Insere caractere(s) em posição da string — não recuperável sem saber exatamente o que foi inserido e onde."),
            ["RemoveFunction"] = (false, "Remove caractere(s) da string — não recuperável sem saber exatamente o que foi removido e onde."),
            ["ReplaceFunction"] = (false, "Substituição de texto perde a distinção entre valor original e substituído sem conhecer o padrão exato."),
            ["IndexOfFunction"] = (false, "Retorna metadado derivado (índice) — não recupera a string original."),
            ["ContainsFunction"] = (false, "Retorna booleano de teste — colapsa infinitos valores de entrada em 2 saídas, não bijetor."),
            ["NullFunction"] = (false, "Retorna constante null — não deriva de um campo de origem."),
            ["SendEmail"] = (false, "Efeito colateral (envio de e-mail) — não produz um campo de dado a reverter."),
            ["ConnectionStringFunction"] = (false, "Consulta de configuração de ambiente — não deriva de um campo de origem."),
        };

    /// <summary>Busca a curadoria pelo nome de CLASSE real (ex.: <c>"ConcatFunction"</c>) — não pelo
    /// nome-palpite pós-<c>GuessDslName</c>. Retorna o default conservador (<c>false</c>,
    /// <see cref="NaoCuradaReason"/>) quando a classe não foi revisada nesta curadoria.</summary>
    public static (bool Reversible, string? Reason) Lookup(string className) =>
        ByClassName.TryGetValue(className, out var entry) ? entry : (false, NaoCuradaReason);

    /// <summary>Quantas classes foram efetivamente curadas (para diagnósticos/relatório do spike) —
    /// não confundir com o total de funções existentes na DLL (<see cref="FunctionCatalog.Count"/>).</summary>
    public static int CuratedCount => ByClassName.Count;
}
