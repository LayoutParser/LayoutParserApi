using System.Reflection;

using LayoutParserApi.Services.Database;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Database
{
    /// <summary>
    /// Regressão da migração de banco (correção completa do bug de FK cross-database investigado
    /// na PR #312): <see cref="SqlFiscalPackageStore"/>, <see cref="SqlMappingDraftStore"/> e
    /// <see cref="SqlMappingReleaseStore"/> deixaram de usar o banco compartilhado <c>Database:*</c>
    /// (ConnectUS_Macgyver) e passaram a usar o banco dedicado <c>IdentityDatabase:*</c>.
    /// </summary>
    /// <remarks>
    /// Como não há SQL Server disponível no ambiente de teste (nem um jeito rápido/determinístico de
    /// forçar uma <c>SqlException</c> com o hostname no texto — depende de resolução DNS/timeout de
    /// rede, que variou entre <c>SqlException</c> e <c>TaskCanceledException</c> nesta máquina), a
    /// verificação lê a connection string privada montada no construtor via reflection e confirma
    /// que ela foi montada a partir de <c>IdentityDatabase:*</c>, nunca de <c>Database:*</c>.
    /// </remarks>
    public class FiscalStoresUseIdentityDatabaseTests
    {
        private const string DatabaseHost = "host-database-legado-teste.invalid";
        private const string IdentityHost = "host-identity-dedicado-teste.invalid";

        private static IConfiguration BuildConfiguration()
        {
            var configValues = new Dictionary<string, string?>
            {
                // Host distinto e claramente rotulado — se o store usar esta config por engano, a
                // connection string lida via reflection vai conter "database-legado", não
                // "identity-dedicado".
                ["Database:Server"] = DatabaseHost,
                ["Database:Database"] = "ConnectUS_Macgyver",
                ["Database:UserId"] = "macgyver",
                ["Database:Password"] = "pwd-fiscal-legado",

                ["IdentityDatabase:Server"] = IdentityHost,
                ["IdentityDatabase:Database"] = "LayoutParserIdentity",
                ["IdentityDatabase:UserId"] = "identity-user",
                ["IdentityDatabase:Password"] = "pwd-identity",
            };
            return new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
        }

        private static string ReadConnectionString(object store)
        {
            var field = store.GetType().GetField("_connectionString", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            var value = field!.GetValue(store) as string;
            Assert.NotNull(value);
            return value!;
        }

        [Fact]
        public void SqlFiscalPackageStore_usa_IdentityDatabase_nao_Database()
        {
            var store = new SqlFiscalPackageStore(NullLogger<SqlFiscalPackageStore>.Instance, BuildConfiguration());
            var connectionString = ReadConnectionString(store);

            Assert.Contains(IdentityHost, connectionString, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(DatabaseHost, connectionString, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void SqlMappingDraftStore_usa_IdentityDatabase_nao_Database()
        {
            var store = new SqlMappingDraftStore(NullLogger<SqlMappingDraftStore>.Instance, BuildConfiguration());
            var connectionString = ReadConnectionString(store);

            Assert.Contains(IdentityHost, connectionString, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(DatabaseHost, connectionString, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void SqlMappingReleaseStore_usa_IdentityDatabase_nao_Database()
        {
            var store = new SqlMappingReleaseStore(NullLogger<SqlMappingReleaseStore>.Instance, BuildConfiguration());
            var connectionString = ReadConnectionString(store);

            Assert.Contains(IdentityHost, connectionString, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(DatabaseHost, connectionString, StringComparison.OrdinalIgnoreCase);
        }
    }
}
