using System.Text.RegularExpressions;

using LayoutParserApi.Services.Database;

namespace LayoutParserApi.Tests.Database
{
    /// <summary>
    /// Reproduz, sem precisar de um SQL Server real, o cenário do 503 investigado na PR #310
    /// ("MappingDraftStore falhou: FK ... references invalid table") — banco "vazio" onde a
    /// requisição bate primeiro no store DEPENDENTE (ex.: <c>SqlMappingDraftStore</c>) antes de
    /// qualquer request ter passado pelo store que cria a tabela referenciada
    /// (<c>SqlFiscalPackageStore</c>).
    /// </summary>
    /// <remarks>
    /// Em vez de fixar a ordem esperada como uma lista solta (que poderia divergir do DDL real e
    /// mascarar uma regressão), o teste PARSEIA o texto de <c>SchemaDdl</c> de cada store — extraindo
    /// toda tabela criada (<c>CREATE TABLE dbo.X</c>) e toda tabela referenciada
    /// (<c>REFERENCES dbo.Y</c>) — e valida que, na ordem em que
    /// <see cref="FiscalSchemaInitializer"/> concatena os DDLs (FiscalPackage → MappingDraft →
    /// MappingRelease), NENHUMA tabela é referenciada por FK antes de ter sido criada. Isso pega
    /// tanto a regressão original (ordem invertida) quanto uma tabela nova entrando na cadeia sem
    /// atualizar a ordem do initializer.
    /// </remarks>
    public class FiscalSchemaInitializerOrderTests
    {
        private static readonly Regex CreateTableRegex = new(@"CREATE TABLE dbo\.(\w+)", RegexOptions.Compiled);
        private static readonly Regex ReferencesRegex = new(@"REFERENCES dbo\.(\w+)\(", RegexOptions.Compiled);

        // Ordem real usada por FiscalSchemaInitializer.InitializeAsync — desde a migração para o
        // banco dedicado (IdentityDatabase:*), estes 3 stores não moram mais no ConnectUS_Macgyver.
        // Mantida em sincronia manualmente com o initializer; qualquer divergência de ordem é pega
        // pelo teste abaixo mesmo assim, porque a validação real é sobre "tabela criada antes de
        // referenciada", não sobre esta lista em si.
        private static readonly string[] SysmiddleDbDdlInOrder =
        {
            SqlFiscalPackageStore.SchemaDdl,
            SqlMappingDraftStore.SchemaDdl,
            SqlMappingReleaseStore.SchemaDdl
        };

        [Fact]
        public void Ordem_do_initializer_nunca_referencia_tabela_ainda_nao_criada()
        {
            var tabelasJaCriadas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var ddl in SysmiddleDbDdlInOrder)
            {
                // Dentro do MESMO bloco de DDL, uma tabela pode referenciar outra criada mais acima
                // no mesmo texto (ex.: tbFiscalMappingPackageRevision -> tbFiscalMappingPackage) —
                // isso é seguro porque o SQL Server executa o batch inteiro em ordem sequencial.
                // Simulamos isso processando o texto linha a linha, registrando CREATE TABLE assim
                // que aparece, antes de validar REFERENCES subsequentes.
                var linhas = ddl.Split('\n');
                foreach (var linha in linhas)
                {
                    var referencias = ReferencesRegex.Matches(linha);
                    foreach (Match referencia in referencias)
                    {
                        var tabelaReferenciada = referencia.Groups[1].Value;
                        Assert.True(
                            tabelasJaCriadas.Contains(tabelaReferenciada),
                            $"FK para dbo.{tabelaReferenciada} apareceria ANTES dessa tabela existir " +
                            "— mesma classe de bug do 503 investigado na PR #310. Ajuste a ordem em " +
                            "FiscalSchemaInitializer (e nesta lista de teste).");
                    }

                    var criacoes = CreateTableRegex.Matches(linha);
                    foreach (Match criacao in criacoes)
                        tabelasJaCriadas.Add(criacao.Groups[1].Value);
                }
            }

            // Sanidade: as 9 tabelas do grafo fiscal (Slice 2: tbFiscalProject, tbFiscalMappingPackage,
            // tbFiscalMappingPackageRevision, tbPackageArtifact; Slice 3: tbMappingDraft,
            // tbMappingDraftRule, tbMappingDraftRuleDecision; Slice 5: tbMappingRelease,
            // tbMappingTransition) realmente foram encontradas — um teste que passasse com a lista
            // vazia (ex.: regex quebrado por refactor) seria um falso positivo silencioso.
            Assert.Equal(9, tabelasJaCriadas.Count);
        }

        [Fact]
        public void tbFiscalWorkspace_nao_e_mais_referenciada_por_nenhum_store_do_banco_fiscal()
        {
            // Regressão do achado real: `dbo.tbFiscalWorkspace` nunca existiu no banco Database:*
            // (ConnectUS_Macgyver) — o workspace fiscal real (`tbLpFiscalWorkspace`) mora no banco
            // FISICAMENTE diferente `IdentityDatabase:*` (ver SqlIdentityWorkspaceStore), e FK entre
            // bancos distintos não é suportada pelo SQL Server. A FK foi removida das 4 tabelas que a
            // declaravam; este teste impede reintrodução acidental.
            foreach (var ddl in SysmiddleDbDdlInOrder)
                Assert.DoesNotContain("tbFiscalWorkspace", ddl);
        }

        [Fact]
        public void Schema_de_identidade_e_autossuficiente_sem_dependencia_do_banco_fiscal()
        {
            // SqlIdentityWorkspaceStore e SqlAiUserSessionStore vivem em IdentityDatabase:* — um SQL
            // Server fisicamente diferente do banco fiscal (Database:*). Toda FK declarada neles
            // precisa apontar para uma tabela criada no PRÓPRIO bloco, nunca para o outro banco.
            var identityDdls = new[] { SqlIdentityWorkspaceStore.SchemaDdl, SqlAiUserSessionStore.SchemaDdl };
            var tabelasDoBancoFiscal = new[]
            {
                "tbFiscalProject", "tbFiscalMappingPackage", "tbFiscalMappingPackageRevision",
                "tbPackageArtifact", "tbMappingDraft", "tbMappingDraftRule",
                "tbMappingDraftRuleDecision", "tbMappingRelease", "tbMappingTransition"
            };

            foreach (var ddl in identityDdls)
                foreach (var tabelaFiscal in tabelasDoBancoFiscal)
                    Assert.DoesNotContain(tabelaFiscal, ddl);
        }
    }
}
