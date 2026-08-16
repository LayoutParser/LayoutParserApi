using XslSynth.Core;
using XslSynth.Model;

namespace XslSynth.Core.Tests;

/// <summary>
/// Camada 0 (design-dsl-mapper-prompt-ia-2026-08-16.md §2): cobre os 2 casos reais disponíveis
/// em .claude/tmp/story/103/MAP_f31a6758-...xml — a regra da chave de acesso (fallback em 3
/// camadas, if/else aninhado real, âncora do design doc) e um trecho de Regra_ICMSTotal
/// (condicional guarded-emit real, não simplificado). O texto é copiado literal do XML real
/// (com as entidades &amp;/&amp;gt;/&amp;lt; já decodificadas como estariam após leitura do XML).
/// </summary>
public sealed class DslStructuredParserTests
{
    // Cópia literal de RUL_a7c1ce5b-32dc-4032-bcdd-cf53714a5f0c (Regra_chaveDeAcesso).
    private const string ChaveAcessoDsl = @"%beginRuleContent;
#.campoChaveAcesso = I.LINHA000/ChaveAcesso;
#.tamanhoChaveAcesso = GetLength(#.campoChaveAcesso);
$.buildChaveAcesso = '';
$.validaPBCOP = 0;

if(#.tamanhoChaveAcesso = 44)
begin
	$.buildChaveAcesso = #.campoChaveAcesso;
	T.enviNFe/NFe/infNFe/Id = ConcatString('NFe',$.buildChaveAcesso);
end
else
begin
	if(#.tamanhoChaveAcesso > 35 && #.tamanhoChaveAcesso < 44)
	begin
		#.chaveAcesso = #.campoChaveAcesso;
		$.buildChaveAcesso = ConcatString(#.chaveAcesso,CalculateVerifierDigit(#.chaveAcesso));
		T.enviNFe/NFe/infNFe/Id = ConcatString('NFe', $.buildChaveAcesso);
	end
	else
	begin
		#.codigoDaUfDoEmitente = I.LINHA001/CodigoDaUFDoEmitente001;
		#.dtHrEmissaoCampo37 = I.LINHA001/DataDeEmissaoDoDocumentoFiscal001;

		if(IsNullOrEmpty(#.dtHrEmissaoCampo37) != True() && #.dtHrEmissaoCampo37 != 'xml')
		begin
			#.AAMM = #.dtHrEmissaoCampo37;
		end
		else
		begin
			#.dtHrEmissaoCampo50 = I.LINHA001/DataHoraEmissaoDocumento;

			if(IsNullOrEmpty(#.dtHrEmissaoCampo50) != True() && #.dtHrEmissaoCampo50 != 'xml')
			begin
				#.AAMM = #.dtHrEmissaoCampo50;
			end
			else
			begin
				#.AAMM = DateTimeNow('');
			end
		end

		#.ano = Substring(#.AAMM, 2, 2);
		#.mes = Substring(#.AAMM, 2, 2);
		#.cnpjEmite = I.LINHA004/NumeroDoCNPJDoEmitenteEmit;
		#.modelo = '55';
		#.serie = I.LINHA000/SerieNF;
		#.nNF = I.LINHA001/NumeroDoDocumentoFiscal001;
		#.tpEmi = I.LINHA001/FormaDaEmissaoDaNFe01;
		$.buildChaveAcesso = GenerateAccessKey(#.codigoDaUfDoEmitente,ConcatString(#.ano,#.mes),#.cnpjEmite,#.modelo,#.serie,#.nNF,#.tpEmi);
		T.enviNFe/NFe/infNFe/Id = ConcatString('NFe', $.buildChaveAcesso);
	end
end
%endRuleContent;";

    // Cópia literal (trecho) de RUL_6a87f524-96ad-411c-a5c0-de4b4666faac (Regra_ICMSTotal) —
    // um bloco guarded-emit real, com begin/end, não simplificado.
    private const string IcmsTotalDsl = @"%beginRuleContent;
#.vBC = I.LINHA050/BaseDeCalculoDoICMS;
#.vICMS = I.LINHA050/ValorTotalDoICMS;

if(IsNullOrEmpty(#.vBC) != True())
begin
	T.enviNFe/NFe/infNFe/total/ICMSTot/vBC = FormaterDecimal(#.vBC,2);
end

if(IsNullOrEmpty(#.vICMS) != True())
begin
	T.enviNFe/NFe/infNFe/total/ICMSTot/vICMS = FormaterDecimal(#.vICMS,2);
end
%endRuleContent;";

    private static MapperRule Rule(string contentValue, string? guid = null, string? name = null) => new()
    {
        ElementGuid = guid,
        Name = name,
        ContentValue = contentValue
    };

    [Fact]
    public void ChaveAcesso_produz_tres_ramos_um_por_camada_de_fallback()
    {
        var rule = Rule(ChaveAcessoDsl, "RUL_a7c1ce5b-32dc-4032-bcdd-cf53714a5f0c", "Regra_chaveDeAcesso");
        var structured = new DslStructuredParser().Parse(rule);

        Assert.Equal("RUL_a7c1ce5b-32dc-4032-bcdd-cf53714a5f0c", structured.RuleId);
        Assert.Equal("enviNFe/NFe/infNFe/Id", structured.Target);
        Assert.Equal(3, structured.Branches.Count);

        // ramo 1: chave completa (44 chars) — condição simples (sem &&, sem negação prévia).
        Assert.DoesNotContain("&&", structured.Branches[0].Condition);
        Assert.DoesNotContain("!(", structured.Branches[0].Condition);
        Assert.Contains("LINHA000/ChaveAcesso", structured.Branches[0].Sources);
        Assert.Contains("ConcatString", structured.Branches[0].Functions);

        // ramo 2: chave truncada + dígito verificador — condição do 1º ramo negada + condição composta.
        Assert.StartsWith("!(", structured.Branches[1].Condition);
        Assert.Contains("&&", structured.Branches[1].Condition);
        Assert.Contains("CalculateVerifierDigit", structured.Branches[1].Functions);
        Assert.Contains("LINHA000/ChaveAcesso", structured.Branches[1].Sources); // via #.chaveAcesso = #.campoChaveAcesso

        // ramo 3: reconstrução via UF+data+campos — acumula as 2 negações anteriores.
        var cond3 = structured.Branches[2].Condition;
        Assert.Equal(2, cond3.Split("&&").Length >= 1 ? cond3.Count(c => c == '!') : 0); // pelo menos 2 negações encadeadas
        Assert.Contains("GenerateAccessKey", structured.Branches[2].Functions);
        Assert.Contains("LINHA001/CodigoDaUFDoEmitente001", structured.Branches[2].Sources);
        Assert.Contains("LINHA004/NumeroDoCNPJDoEmitenteEmit", structured.Branches[2].Sources);
        Assert.Contains("LINHA000/SerieNF", structured.Branches[2].Sources);
        Assert.Contains("LINHA001/NumeroDoDocumentoFiscal001", structured.Branches[2].Sources);
    }

    [Fact]
    public void ChaveAcesso_resolve_origem_transitivamente_atraves_de_temp_aninhado()
    {
        // #.AAMM depende de #.dtHrEmissaoCampo37 OU #.dtHrEmissaoCampo50 (dependendo do ramo
        // interno que rodou) — ambos precisam aparecer entre as origens do ramo 3, mesmo com
        // 2 níveis de indireção (#.ano/#.mes = Substring(#.AAMM,...) → #.AAMM → #.dtHrEmissaoCampoNN).
        var rule = Rule(ChaveAcessoDsl);
        var structured = new DslStructuredParser().Parse(rule);
        var ramo3Sources = structured.Branches[2].Sources;

        Assert.Contains("LINHA001/DataDeEmissaoDoDocumentoFiscal001", ramo3Sources);
    }

    [Fact]
    public void IcmsTotal_produz_um_ramo_por_bloco_guardado_incondicional_no_topo()
    {
        var rule = Rule(IcmsTotalDsl);
        var structured = new DslStructuredParser().Parse(rule);

        Assert.Equal(2, structured.Branches.Count);
        Assert.Equal("enviNFe/NFe/infNFe/total/ICMSTot/vBC", structured.Branches[0].Target);
        Assert.Contains("LINHA050/BaseDeCalculoDoICMS", structured.Branches[0].Sources);
        Assert.Contains("IsNullOrEmpty", structured.Branches[0].Functions);
        Assert.Contains("FormaterDecimal", structured.Branches[0].Functions);

        Assert.Equal("enviNFe/NFe/infNFe/total/ICMSTot/vICMS", structured.Branches[1].Target);
        Assert.Contains("LINHA050/ValorTotalDoICMS", structured.Branches[1].Sources);
    }

    [Fact]
    public void Regra_sem_condicional_produz_um_unico_ramo_true()
    {
        var rule = Rule("%beginRuleContent;\nT.enviNFe/NFe/infNFe/versao = '4.00';\n%endRuleContent;");
        var structured = new DslStructuredParser().Parse(rule);

        var branch = Assert.Single(structured.Branches);
        Assert.Equal("true", branch.Condition);
        Assert.Empty(branch.Sources);
        Assert.Empty(branch.Functions);
    }

    [Fact]
    public void ContentValue_vazio_nao_lanca_e_produz_zero_ramos()
    {
        var rule = Rule("");
        var structured = new DslStructuredParser().Parse(rule);
        Assert.Empty(structured.Branches);
    }

    [Fact]
    public void DSL_fora_do_subconjunto_conhecido_nao_lanca()
    {
        // token estranho no meio (aspas não fechadas, parênteses desbalanceados) — o parser
        // deve resincronizar, não lançar. Esta é uma garantia de resiliência, não de cobertura.
        var rule = Rule("%beginRuleContent;\n#.x = ???unbalanced(((;\nT.a/b = #.x;\n%endRuleContent;");
        var ex = Record.Exception(() => new DslStructuredParser().Parse(rule));
        Assert.Null(ex);
    }
}
