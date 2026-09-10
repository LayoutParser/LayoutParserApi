using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Database;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Generation;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Generation.Implementations;
using LayoutParserApi.Services.Generation.Interfaces;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Transformation.LowCode;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Controllers
{
    /// <summary>
    /// Issue #355: <c>POST /api/layouts/{layoutGuid}/generate-sample</c>. Cobre a pré-condição
    /// obrigatória de mapper existente (correção do dono no ADR
    /// docs/architecture/adr-geracao-documento-exemplo-2026-09-09.md), o caminho feliz
    /// TextPositional (reaproveitando <see cref="SyntheticDataGeneratorService"/>) e o
    /// not-implemented explícito para layout Xml (fica para a issue #356).
    /// </summary>
    public class LayoutsControllerGenerateSampleTests
    {
        private const string TextPositionalLayoutXml = @"<LayoutVO xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"">
  <LayoutGuid>LAY_teste-355</LayoutGuid>
  <LayoutType>TextPositional</LayoutType>
  <Name>LAYOUT_TESTE_355</Name>
  <Description>Layout de teste</Description>
  <LimitOfCaracters>20</LimitOfCaracters>
  <Elements>
    <Element xsi:type=""LineElementVO"">
      <ElementGuid>line-1</ElementGuid>
      <Name>Linha1</Name>
      <Sequence>1</Sequence>
      <IsRequired>true</IsRequired>
      <Elements>
        <Element xsi:type=""FieldElementVO"">
          <ElementGuid>field-1</ElementGuid>
          <Name>Nome</Name>
          <Sequence>1</Sequence>
          <IsRequired>true</IsRequired>
          <LengthField>20</LengthField>
          <AlignmentType>Left</AlignmentType>
        </Element>
      </Elements>
    </Element>
  </Elements>
</LayoutVO>";

        private const string XmlLayoutXml = @"<LayoutVO xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"">
  <LayoutGuid>LAY_teste-xml-355</LayoutGuid>
  <LayoutType>Xml</LayoutType>
  <Name>LAYOUT_XML_TESTE_355</Name>
  <Description>Layout XML de teste</Description>
  <Elements></Elements>
</LayoutVO>";

        private static LayoutsController BuildController(
            LayoutRecord? layoutRecord,
            Mapper? mapper,
            List<string>? allowedPackageGuids = null)
        {
            var fakeLayoutService = new FakeCachedLayoutService(layoutRecord);
            var fakeMapperDb = new FakeMapperDatabaseService(mapper);
            var dataGenerator = new SyntheticDataGeneratorService(NullLogger<SyntheticDataGeneratorService>.Instance);
            var options = Options.Create(new LowCodeRunnerOptions
            {
                ProjectId = 2,
                AllowedPackageGuids = allowedPackageGuids ?? new List<string> { "PAC_teste" }
            });

            return new LayoutsController(
                fakeLayoutService,
                fakeMapperDb,
                dataGenerator,
                options,
                NullLogger<LayoutsController>.Instance);
        }

        [Fact]
        public async Task GenerateSample_retorna_400_quando_layout_nao_existe()
        {
            var controller = BuildController(layoutRecord: null, mapper: null);

            var result = await controller.GenerateSample("guid-inexistente", new GenerateSampleRequest());

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task GenerateSample_retorna_404_quando_layout_existe_mas_sem_mapper()
        {
            var layoutRecord = new LayoutRecord
            {
                LayoutGuid = Guid.NewGuid(),
                Name = "LAYOUT_TESTE_355",
                LayoutType = "TextPositional",
                DecryptedContent = TextPositionalLayoutXml
            };
            var controller = BuildController(layoutRecord, mapper: null);

            var result = await controller.GenerateSample("teste-355", new GenerateSampleRequest());

            var notFound = Assert.IsType<NotFoundObjectResult>(result);
            Assert.NotNull(notFound.Value);
        }

        [Fact]
        public async Task GenerateSample_gera_documento_positional_quando_layout_TextPositional_tem_mapper()
        {
            var layoutRecord = new LayoutRecord
            {
                LayoutGuid = Guid.NewGuid(),
                Name = "LAYOUT_TESTE_355",
                LayoutType = "TextPositional",
                DecryptedContent = TextPositionalLayoutXml
            };
            var mapper = new Mapper { MapperGuid = "mapper-355", TargetLayoutGuid = "teste-355" };
            var controller = BuildController(layoutRecord, mapper);

            var result = await controller.GenerateSample("teste-355", new GenerateSampleRequest { NumberOfRecords = 2 });

            var ok = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<GenerateSampleResponse>(ok.Value);
            Assert.Equal("positional", response.Format);
            Assert.False(string.IsNullOrWhiteSpace(response.GeneratedDocument));
            Assert.NotEmpty(response.Warnings);
            // 2 registros de 1 linha cada, separados por \n.
            Assert.Equal(2, response.GeneratedDocument.Split('\n').Length);
        }

        [Fact]
        public async Task GenerateSample_avisa_quando_seed_e_ignorado()
        {
            var layoutRecord = new LayoutRecord
            {
                LayoutGuid = Guid.NewGuid(),
                Name = "LAYOUT_TESTE_355",
                LayoutType = "TextPositional",
                DecryptedContent = TextPositionalLayoutXml
            };
            var mapper = new Mapper { MapperGuid = "mapper-355", TargetLayoutGuid = "teste-355" };
            var controller = BuildController(layoutRecord, mapper);

            var result = await controller.GenerateSample("teste-355", new GenerateSampleRequest { Seed = 42 });

            var ok = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<GenerateSampleResponse>(ok.Value);
            Assert.Contains(response.Warnings, w => w.Contains("seed", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task GenerateSample_retorna_501_quando_layout_e_Xml()
        {
            var layoutRecord = new LayoutRecord
            {
                LayoutGuid = Guid.NewGuid(),
                Name = "LAYOUT_XML_TESTE_355",
                LayoutType = "Xml",
                DecryptedContent = XmlLayoutXml
            };
            var mapper = new Mapper { MapperGuid = "mapper-xml-355", TargetLayoutGuid = "teste-xml-355" };
            var controller = BuildController(layoutRecord, mapper);

            var result = await controller.GenerateSample("teste-xml-355", new GenerateSampleRequest());

            var objResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status501NotImplemented, objResult.StatusCode);
        }

        private sealed class FakeCachedLayoutService : ICachedLayoutService
        {
            private readonly LayoutRecord? _record;

            public FakeCachedLayoutService(LayoutRecord? record) => _record = record;

            public Task<LayoutSearchResponse> SearchLayoutsAsync(LayoutSearchRequest request) =>
                throw new NotSupportedException();

            public Task<LayoutRecord?> GetLayoutByIdAsync(int id) => throw new NotSupportedException();

            public Task<LayoutRecord?> GetLayoutByGuidAsync(string layoutGuid) => Task.FromResult(_record);

            public Task RefreshCacheFromDatabaseAsync() => Task.CompletedTask;

            public Task ClearCacheAsync() => Task.CompletedTask;

            public ILayoutDatabaseService GetLayoutDatabaseService() => throw new NotSupportedException();
        }

        /// <summary>
        /// <c>MapperDatabaseService</c> não tem interface própria — double por herança, no mesmo
        /// ponto de substituição de <c>GetRankedMapperCandidatesForLayoutGuidAsync</c>
        /// (ver TransformationExecutionControllerEndToEndAiCandidateTests).
        /// </summary>
        private sealed class FakeMapperDatabaseService : MapperDatabaseService
        {
            private readonly Mapper? _mapper;

            public FakeMapperDatabaseService(Mapper? mapper)
                : base(NullLogger<MapperDatabaseService>.Instance, new FakeDecryptionService(), new ConfigurationBuilder().Build())
            {
                _mapper = mapper;
            }

            public override Task<Mapper?> GetBestMapperForLayoutGuidAsync(string layoutGuid, int projectId, IReadOnlyCollection<string> allowedPackageGuids) =>
                Task.FromResult(_mapper);
        }

        private sealed class FakeDecryptionService : IDecryptionService
        {
            public Task<string> DecryptContentAsync(string encryptedContent) => Task.FromResult(encryptedContent);
            public bool IsDecryptorAvailable => true;
        }
    }
}
