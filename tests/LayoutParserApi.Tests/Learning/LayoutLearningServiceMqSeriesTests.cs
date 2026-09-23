using LayoutParserApi.Services.Learning;
using LayoutParserApi.Services.Parsing.Implementations;
using LayoutParserApi.Tests.TestHelpers;

using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Learning;

/// <summary>
/// Cobre a regressão fix/mqseries-line-detection-601: arquivo MQSeries real é um stream
/// contínuo de largura fixa (600 chars por "linha" lógica), SEM terminador de linha (\r\n)
/// entre elas — só o layout XML/mapper sabe onde uma linha lógica termina e a próxima começa.
///
/// Antes da correção, <see cref="LayoutLearningService.LearnFromFileAsync"/> fazia
/// <c>content.Split('\r', '\n')</c> incondicionalmente, tratando o arquivo inteiro como uma
/// única "linha" gigante — resultado: <c>TotalLines=1</c>, <c>LineLength</c> = tamanho do
/// arquivo inteiro, <c>TotalFields=0</c> (evidência real: layout_learned.json de
/// LAY_TXT_MQSERIES_ENVNFE_4.00_NFe com LineLength=35400 para um arquivo de 59 linhas de 600).
/// </summary>
public sealed class LayoutLearningServiceMqSeriesTests : IDisposable
{
    private readonly string _tempFilePath = Path.Combine(Path.GetTempPath(), $"mqseries_fixture_{Guid.NewGuid():N}.mq_series");

    public void Dispose()
    {
        if (File.Exists(_tempFilePath))
            File.Delete(_tempFilePath);
    }

    /// <summary>
    /// Monta um fixture sintético reproduzindo o padrão real: 1ª linha "HEADER..." + N linhas de
    /// dado, cada uma começando com um contador sequencial de 9 dígitos ÚNICO por linha (não um
    /// prefixo textual repetido como "LINHA001") — é esse padrão que também derrubava o
    /// agrupamento por prefixo em <see cref="LayoutLearningService"/> mesmo após corrigir o split
    /// físico (ver DetectContinuousStreamGroups). Tudo concatenado SEM \r\n, largura fixa de 600.
    /// </summary>
    private static string BuildSyntheticMqSeriesStream(int dataLineCount)
    {
        const int lineLength = 600;
        var header = "HEADER20251607191020000G133".PadRight(lineLength);

        var sb = new System.Text.StringBuilder(header);
        for (var i = 1; i <= dataLineCount; i++)
        {
            var sequence = i.ToString("D6");
            var lineNumber = (i - 1).ToString("D3");
            var body = $"{sequence}{lineNumber}00000{i}CAMPO TEXTO EXEMPLO{i}";
            sb.Append(body.PadRight(lineLength));
        }

        return sb.ToString();
    }

    [Fact]
    public async Task LearnFromFileAsync_MqSeriesContinuousStream_SplitsIntoFixedWidthLines()
    {
        var content = BuildSyntheticMqSeriesStream(dataLineCount: 10);
        await File.WriteAllTextAsync(_tempFilePath, content);

        var service = new LayoutLearningService(
            NullLogger<LayoutLearningService>.Instance,
            new LineSplitter(new NoOpTechLogger()));

        var result = await service.LearnFromFileAsync(_tempFilePath, "mqseries");

        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.LearnedModel);

        // Regressão direta do bug: sem o split correto, isso seria 1 linha de 6600 chars.
        Assert.Equal(11, result.LearnedModel!.TotalLines); // 1 HEADER + 10 linhas de dado
        Assert.Equal(600, result.LearnedModel.LineLength);
    }

    [Fact]
    public async Task LearnFromFileAsync_MqSeriesContinuousStream_LearnsRealFields()
    {
        var content = BuildSyntheticMqSeriesStream(dataLineCount: 10);
        await File.WriteAllTextAsync(_tempFilePath, content);

        var service = new LayoutLearningService(
            NullLogger<LayoutLearningService>.Instance,
            new LineSplitter(new NoOpTechLogger()));

        var result = await service.LearnFromFileAsync(_tempFilePath, "mqseries");

        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.LearnedModel);

        // Regressão do segundo bug (agrupamento por prefixo literal): sequência única por linha
        // impedia >=3 linhas com o mesmo prefixo, então nenhum campo era aprendido mesmo com o
        // split físico já correto.
        Assert.True(result.LearnedModel!.TotalFields > 0, "MQSeries deveria aprender campos reais, não TotalFields=0.");
        Assert.NotEmpty(result.LearnedModel.Fields);
    }

    [Fact]
    public async Task LearnFromFileAsync_IdocRecordPerLine_ContinuesUsingNewlineSplit()
    {
        // Regressão negativa: IDOC (uma linha física por registro) não pode ser afetado pela
        // correção — continua usando split por \r\n.
        var content = string.Join("\n", new[]
        {
            "EDI_DC40 SEGMENTO_A CAMPO1",
            "EDI_DC40 SEGMENTO_A CAMPO2",
            "EDI_DC40 SEGMENTO_A CAMPO3",
        });
        await File.WriteAllTextAsync(_tempFilePath, content);

        var service = new LayoutLearningService(
            NullLogger<LayoutLearningService>.Instance,
            new LineSplitter(new NoOpTechLogger()));

        var result = await service.LearnFromFileAsync(_tempFilePath, "txt");

        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.LearnedModel);
        Assert.Equal(3, result.LearnedModel!.TotalLines);
    }
}
