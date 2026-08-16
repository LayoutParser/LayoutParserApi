using XslSynth.Core;
using XslSynth.Model;
using XslSynth.Synthesis;

namespace XslSynth.Core.Tests;

/// <summary>
/// Extensão do FewShotIndex (design-dsl-mapper-prompt-ia-2026-08-16.md §2, "Extensão do
/// FewShotIndex"): recuperação por JSON estruturado (<see cref="FewShotIndex.RetrieveStructured"/>)
/// em vez de regex sobre a DSL bruta. Usa <see cref="FewShotIndex.AddMapper"/> (mecanismo já
/// existente) para indexar um MapperVO sintético com 2 regras de "família" diferente e confirma
/// que a consulta estruturada recupera a mais parecida, não a mais distante.
/// </summary>
public sealed class FewShotIndexStructuredTests
{
    private static MapperRule Rule(string guid, string dsl) => new() { ElementGuid = guid, ContentValue = dsl };

    [Fact]
    public void RetrieveStructured_prioriza_exemplo_com_mesmas_funcoes_e_forma_de_ramos()
    {
        // "Alvo": regra parecida com a chave de acesso (2 ramos, ConcatString + CalculateVerifierDigit).
        const string alvoSimilarDsl = @"%beginRuleContent;
#.campo = I.LINHA000/OutroCampo;
#.tam = GetLength(#.campo);
if(#.tam = 10)
begin
	T.a/b/c = ConcatString('X',#.campo);
end
else
begin
	#.d = ConcatString(#.campo,CalculateVerifierDigit(#.campo));
	T.a/b/c = #.d;
end
%endRuleContent;";

        // "Distante": regra guarded-emit simples, sem nada em comum (funções e forma diferentes).
        const string distanteDsl = @"%beginRuleContent;
#.vBC = I.LINHA050/BaseDeCalculoDoICMS;
if(IsNullOrEmpty(#.vBC) != True())
begin
	T.enviNFe/NFe/infNFe/total/ICMSTot/vBC = FormaterDecimal(#.vBC,2);
end
%endRuleContent;";

        var mapper = new MapperVo { Name = "Mapper_NFE" };
        mapper.Rules.Add(Rule("RUL_similar", alvoSimilarDsl));
        mapper.Rules.Add(Rule("RUL_distante", distanteDsl));

        var index = FewShotIndex.Build(Directory.CreateTempSubdirectory().FullName); // corpus vazio, só a estrutura
        index.AddMapper(mapper, "teste.decrypted.xml");

        // Consulta: a própria regra da chave de acesso real (2 ramos, ConcatString + CalculateVerifierDigit no 2º).
        var queryRule = Rule("RUL_a7c1ce5b-consulta", @"%beginRuleContent;
#.campoChaveAcesso = I.LINHA000/ChaveAcesso;
#.tamanhoChaveAcesso = GetLength(#.campoChaveAcesso);
if(#.tamanhoChaveAcesso = 44)
begin
	T.enviNFe/NFe/infNFe/Id = ConcatString('NFe',#.campoChaveAcesso);
end
else
begin
	T.enviNFe/NFe/infNFe/Id = ConcatString(#.campoChaveAcesso,CalculateVerifierDigit(#.campoChaveAcesso));
end
%endRuleContent;");
        var query = new DslStructuredParser().Parse(queryRule);

        var results = index.RetrieveStructured(query, k: 1);

        var top = Assert.Single(results);
        Assert.NotNull(top.Structured);
        Assert.Contains("CalculateVerifierDigit", top.Structured!.AllFunctions);
    }

    [Fact]
    public void RetrieveStructured_com_k_zero_nao_retorna_nada()
    {
        var index = FewShotIndex.Build(Directory.CreateTempSubdirectory().FullName);
        var query = new DslStructuredParser().Parse(Rule("x", "%beginRuleContent;\nT.a/b = 'x';\n%endRuleContent;"));
        Assert.Empty(index.RetrieveStructured(query, k: 0));
    }
}
