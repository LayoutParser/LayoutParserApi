using XslSynth.Prompting;

namespace XslSynth.Core.Tests;

/// <summary>
/// Camada 2 (design-dsl-mapper-prompt-ia-2026-08-16.md §5): extração do catálogo de funções
/// a partir de SysMiddle.ConnectUs.Functions.dll. A DLL real (fora deste repositório, disponível
/// só localmente em .claude/tmp/sysmiddle/ nas máquinas onde o dono a disponibilizou) está
/// fortemente ofuscada — confirmado por decompilação com ilspycmd em 2026-08-16: os corpos de
/// `Name`/`OwnerName`/`Execute` decompilam como `return null;` (control-flow flattening + strings
/// criptografadas em runtime). O teste de sucesso abaixo roda só se a DLL existir localmente
/// (pula com mensagem clara nas demais máquinas/CI) e serve para DOCUMENTAR o comportamento
/// degradado esperado (nomes são palpites, não confirmados) — não para provar 100% de cobertura
/// de nomes reais, que é justamente a limitação registrada.
/// </summary>
public sealed class FunctionCatalogTests
{
    // Caminho local conhecido nesta sessão (fora do worktree — .claude/tmp não é versionado/
    // compartilhado entre worktrees). Se ausente, o teste de extração real é pulado.
    private const string LocalDllPath =
        @"C:\Users\elson.lopes\source\repos\LayoutParserApi\.claude\tmp\sysmiddle\SysMiddle.ConnectUs.Functions.dll";

    [Fact]
    public void Dll_inexistente_degrada_para_catalogo_vazio_sem_lancar()
    {
        var logs = new List<string>();
        var catalog = FunctionCatalog.ExtractFromDll(@"C:\caminho\que\nao\existe.dll", logs.Add);

        Assert.Equal(0, catalog.Count);
        Assert.Contains(logs, l => l.Contains("não encontrada", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Lookup_de_funcao_nao_catalogada_retorna_null_para_habilitar_o_fallback_do_prompt()
    {
        var catalog = FunctionCatalog.ExtractFromDll(@"C:\caminho\que\nao\existe.dll");
        Assert.Null(catalog.Lookup("QualquerFuncaoNaoIndexada"));
    }

    [Fact]
    public void Dll_real_extrai_candidatos_com_nome_marcado_como_nao_confiavel()
    {
        if (!File.Exists(LocalDllPath))
            return; // DLL real não disponível nesta máquina/CI — teste documental, pula graciosamente (sem xunit.skippablefact).

        var logs = new List<string>();
        var catalog = FunctionCatalog.ExtractFromDll(LocalDllPath, logs.Add);

        // Limitação confirmada: extrai candidatos (nome de classe), mas nenhum é confiável —
        // o nome REAL usado em F.Nome(...) na DSL só existe depois do cctor ofuscado rodar.
        Assert.True(catalog.Count > 0, "esperava ao menos um tipo *Function reconhecido por reflection metadata");
        Assert.All(catalog.All, e => Assert.False(e.NameIsReliable));
        Assert.All(catalog.All, e => Assert.Equal("object Execute(object[] pars)", e.Signature));

        // ConcatFunction é um dos tipos confirmados por ilspycmd -l c nesta sessão.
        var concat = catalog.Lookup("Concat");
        Assert.NotNull(concat);
        Assert.Contains("ConcatFunction", concat!.FullTypeName);
    }
}
