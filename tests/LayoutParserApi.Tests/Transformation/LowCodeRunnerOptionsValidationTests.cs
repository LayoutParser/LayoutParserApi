using LayoutParserApi.Services.Transformation.LowCode;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Transformation
{
    /// <summary>
    /// Trava a regra de validação de arranque (A1, gate #107/#108): a mesma composição
    /// <c>AddOptions&lt;LowCodeRunnerOptions&gt;().Bind(...).Validate(...).ValidateOnStart()</c>
    /// registrada em Program.cs, exercitada aqui isoladamente (sem subir o host inteiro).
    ///
    /// Causa raiz que a validação fecha: <c>IOptions&lt;LowCodeRunnerOptions&gt;</c> cai nos
    /// defaults do C# sem erro nenhum quando a seção "LowCode" falta do appsettings/env vars —
    /// AllowedPackageGuids vira lista vazia e a query SQL de mapper vira IN (NULL), nunca batendo
    /// com nada. A regra é restrita de propósito: só falha quando a seção EXISTE e o campo está
    /// vazio — host sem low-code configurado (seção ausente) continua válido.
    /// </summary>
    public class LowCodeRunnerOptionsValidationTests
    {
        private static ServiceProvider BuildProvider(IConfiguration configuration)
        {
            var services = new ServiceCollection();
            services.AddSingleton(configuration);

            services.AddOptions<LowCodeRunnerOptions>()
                .Bind(configuration.GetSection("LowCode"))
                .Validate(opt =>
                {
                    var lowCodeSection = configuration.GetSection("LowCode");
                    if (!lowCodeSection.Exists())
                        return true;

                    return opt.AllowedPackageGuids is { Count: > 0 };
                }, "LowCode:AllowedPackageGuids esta vazio, mas a secao LowCode esta presente na configuracao.")
                .ValidateOnStart();

            return services.BuildServiceProvider();
        }

        /// <summary>
        /// (a) Seção "LowCode" presente + AllowedPackageGuids vazia → o arranque deve falhar.
        /// É exatamente o cenário do gate #107/#108: config parcial que hoje sobe silenciosa.
        /// </summary>
        [Fact]
        public void Startup_falha_quando_LowCode_presente_e_AllowedPackageGuids_vazio()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LowCode:RunnerPath"] = @"C:\algum\caminho\runner.exe",
                    ["LowCode:Package"] = "PKG123"
                    // AllowedPackageGuids deliberadamente ausente → lista vazia após bind
                })
                .Build();

            using var provider = BuildProvider(configuration);

            var ex = Assert.Throws<OptionsValidationException>(
                () => provider.GetRequiredService<IOptions<LowCodeRunnerOptions>>().Value);

            Assert.Contains("AllowedPackageGuids", ex.Message);
        }

        /// <summary>
        /// (b) Seção "LowCode" ausente por completo → NÃO deve falhar. Host sem low-code
        /// configurado é um cenário válido (parse/catálogo servem sem transformação).
        /// </summary>
        [Fact]
        public void Startup_nao_falha_quando_secao_LowCode_esta_totalmente_ausente()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OutraSecao:Chave"] = "valor"
                })
                .Build();

            using var provider = BuildProvider(configuration);

            var options = provider.GetRequiredService<IOptions<LowCodeRunnerOptions>>().Value;

            Assert.Empty(options.AllowedPackageGuids);
        }

        /// <summary>
        /// Caso feliz: seção presente e AllowedPackageGuids preenchida → arranque segue normalmente.
        /// </summary>
        [Fact]
        public void Startup_nao_falha_quando_AllowedPackageGuids_preenchido()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LowCode:RunnerPath"] = @"C:\algum\caminho\runner.exe",
                    ["LowCode:AllowedPackageGuids:0"] = "11111111-1111-1111-1111-111111111111"
                })
                .Build();

            using var provider = BuildProvider(configuration);

            var options = provider.GetRequiredService<IOptions<LowCodeRunnerOptions>>().Value;

            Assert.Single(options.AllowedPackageGuids);
        }
    }
}
