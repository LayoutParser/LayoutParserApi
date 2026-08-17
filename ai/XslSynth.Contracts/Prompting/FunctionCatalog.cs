using System.Reflection;

namespace XslSynth.Prompting;

/// <summary>Uma entrada do catálogo de funções <c>F.*</c> (Camada 2, ver §5 do design doc).</summary>
public sealed record FunctionCatalogEntry(
    string Name,
    string FullTypeName,
    string? Namespace,
    /// <summary>Categoria derivada do namespace (ex.: "Manipulations.String.Returns.String").</summary>
    string? Category,
    /// <summary>
    /// Assinatura observável por reflection. Nas DLLs Sysmiddle reais todo <c>FunctionMember</c>
    /// implementa <c>object Execute(object[] pars)</c> — a assinatura tipada real (nomes/tipos dos
    /// parâmetros) só existe dentro do corpo ofuscado de <c>MakeSignature()</c>, inacessível sem
    /// deobfuscation/execução (ver <see cref="ObfuscationNote"/>).
    /// </summary>
    string Signature,
    /// <summary>Se o nome exibido é o nome REAL usado na DSL (<c>F.Nome</c>) ou só um palpite
    /// derivado do nome da classe (<c>ConcatFunction</c> → "Concat"). Nunca confie cegamente.</summary>
    bool NameIsReliable,
    string? ObfuscationNote);

/// <summary>
/// Camada 2 do desenho de contexto/prompt: catálogo indexável (nome→assinatura) das funções
/// <c>F.*</c> usadas na DSL Sysmiddle, extraído OFFLINE e UMA VEZ de
/// <c>SysMiddle.ConnectUs.Functions.dll</c> — consultado por RAG/lookup exato quando um
/// <see cref="XslSynth.Core.DslStructuredParser"/> encontra uma chamada <c>F.NomeDaFuncao</c>
/// (ver design doc §5: lookup por nome exato, não busca fuzzy).
///
/// LIMITAÇÃO REAL DOCUMENTADA (confirmada por decompilação com ilspycmd, 2026-08-16): a DLL
/// real usa control-flow flattening + strings criptografadas em runtime. Toda classe de função
/// (<c>*Function</c> em <c>SysMiddle.ConnectUs.Functions.Manipulations.*</c>) tem:
///   • <c>Name</c>/<c>OwnerName</c> (getters) → corpo decompila como <c>return null;</c> (o valor
///     real só existe depois do cctor ofuscado rodar em runtime) — o nome usado de fato na DSL
///     (ex.: "ConcatString") NÃO é extraível estaticamente com confiança total.
///   • <c>Execute(object[] pars)</c> → assinatura SEMPRE <c>object[]→object</c>; não há tipos de
///     parâmetro/retorno reais visíveis (ficam dentro de <c>MakeSignature()</c>, também ofuscado).
/// O que É extraível com segurança (via reflection metadata, sem executar nenhum código — usa
/// <see cref="MetadataLoadContext"/>, reflection-only): nome de CLASSE + namespace completo +
/// confirmação de que herda de <c>SysMiddle.Base.Function.FunctionMember</c>. Isso já é suficiente
/// para (a) confirmar quais candidatos a função existem, (b) derivar um nome PROVÁVEL (kebab da
/// classe sem o sufixo "Function") a injetar no prompt como "candidato não confirmado", e (c)
/// alimentar o fallback do design doc §5.3 ("função não catalogada — não invente comportamento")
/// com uma lista real de nomes-candidatos em vez de nada.
/// </summary>
public sealed class FunctionCatalog
{
    private readonly Dictionary<string, FunctionCatalogEntry> _byGuessedName;

    private FunctionCatalog(Dictionary<string, FunctionCatalogEntry> byGuessedName) => _byGuessedName = byGuessedName;

    public int Count => _byGuessedName.Count;

    /// <summary>Lookup por nome EXATO (como aparece em <c>F.Nome(...)</c> na DSL) — case-insensitive
    /// pelo nome-palpite derivado da classe, já que o nome real não é extraível estaticamente
    /// (ver limitação na doc da classe). Retorna null se não houver candidato.</summary>
    public FunctionCatalogEntry? Lookup(string dslFunctionName) =>
        _byGuessedName.GetValueOrDefault(dslFunctionName);

    public IReadOnlyCollection<FunctionCatalogEntry> All => _byGuessedName.Values;

    /// <summary>
    /// Extrai o catálogo de uma DLL Sysmiddle real via reflection-only (<see cref="MetadataLoadContext"/>
    /// — NÃO executa nenhum código do assembly, só lê metadata; diferente de decompilar lógica
    /// interna, que o design doc pede para não fazer sem confirmação). Degrada graciosamente: se a
    /// DLL não existir/não carregar, devolve catálogo vazio e loga — nunca lança para o chamador.
    /// </summary>
    public static FunctionCatalog ExtractFromDll(string dllPath, Action<string>? log = null)
    {
        var byGuessedName = new Dictionary<string, FunctionCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(dllPath))
        {
            log?.Invoke($"[function-catalog] DLL não encontrada: {dllPath} — catálogo vazio (degradando).");
            return new FunctionCatalog(byGuessedName);
        }

        try
        {
            var runtimeAssemblies = Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll");
            var resolver = new PathAssemblyResolver(runtimeAssemblies.Append(dllPath));
            using var mlc = new MetadataLoadContext(resolver);

            var asm = mlc.LoadFromAssemblyPath(dllPath);
            var functionMemberFullName = "SysMiddle.Base.Function.FunctionMember";

            foreach (var type in SafeGetTypes(asm, log))
            {
                if (!type.IsPublic || type.IsAbstract) continue;
                if (!InheritsFrom(type, functionMemberFullName)) continue;

                var className = type.Name;
                var guessedName = GuessDslName(className);
                var category = type.Namespace?.Replace("SysMiddle.ConnectUs.Functions.", "", StringComparison.Ordinal);

                var execute = type.GetMethod("Execute", BindingFlags.Public | BindingFlags.Instance
                    | BindingFlags.DeclaredOnly | BindingFlags.FlattenHierarchy);
                var signature = execute is not null
                    ? $"{execute.ReturnType.Name} Execute(object[] pars)"
                    : "object Execute(object[] pars)"; // herdado, não redeclarado neste type

                var entry = new FunctionCatalogEntry(
                    Name: guessedName,
                    FullTypeName: type.FullName ?? className,
                    Namespace: type.Namespace,
                    Category: category,
                    Signature: signature,
                    NameIsReliable: false, // ver nota de ofuscação na doc da classe
                    ObfuscationNote: "Name/OwnerName reais só existem em runtime (cctor ofuscado); " +
                                      "nome aqui é PALPITE a partir do nome da classe.");

                byGuessedName[guessedName] = entry;
            }

            log?.Invoke($"[function-catalog] {byGuessedName.Count} função(ões)-candidata(s) extraída(s) de {Path.GetFileName(dllPath)} " +
                        "(nomes são palpites — ver limitação de ofuscação).");
        }
        catch (Exception ex)
        {
            log?.Invoke($"[function-catalog] falha ao extrair '{dllPath}': {ex.Message} — catálogo vazio (degradando).");
        }

        return new FunctionCatalog(byGuessedName);
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly asm, Action<string>? log)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            log?.Invoke($"[function-catalog] alguns tipos não carregaram ({ex.LoaderExceptions.Length} erro(s)) — usando os que carregaram.");
            return ex.Types.Where(t => t is not null)!;
        }
    }

    private static bool InheritsFrom(Type type, string baseFullName)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
            if (t.FullName == baseFullName) return true;
        return false;
    }

    /// <summary>"ConcatFunction" → "Concat"; "CreateNFeAcessKeyFunction" → "CreateNFeAcessKey".
    /// Palpite só — o nome real usado em <c>F.Nome(...)</c> pode divergir (ver limitação).</summary>
    private static string GuessDslName(string className) =>
        className.EndsWith("Function", StringComparison.Ordinal)
            ? className[..^"Function".Length]
            : className;
}

file static class RuntimeEnvironment
{
    public static string GetRuntimeDirectory() => System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
}
